using System.Security.Claims;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Control;
using HydroPilotWeb.Services.Anomalies;
using HydroPilotWeb.Services.Dashboard;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.SensorFaults;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Services.Control;

/// <summary>
/// Control de pH y CE por circuito (tanque). La vista Optimización pide dosis manuales y cambia el modo; en AUTO, el motor
/// (<see cref="ControlEngine"/>) evalúa el tanque cada vez que llegan lecturas y en cada barrido, y crea las dosis
/// él mismo. La Pi pide sus comandos y responde el resultado. El barrido vence lo que nadie tomó, mide el efecto
/// de cada dosis (autocalibra la bomba y pausa la magnitud tras varias dosis sin efecto) y guarda el nivel vigente
/// de cada lote. Las transiciones de estado son UPDATE condicionales, así un vencimiento, una cancelación y una
/// entrega simultáneos nunca se pisan; las evaluaciones del motor van de a una. Contrato y fases en
/// notas/control/plan-control-ph-ce.md.
/// </summary>
public sealed class ControlService
{
    private static readonly string[] OpenLotStatuses = [LotStatusNames.Activo, LotStatusNames.EnPausa];

    /// <summary>El motor y la medición de efectos corren de a uno (ingesta, barrido y vista comparten el gate).</summary>
    private static readonly SemaphoreSlim EvaluationGate = new(1, 1);

    /// <summary>Lecturas recientes que mira el motor (tendencia y lecturas posteriores a la mezcla).</summary>
    private const int RecentReadingsMinutes = 60;

    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly ControlOptions _options;
    private readonly DashboardOptions _dashboardOptions;
    private readonly IControlEventSink _events;
    private readonly ICommandChannel _channel;
    private readonly ILogger<ControlService> _logger;
    private readonly int _silenceFactor;

    public ControlService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        IOptions<ControlOptions> options,
        IOptions<DashboardOptions> dashboardOptions,
        IControlEventSink events,
        ICommandChannel channel,
        ILogger<ControlService> logger,
        IOptions<TelemetryOptions>? telemetryOptions = null)
    {
        _dbFactory = dbFactory;
        _options = options.Value;
        _dashboardOptions = dashboardOptions.Value;
        _events = events;
        _channel = channel;
        _logger = logger;
        _silenceFactor = telemetryOptions?.Value.DegradedIntervalFactor ?? SensorFaultRules.DefaultSilenceFactor;
    }

    /// <summary>Pedir y cancelar dosis, reanudar el control pausado: Administrador, Productor y Operador.</summary>
    public static bool CanDose(ClaimsPrincipal user) =>
        user.IsInRole("Administrador") || user.IsInRole("Productor") || user.IsInRole("Operador");

    /// <summary>Cambiar el modo y los datos del tanque: Administrador y Productor (igual que Parámetros).</summary>
    public static bool CanConfigure(ClaimsPrincipal user) =>
        user.IsInRole("Administrador") || user.IsInRole("Productor");

    private ControlEngine.Settings EngineSettings => new(
        Math.Max(1, _options.MixingMinutes),
        Math.Max(0, _options.DirectionChangeMinutes),
        Math.Max(1, _options.PollStaleMinutes),
        Math.Max(ControlEngine.TrendMinSpanMinutes, _options.TrendMinutes));

    // ------------------------------------------------------------------
    // Vista Control
    // ------------------------------------------------------------------

    /// <summary>Circuitos activos con su lote en curso (el desplegable de Optimización).</summary>
    public async Task<IReadOnlyList<ControlCircuitOption>> GetCircuitsAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var circuits = await context.Circuits.AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.GreenhouseId).ThenBy(c => c.Name)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(ct);
        var lots = await CurrentLotsAsync(context, circuits.Select(c => c.Id).ToList(), DateTime.UtcNow, ct);
        return circuits
            .Select(c => new ControlCircuitOption(c.Id, c.Name, lots.GetValueOrDefault(c.Id)?.Name))
            .ToList();
    }

    /// <summary>Circuito donde está plantado el lote (para abrir Optimización desde un lote).</summary>
    public async Task<int?> CircuitForLotAsync(int lotId, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        return await context.CircuitAssignments.AsNoTracking()
            .Where(a => a.LotId == lotId && a.ValidFromUtc <= now && (a.ValidUntilUtc == null || a.ValidUntilUtc > now))
            .OrderByDescending(a => a.ValidFromUtc)
            .Select(a => (int?)a.CircuitId)
            .FirstOrDefaultAsync(ct)
            ?? await context.Lots.AsNoTracking().Where(l => l.Id == lotId).Select(l => l.CircuitId).FirstOrDefaultAsync(ct);
    }

    public async Task<ControlViewDto?> GetViewAsync(int circuitId, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;

        var snapshot = await SnapshotAsync(context, circuitId, now, ct);
        if (snapshot is null)
            return null;

        var node = snapshot.Node;
        var circuit = snapshot.Circuit;
        // Todo lo que se ve es del lote en curso: lo anterior a su asignación (otro lote) no se muestra.
        var lotSince = snapshot.Lot is null ? null : await LotSinceAsync(context, circuitId, now, ct);
        var day = Later(now.AddHours(-24), lotSince);
        var dtos = snapshot.Commands.Select(c => ToDto(c.Command, c.Kind, c.Name)).ToList();

        var actuators = snapshot.Actuators
            .Select(a => new ControlActuatorDto(
                a.Id, a.Kind, a.Name, a.Unit, a.MaxPerCommand, a.MaxPerDay, a.IsActive,
                snapshot.Input.Pumps.Single(p => p.Id == a.Id).UsedLast24h,
                dtos.FirstOrDefault(c => c.ActuatorId == a.Id && ControlRules.IsInFlight(c.Status)),
                a.Kind == ControlContract.KindWater ? null : a.EffectPerUnit ?? ControlContract.DefaultFor(a.Kind)?.Effect,
                a.EffectSamples))
            .ToList();

        var lastDay = dtos.Where(c => c.CreatedAtUtc >= day).ToList();
        var kpis = new ControlKpis(
            lastDay.Count(c => c.Status == ControlContract.StatusExecuted),
            dtos.Count(c => ControlRules.IsInFlight(c.Status)),
            lastDay.Count(c => c.Status is ControlContract.StatusRejected or ControlContract.StatusFailed
                or ControlContract.StatusExpired or ControlContract.StatusUnconfirmed));

        var decision = ControlEngine.Decide(snapshot.Input);
        var status = new ControlStatusDto(
            decision.Outcome.ToString(), decision.Code, decision.Parameter, decision.Pump?.Name,
            decision.Amount, decision.Pump?.Unit, decision.Strength, decision.UntilUtc, decision.SensorFaultKind);

        // Conversación: lecturas de pH/CE, mensajes de comandos y pausas de las últimas TimelineHours.
        var timelineSince = Later(now.AddHours(-Math.Max(1, _options.TimelineHours)), lotSince);
        var readings = await UsableReadings(context, circuitId)
            .Where(r => r.ObservedAtUtc >= timelineSince)
            .OrderByDescending(r => r.ObservedAtUtc)
            .Take(5_000)
            .Select(r => new ControlRules.ReadingPoint(r.ObservedAtUtc, r.Sensor!.SensorType!.Name, r.Value))
            .ToListAsync(ct);
        var pauses = await context.ControlPauses.AsNoTracking()
            .Where(p => p.CircuitId == circuitId && (p.ResumedAtUtc == null || p.ResumedAtUtc >= timelineSince))
            .OrderByDescending(p => p.PausedAtUtc)
            .ToListAsync(ct);
        var pauseDtos = pauses.Select(ToDto).ToList();
        var timeline = ControlRules.BuildTimeline(
            readings,
            dtos.Where(c => c.CreatedAtUtc >= timelineSince),
            Math.Max(1, _options.TimelineMaxEntries),
            pauseDtos);

        // Ambiente del invernadero (sensores sin circuito): solo se muestra, no entra en el control.
        var temperature = await LatestAmbientAsync(context, circuit.GreenhouseId, AnomalyContract.SensorTypeTemperatura, ct);
        var humidity = await LatestAmbientAsync(context, circuit.GreenhouseId, AnomalyContract.SensorTypeHumedad, ct);

        return new ControlViewDto(
            circuit.Id,
            circuit.Name,
            node.Identifier,
            node.ConnectionState,
            Utc(node.LastCommandPollAtUtc),
            circuit.ControlMode,
            circuit.ReservoirLiters,
            circuit.SourceWaterEc,
            snapshot.Lot?.Id,
            snapshot.Lot is null ? null : LotName(snapshot.Lot.Id, snapshot.Lot.Name),
            snapshot.Lot?.AppliedPhenologicalStage?.Name,
            snapshot.Ph,
            snapshot.Ec,
            actuators,
            kpis,
            status,
            pauseDtos.Where(p => p.ResumedAtUtc is null).ToList(),
            timeline,
            PiContactStaleMinutes(node),
            snapshot.Recipe is { } r
                ? new ControlRecipeDto(r.PhOffset, r.EcOffset, r.HistoryPhOffset, r.HistoryEcOffset, r.Trust, r.SimilarLots,
                    r.ScoreTrend, r.Reason, Utc(r.UpdatedAtUtc))
                : null,
            temperature,
            humidity,
            lotSince);
    }

    private static async Task<ControlAmbientReading?> LatestAmbientAsync(
        HydroPilotDbContext context, int greenhouseId, string sensorType, CancellationToken ct)
    {
        var latest = await context.SensorReadings.AsNoTracking()
            .Where(TelemetryQualityPolicy.OperationallyUsableReading)
            .Where(r => r.Sensor!.CircuitId == null && r.Sensor.Node!.GreenhouseId == greenhouseId
                        && r.Sensor.SensorType!.Name == sensorType)
            .OrderByDescending(r => r.ObservedAtUtc)
            .Select(r => new { r.Value, r.ObservedAtUtc })
            .FirstOrDefaultAsync(ct);
        return latest is null ? null : new ControlAmbientReading(latest.Value, Utc(latest.ObservedAtUtc));
    }

    /// <summary>
    /// Pide una dosis manual: valida modo, bomba, límites (por dosis y 24 h) y que no haya otra en curso; guarda la
    /// lectura que la motivó, el nivel buscado y el efecto esperado. Queda PENDING hasta que la Pi la pida.
    /// </summary>
    public async Task<ControlActionResult> RequestDoseAsync(
        int circuitId,
        int actuatorId,
        decimal amount,
        string? requestedBy,
        CancellationToken ct = default)
    {
        amount = Math.Round(amount, 2);
        var now = DateTime.UtcNow;
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var circuit = await context.Circuits.AsNoTracking().Include(c => c.Node).FirstOrDefaultAsync(c => c.Id == circuitId, ct);
        if (circuit is null)
            return ControlActionResult.Fail("El circuito no existe.");
        var actuator = await context.Actuators.AsNoTracking().FirstOrDefaultAsync(a => a.Id == actuatorId && a.CircuitId == circuitId, ct);
        if (actuator is null)
            return ControlActionResult.Fail("La bomba no existe en este circuito.");

        // Lo vencido deja de estar "en curso" antes de validar.
        await ExpireAsync(context, now, circuitId: circuitId, ct: ct);

        var recent = await context.ActuatorCommands.AsNoTracking()
            .Where(c => c.ActuatorId == actuatorId
                        && (c.CreatedAtUtc >= now.AddHours(-24)
                            || c.Status == ControlContract.StatusPending
                            || c.Status == ControlContract.StatusDelivered))
            .Select(c => new { c.Status, c.Amount, c.ExecutedAmount, c.CreatedAtUtc })
            .ToListAsync(ct);
        var used = recent
            .Where(c => c.CreatedAtUtc >= now.AddHours(-24))
            .Sum(c => ControlRules.UsedAmount(c.Status, c.Amount, c.ExecutedAmount));

        var error = ControlRules.ValidateManualDose(
            circuit.ControlMode, actuator.IsActive, recent.Any(c => ControlRules.IsInFlight(c.Status)),
            amount, actuator.MaxPerCommand, actuator.MaxPerDay, used, actuator.Unit);
        if (error is not null)
            return ControlActionResult.Fail(error);

        var parameter = ControlRules.ParameterFor(actuator.Kind);
        var trigger = await LatestReadingWithIdAsync(context, circuitId, parameter, ct);
        var lot = await LotWithBandsAsync(context, circuitId, now, ct);
        var recipe = lot is null ? null : await RecipeService.CurrentAsync(context, lot.Id, ct);
        var band = BandFor(lot, recipe, parameter);
        var expected = trigger is null
            ? (decimal?)null
            : ControlEngine.ExpectedEffect(actuator.Kind, amount, circuit.ReservoirLiters, EffectOf(actuator), trigger.Value, circuit.SourceWaterEc);

        var created = await AddCommandAsync(context, new ActuatorCommand
        {
            Id = Guid.NewGuid(),
            NodeId = circuit.NodeId,
            CircuitId = circuitId,
            ActuatorId = actuatorId,
            LotId = lot?.Id,
            Source = ControlContract.SourceManual,
            RequestedBy = requestedBy,
            Amount = amount,
            Unit = actuator.Unit,
            Status = ControlContract.StatusPending,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(Math.Max(1, _options.CommandTtlMinutes)),
            Parameter = parameter,
            TriggerReadingId = trigger?.Id,
            ValueBefore = trigger?.Value,
            BandMin = band.Min,
            Target = band.Target,
            BandMax = band.Max,
            StageId = lot?.AppliedPhenologicalStageId,
            ControllerVersion = ControlContract.ControllerManual,
            ExpectedEffect = expected,
        }, ct);
        if (!created)
            return ControlActionResult.Fail("Ya hay una dosis en curso para esta bomba.");

        _logger.LogInformation("Dosis manual pedida en {Circuit}: {Actuator} {Amount} {Unit} por {User}.",
            circuit.Name, actuator.TechnicalKey, amount, actuator.Unit, requestedBy);
        return ControlActionResult.Ok();
    }

    /// <summary>Cancela una dosis que la Pi todavía no pidió.</summary>
    public async Task<ControlActionResult> CancelAsync(int circuitId, Guid commandId, string? user, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await context.ActuatorCommands
            .Where(c => c.Id == commandId && c.CircuitId == circuitId && c.Status == ControlContract.StatusPending
                        && c.DispatchedAtUtc == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, ControlContract.StatusCancelled)
                .SetProperty(c => c.CompletedAtUtc, now), ct);
        if (rows == 0)
            return ControlActionResult.Fail("La Pi ya la tomó (o ya se le mandó): no se puede cancelar.");

        _logger.LogInformation("Dosis {CommandId} cancelada por {User}.", commandId, user);
        return ControlActionResult.Ok();
    }

    public async Task<ControlActionResult> SetReservoirAsync(int circuitId, decimal liters, string? user, CancellationToken ct = default)
    {
        liters = Math.Round(liters, 1);
        if (liters <= 0m || liters > ControlContract.MaxReservoirLiters)
            return ControlActionResult.Fail($"El tanque tiene que tener entre 1 y {ControlContract.MaxReservoirLiters:0} litros.");

        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await context.Circuits
            .Where(c => c.Id == circuitId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ReservoirLiters, liters), ct);
        if (rows == 0)
            return ControlActionResult.Fail("El circuito no existe.");

        _logger.LogInformation("Tanque del circuito {CircuitId}: {Liters} L (por {User}).", circuitId, liters, user);
        return ControlActionResult.Ok();
    }

    /// <summary>Manual o Automático. Al pasar a Automático, el motor evalúa el tanque en el momento.</summary>
    public async Task<ControlActionResult> SetModeAsync(int circuitId, string mode, string? user, CancellationToken ct = default)
    {
        if (mode is not (ControlContract.ModeManual or ControlContract.ModeAuto or ControlContract.ModeOff))
            return ControlActionResult.Fail("Modo desconocido.");

        await using (var context = await _dbFactory.CreateDbContextAsync(ct))
        {
            var rows = await context.Circuits
                .Where(c => c.Id == circuitId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ControlMode, mode), ct);
            if (rows == 0)
                return ControlActionResult.Fail("El circuito no existe.");
        }

        _logger.LogInformation("Modo de control del circuito {CircuitId}: {Mode} (por {User}).", circuitId, mode, user);
        if (mode == ControlContract.ModeAuto)
            await EvaluateCircuitAsync(circuitId, DateTime.UtcNow, ct);
        return ControlActionResult.Ok();
    }

    /// <summary>Reanuda el control automático de una magnitud pausada (después de revisar bidón, bomba y sonda).</summary>
    public async Task<ControlActionResult> ResumeAsync(int circuitId, string parameter, string? user, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using (var context = await _dbFactory.CreateDbContextAsync(ct))
        {
            var rows = await context.ControlPauses
                .Where(p => p.CircuitId == circuitId && p.Parameter == parameter && p.ResumedAtUtc == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.ResumedAtUtc, now)
                    .SetProperty(p => p.ResumedBy, user), ct);
            if (rows == 0)
                return ControlActionResult.Fail("No estaba pausado.");
        }

        await _events.OnResumedAsync(circuitId, parameter, ct);
        _logger.LogInformation("Control de {Parameter} del circuito {CircuitId} reanudado por {User}.", parameter, circuitId, user);
        await EvaluateCircuitAsync(circuitId, DateTime.UtcNow, ct);
        return ControlActionResult.Ok();
    }

    // ------------------------------------------------------------------
    // Motor
    // ------------------------------------------------------------------

    /// <summary>
    /// Evalúa el tanque de un circuito en AUTO y, si corresponde, crea la dosis. La llama la ingesta (vía
    /// <see cref="ControlWorker"/>) al llegar lecturas de pH/CE, el barrido y los cambios de modo.
    /// </summary>
    public async Task<ControlEngine.Decision> EvaluateCircuitAsync(int circuitId, DateTime nowUtc, CancellationToken ct = default)
    {
        await EvaluationGate.WaitAsync(ct);
        try
        {
            await using var context = await _dbFactory.CreateDbContextAsync(ct);
            var mode = await context.Circuits.AsNoTracking()
                .Where(c => c.Id == circuitId)
                .Select(c => c.ControlMode)
                .FirstOrDefaultAsync(ct);
            if (mode != ControlContract.ModeAuto)
                return new ControlEngine.Decision(ControlEngine.Outcome.Off, ControlEngine.Codes.Manual);

            await ExpireAsync(context, nowUtc, circuitId: circuitId, ct: ct);
            var snapshot = await SnapshotAsync(context, circuitId, nowUtc, ct);
            if (snapshot is null)
                return new ControlEngine.Decision(ControlEngine.Outcome.Off, ControlEngine.Codes.Manual);

            var decision = ControlEngine.Decide(snapshot.Input);
            if (decision.Outcome == ControlEngine.Outcome.Dose)
                await CreateAutoDoseAsync(context, snapshot, decision, nowUtc, ct);
            return decision;
        }
        finally
        {
            EvaluationGate.Release();
        }
    }

    private async Task CreateAutoDoseAsync(
        HydroPilotDbContext context, CircuitSnapshot snapshot, ControlEngine.Decision decision, DateTime nowUtc, CancellationToken ct)
    {
        var pump = decision.Pump!;
        var parameter = decision.Parameter!;
        var trigger = await LatestReadingWithIdAsync(context, snapshot.Circuit.Id, parameter, ct);
        var band = BandFor(snapshot.Lot, snapshot.Recipe, parameter);

        var created = await AddCommandAsync(context, new ActuatorCommand
        {
            Id = Guid.NewGuid(),
            NodeId = snapshot.Circuit.NodeId,
            CircuitId = snapshot.Circuit.Id,
            ActuatorId = pump.Id,
            LotId = snapshot.Lot?.Id,
            Source = ControlContract.SourceAuto,
            Amount = decision.Amount,
            Unit = pump.Unit,
            Status = ControlContract.StatusPending,
            CreatedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc.AddMinutes(Math.Max(1, _options.CommandTtlMinutes)),
            Parameter = parameter,
            TriggerReadingId = trigger?.Id,
            ValueBefore = trigger?.Value ?? decision.Value,
            BandMin = band.Min,
            Target = band.Target,
            BandMax = band.Max,
            StageId = snapshot.Lot?.AppliedPhenologicalStageId,
            ControllerVersion = ControlContract.ControllerAuto,
            Strength = decision.Strength,
            ExpectedEffect = decision.ExpectedEffect,
        }, ct);

        if (created)
            _logger.LogInformation(
                "Dosis automática en {Circuit}: {Pump} {Amount} {Unit} ({Parameter} {Value} → {Target}, fuerza {Strength:P0}).",
                snapshot.Circuit.Name, pump.Kind, decision.Amount, pump.Unit, parameter, decision.Value, decision.Target, decision.Strength);
    }

    /// <summary>Puntos por magnitud como mucho en la tendencia (las lecturas se promedian por tramo).</summary>
    public const int TrendMaxPoints = 180;

    /// <summary>
    /// pH y CE del nodo en las últimas <paramref name="hours"/> horas: lecturas promediadas por tramo, la banda del lote
    /// (etapa + receta), la banda en la que actúa el motor y las dosis de la ventana. Null si el nodo no existe.
    /// </summary>
    public async Task<ControlTrendDto?> GetTrendAsync(int circuitId, int hours, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        if (!await context.Circuits.AnyAsync(c => c.Id == circuitId, ct))
            return null;

        var from = now.AddHours(-Math.Clamp(hours, 1, 72));
        var lot = await LotWithBandsAsync(context, circuitId, now, ct);
        var recipe = lot is null ? null : await RecipeService.CurrentAsync(context, lot.Id, ct);
        // Solo el lote en curso: lo de antes de su asignación (otro lote) no se dibuja.
        var since = Later(from, lot is null ? null : await LotSinceAsync(context, circuitId, now, ct));

        var readings = await UsableReadings(context, circuitId)
            .Where(r => r.ObservedAtUtc >= since && r.ObservedAtUtc <= now)
            .OrderBy(r => r.ObservedAtUtc)
            .Select(r => new { r.ObservedAtUtc, Type = r.Sensor!.SensorType!.Name, r.Value })
            .ToListAsync(ct);
        var doses = await context.ActuatorCommands.AsNoTracking()
            .Where(c => c.CircuitId == circuitId && c.CreatedAtUtc >= since)
            .OrderBy(c => c.CreatedAtUtc)
            .Select(c => new
            {
                c.CompletedAtUtc, c.CreatedAtUtc, c.Parameter, c.Actuator!.Kind, c.Actuator.Name, c.Amount, c.ExecutedAmount, c.Unit,
                c.Status, c.Source, c.Strength, c.Effective,
            })
            .ToListAsync(ct);

        var bucket = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks, (now - from).Ticks / TrendMaxPoints));
        ControlTrendSeries Series(string parameter)
        {
            var points = readings
                .Where(r => r.Type == parameter)
                .GroupBy(r => r.ObservedAtUtc.Ticks / bucket.Ticks)
                .Select(g => new ControlTrendPoint(Utc(g.Last().ObservedAtUtc), Math.Round(g.Average(r => r.Value), 3)))
                .ToList();
            var band = BandFor(lot, recipe, parameter);
            return new ControlTrendSeries(
                parameter,
                points,
                band,
                ControlEngine.ActionBand(band),
                doses.Where(d => d.Parameter == parameter)
                    .Select(d => new ControlTrendDose(Utc(d.CompletedAtUtc ?? d.CreatedAtUtc), d.Parameter, d.Kind, d.Name,
                        d.ExecutedAmount ?? d.Amount, d.Unit, d.Status, d.Source, d.Strength, d.Effective))
                    .ToList());
        }

        return new ControlTrendDto(from, now, Series(ControlContract.ParameterPh), Series(ControlContract.ParameterEc));
    }

    // ------------------------------------------------------------------
    // Raspberry Pi
    // ------------------------------------------------------------------

    /// <summary>
    /// Comandos que la Pi tiene que ejecutar: los pendientes pasan a entregados, y los entregados sin resultado se
    /// vuelven a mandar (la Pi no repite un commandId). Lo vencido no se entrega nunca. Null si el nodo no existe.
    /// </summary>
    public async Task<IReadOnlyList<DeviceCommandDto>?> PollAsync(string nodeIdentifier, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var node = await context.IotNodes.FirstOrDefaultAsync(n => n.Identifier == nodeIdentifier, ct);
        if (node is null)
            return null;

        await ExpireAsync(context, nowUtc, nodeId: node.Id, ct: ct);

        await context.ActuatorCommands
            .Where(c => c.NodeId == node.Id && c.Status == ControlContract.StatusPending && c.ExpiresAtUtc > nowUtc)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, ControlContract.StatusDelivered)
                .SetProperty(c => c.DeliveredAtUtc, nowUtc), ct);

        var commands = await context.ActuatorCommands.AsNoTracking()
            .Where(c => c.NodeId == node.Id && c.Status == ControlContract.StatusDelivered && c.ExpiresAtUtc > nowUtc)
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync(ct);

        node.LastCommandPollAtUtc = nowUtc;
        await context.SaveChangesAsync(ct);

        return await ToDeviceCommandsAsync(context, commands, ct);
    }

    /// <summary>
    /// IoT Hub: cualquier mensaje del dispositivo cuenta como señal de la Pi (lo mismo que su consulta HTTP de
    /// comandos), para que el motor sepa que hay quién ejecute las dosis.
    /// </summary>
    public async Task MarkDeviceContactAsync(string nodeIdentifier, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        await context.IotNodes
            .Where(n => n.Identifier == nodeIdentifier)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.LastCommandPollAtUtc, nowUtc), ct);
    }

    /// <summary>
    /// Acuse de IoT Hub de un comando C2D: la Pi lo recibió (PENDING → DELIVERED) o lo rechazó (→ REJECTED). Lo
    /// vencido o purgado no cambia acá: lo cierra el barrido.
    /// </summary>
    public async Task ApplyDeliveryFeedbackAsync(Guid commandId, DeliveryFeedback feedback, DateTime atUtc, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        switch (feedback)
        {
            case DeliveryFeedback.Delivered:
                await context.ActuatorCommands
                    .Where(c => c.Id == commandId && c.Status == ControlContract.StatusPending)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.Status, ControlContract.StatusDelivered)
                        .SetProperty(c => c.DeliveredAtUtc, atUtc), ct);
                break;
            case DeliveryFeedback.Rejected:
                await context.ActuatorCommands
                    .Where(c => c.Id == commandId
                                && (c.Status == ControlContract.StatusPending || c.Status == ControlContract.StatusDelivered))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.Status, ControlContract.StatusRejected)
                        .SetProperty(c => c.ResultError, "La Pi rechazó el mensaje (IoT Hub).")
                        .SetProperty(c => c.CompletedAtUtc, now), ct);
                break;
        }
    }

    /// <summary>Registra el resultado que informa la Pi (idempotente por commandId).</summary>
    public async Task<DeviceResultReport> ReportResultAsync(
        string nodeIdentifier,
        Guid commandId,
        DeviceResultRequest request,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var nodeId = await context.IotNodes.AsNoTracking()
            .Where(n => n.Identifier == nodeIdentifier)
            .Select(n => (int?)n.Id)
            .FirstOrDefaultAsync(ct);
        if (nodeId is null)
            return new(DeviceResultOutcome.NodeNotFound, null, $"Nodo '{nodeIdentifier}' no registrado.");

        var command = await context.ActuatorCommands.FirstOrDefaultAsync(c => c.Id == commandId && c.NodeId == nodeId, ct);
        if (command is null)
            return new(DeviceResultOutcome.CommandNotFound, null, $"Comando '{commandId}' no existe para el nodo.");

        var result = new ControlRules.DeviceResult(
            request.Status,
            request.ExecutedAmount is { } executed ? Math.Round(executed, 2) : null,
            request.StartedAt?.UtcDateTime,
            request.FinishedAt?.UtcDateTime,
            request.Error);
        var check = ControlRules.CheckResult(command.Status, command.Amount, command.ExecutedAmount, result);

        switch (check.Outcome)
        {
            case ControlRules.ResultOutcome.Invalid:
                return new(DeviceResultOutcome.Invalid, null, check.Message);
            case ControlRules.ResultOutcome.Conflict:
                _logger.LogWarning("Resultado en conflicto para {CommandId} ({Node}): {Message}", commandId, nodeIdentifier, check.Message);
                return new(DeviceResultOutcome.Conflict, command.Status, check.Message);
            case ControlRules.ResultOutcome.Duplicate:
                return new(DeviceResultOutcome.Duplicate, command.Status, null);
        }

        ControlRules.ApplyResult(command, check, result, nowUtc);
        await context.SaveChangesAsync(ct);

        _logger.LogInformation("Resultado de {CommandId} ({Node}): {Status} {Executed} {Unit}.",
            commandId, nodeIdentifier, command.Status, command.ExecutedAmount, command.Unit);
        return new(DeviceResultOutcome.Applied, command.Status, null);
    }

    // ------------------------------------------------------------------
    // Barrido
    // ------------------------------------------------------------------

    /// <summary>
    /// Vence comandos, mide el efecto de las dosis ya mezcladas (autocalibra y pausa), registra el nivel vigente
    /// de cada lote en curso cuando cambia y evalúa los circuitos en AUTO.
    /// </summary>
    public async Task<ControlSweepResult> SweepAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        int expired, unconfirmed, setpoints, measured, paused;
        List<int> autoCircuits;

        await EvaluationGate.WaitAsync(ct);
        try
        {
            await using var context = await _dbFactory.CreateDbContextAsync(ct);
            (expired, unconfirmed) = await ExpireAsync(context, nowUtc, ct: ct);
            if (_channel.PushesCommands)
            {
                // IoT Hub: lo que no se pudo mandar al crearse (hub caído) se reintenta mientras no venza.
                var undispatched = await context.ActuatorCommands.AsNoTracking()
                    .Where(c => c.Status == ControlContract.StatusPending && c.DispatchedAtUtc == null && c.ExpiresAtUtc > nowUtc)
                    .ToListAsync(ct);
                await DispatchAsync(context, undispatched, nowUtc, ct);
            }
            (measured, paused) = await MeasureEffectsAsync(context, nowUtc, ct);
            setpoints = await RecordSetpointsAsync(context, nowUtc, ct);
            autoCircuits = await context.Circuits.AsNoTracking()
                .Where(c => c.IsActive && c.ControlMode == ControlContract.ModeAuto)
                .Select(c => c.Id)
                .ToListAsync(ct);
        }
        finally
        {
            EvaluationGate.Release();
        }

        var doses = 0;
        foreach (var circuitId in autoCircuits)
        {
            if ((await EvaluateCircuitAsync(circuitId, nowUtc, ct)).Outcome == ControlEngine.Outcome.Dose)
                doses++;
        }

        if (expired + unconfirmed + setpoints + measured + paused + doses > 0)
            _logger.LogInformation(
                "Barrido de control: {Expired} vencidos, {Unconfirmed} sin confirmar, {Measured} efectos medidos, {Paused} pausas, {Doses} dosis automáticas, {Setpoints} niveles nuevos.",
                expired, unconfirmed, measured, paused, doses, setpoints);
        return new ControlSweepResult(expired, unconfirmed, setpoints, measured, paused, doses);
    }

    /// <summary>
    /// Pendiente y vencido → EXPIRED (nunca se ejecuta). Entregado sin resultado pasado el vencimiento más el
    /// margen → UNCONFIRMED (pudo haberse aplicado; si después llega el resultado, se acepta).
    /// </summary>
    private async Task<(int Expired, int Unconfirmed)> ExpireAsync(
        HydroPilotDbContext context, DateTime nowUtc, int? nodeId = null, int? circuitId = null, CancellationToken ct = default)
    {
        var commands = context.ActuatorCommands.AsQueryable();
        if (nodeId is { } id)
            commands = commands.Where(c => c.NodeId == id);
        if (circuitId is { } circuit)
            commands = commands.Where(c => c.CircuitId == circuit);

        var expired = await commands
            .Where(c => c.Status == ControlContract.StatusPending && c.ExpiresAtUtc <= nowUtc)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, ControlContract.StatusExpired)
                .SetProperty(c => c.CompletedAtUtc, nowUtc), ct);

        var cutoff = nowUtc.AddMinutes(-Math.Max(0, _options.UnconfirmedGraceMinutes));
        var unconfirmed = await commands
            .Where(c => c.Status == ControlContract.StatusDelivered && c.ExpiresAtUtc <= cutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, ControlContract.StatusUnconfirmed)
                .SetProperty(c => c.CompletedAtUtc, nowUtc), ct);

        return (expired, unconfirmed);
    }

    /// <summary>
    /// Efecto de cada dosis aplicada cuya mezcla terminó: primera lectura después de la mezcla contra la lectura
    /// que la motivó, descontando la deriva previa. Con efecto, recalibra la bomba; si las últimas
    /// <see cref="ControlOptions.NoEffectDoses"/> dosis de una magnitud no tuvieron efecto, la pausa y avisa.
    /// </summary>
    private async Task<(int Measured, int Paused)> MeasureEffectsAsync(HydroPilotDbContext context, DateTime nowUtc, CancellationToken ct)
    {
        var settings = EngineSettings;
        var ready = nowUtc.AddMinutes(-settings.MixingMinutes);
        var pending = await context.ActuatorCommands
            .Include(c => c.Actuator)
            .Include(c => c.Circuit)
            .Where(c => c.EffectEvaluatedAtUtc == null && c.CompletedAtUtc != null && c.CompletedAtUtc <= ready
                        && (c.Status == ControlContract.StatusExecuted
                            || (c.Status == ControlContract.StatusFailed && c.ExecutedAmount > 0)))
            .OrderBy(c => c.CompletedAtUtc)
            .Take(200)
            .ToListAsync(ct);
        if (pending.Count == 0)
            return (0, 0);

        var touched = new HashSet<(int CircuitId, string Parameter)>();
        var measured = 0;
        foreach (var command in pending)
        {
            var actuator = command.Actuator!;
            var circuit = command.Circuit!;
            var amount = command.ExecutedAmount ?? command.Amount;
            var settled = command.CompletedAtUtc!.Value.AddMinutes(settings.MixingMinutes);

            if (amount <= 0m || command.ValueBefore is not { } before)
            {
                command.EffectEvaluatedAtUtc = nowUtc; // sin lectura previa: no se puede medir
                continue;
            }

            var after = await UsableReadings(context, command.CircuitId, command.Parameter)
                .Where(r => r.ObservedAtUtc >= settled)
                .OrderBy(r => r.ObservedAtUtc)
                .Select(r => new { r.Value, r.ObservedAtUtc })
                .FirstOrDefaultAsync(ct);
            if (after is null)
            {
                if (nowUtc > settled.AddMinutes(Math.Max(1, _options.EffectWaitMinutes)))
                    command.EffectEvaluatedAtUtc = nowUtc; // sin lectura posterior: queda sin medir
                continue;
            }

            var beforeAt = command.TriggerReadingId is { } triggerId
                ? await context.SensorReadings.AsNoTracking()
                    .Where(r => r.Id == triggerId)
                    .Select(r => (DateTime?)r.ObservedAtUtc)
                    .FirstOrDefaultAsync(ct) ?? command.CreatedAtUtc
                : command.CreatedAtUtc;
            var drift = ControlEngine.TrendPerHour(await UsableReadings(context, command.CircuitId, command.Parameter)
                .Where(r => r.ObservedAtUtc >= beforeAt.AddMinutes(-settings.TrendMinutes) && r.ObservedAtUtc <= beforeAt)
                .Select(r => new ControlEngine.Point(r.ObservedAtUtc, r.Value))
                .ToListAsync(ct));

            var expected = command.ExpectedEffect
                ?? ControlEngine.ExpectedEffect(actuator.Kind, amount, circuit.ReservoirLiters, EffectOf(actuator), before, circuit.SourceWaterEc);
            var hours = (decimal)(after.ObservedAtUtc - beforeAt).TotalHours;
            var measure = ControlEngine.MeasureEffect(before, after.Value, drift, hours, expected);

            command.ExpectedEffect = expected;
            command.ValueAfter = after.Value;
            command.ObservedEffect = measure.Observed;
            command.Effective = measure.Effective;
            command.EffectEvaluatedAtUtc = nowUtc;
            measured++;
            touched.Add((command.CircuitId, command.Parameter));

            if (measure.Effective
                && ControlEngine.Calibrate(actuator.Kind, actuator.EffectPerUnit, measure.Observed, amount, circuit.ReservoirLiters) is { } calibrated
                && calibrated != actuator.EffectPerUnit)
            {
                actuator.EffectPerUnit = calibrated;
                actuator.EffectSamples++;
            }
        }
        await context.SaveChangesAsync(ct);

        var paused = 0;
        foreach (var (circuitId, parameter) in touched)
        {
            if (await PauseIfNoEffectAsync(context, circuitId, parameter, nowUtc, ct))
                paused++;
        }
        return (measured, paused);
    }

    private async Task<bool> PauseIfNoEffectAsync(
        HydroPilotDbContext context, int circuitId, string parameter, DateTime nowUtc, CancellationToken ct)
    {
        var doses = Math.Max(1, _options.NoEffectDoses);
        if (await context.ControlPauses.AnyAsync(p => p.CircuitId == circuitId && p.Parameter == parameter && p.ResumedAtUtc == null, ct))
            return false;

        // Solo cuentan las dosis posteriores a la última reanudación: si no, la misma racha volvería a pausar.
        var lastResume = await context.ControlPauses
            .Where(p => p.CircuitId == circuitId && p.Parameter == parameter && p.ResumedAtUtc != null)
            .MaxAsync(p => p.ResumedAtUtc, ct);
        var latest = await context.ActuatorCommands.AsNoTracking()
            .Where(c => c.CircuitId == circuitId && c.Parameter == parameter && c.Effective != null
                        && (lastResume == null || c.CompletedAtUtc > lastResume))
            .OrderByDescending(c => c.CompletedAtUtc)
            .Take(doses)
            .Select(c => c.Effective!.Value)
            .ToListAsync(ct);
        if (!ControlEngine.ShouldPause(latest, doses))
            return false;

        var reason = $"{doses} dosis seguidas sin efecto";
        context.ControlPauses.Add(new ControlPause { CircuitId = circuitId, Parameter = parameter, PausedAtUtc = nowUtc, Reason = reason });
        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return false; // otra evaluación ya la pausó
        }

        var name = await context.Circuits.AsNoTracking().Where(c => c.Id == circuitId).Select(c => c.Name).SingleAsync(ct);
        var lotId = (await CurrentLotsAsync(context, [circuitId], nowUtc, ct)).GetValueOrDefault(circuitId)?.LotId;
        _logger.LogWarning("Control de {Parameter} del circuito {Circuit} pausado: {Reason}.", parameter, name, reason);
        await _events.OnPausedAsync(new ControlPauseEvent(circuitId, name, lotId, parameter, reason, nowUtc), ct);
        return true;
    }

    private static async Task<int> RecordSetpointsAsync(HydroPilotDbContext context, DateTime nowUtc, CancellationToken ct)
    {
        var lots = await context.Lots.AsNoTracking()
            .Include(l => l.CropType)
            .Include(l => l.AppliedPhenologicalStage)
            .Where(l => l.Status != null && OpenLotStatuses.Contains(l.Status.Name))
            .ToListAsync(ct);
        if (lots.Count == 0)
            return 0;

        var ids = lots.Select(l => l.Id).ToList();
        var recipes = await context.LotRecipes.AsNoTracking()
            .Where(r => ids.Contains(r.LotId))
            .ToDictionaryAsync(r => r.LotId, ct);
        var lastByLot = (await context.LotSetpointHistories.AsNoTracking()
                .Where(h => ids.Contains(h.LotId))
                .ToListAsync(ct))
            .GroupBy(h => h.LotId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.ValidFromUtc).ThenByDescending(h => h.Id).First());

        var added = 0;
        foreach (var lot in lots)
        {
            var recipe = recipes.GetValueOrDefault(lot.Id);
            var ph = BandFor(lot, recipe, ControlContract.ParameterPh);
            var ec = BandFor(lot, recipe, ControlContract.ParameterEc);
            var phOffset = OffsetApplied(SolutionRules.PhBand(lot.CropType), ph);
            var ecOffset = OffsetApplied(SolutionRules.EcBand(lot.AppliedPhenologicalStage, lot.CropType), ec);
            var current = new LotSetpointHistory
            {
                LotId = lot.Id,
                ValidFromUtc = nowUtc,
                PhMin = ph.Min,
                PhTarget = ph.Target,
                PhMax = ph.Max,
                EcMin = ec.Min,
                EcTarget = ec.Target,
                EcMax = ec.Max,
                PhOffset = phOffset,
                EcOffset = ecOffset,
                Source = phOffset is null && ecOffset is null ? ControlContract.SetpointSourceStage : ControlContract.SetpointSourceRecipe,
                StageId = lot.AppliedPhenologicalStageId,
                CreatedAt = nowUtc,
            };
            if (!ControlRules.SetpointChanged(lastByLot.GetValueOrDefault(lot.Id), current))
                continue;

            context.LotSetpointHistories.Add(current);
            added++;
        }

        if (added > 0)
            await context.SaveChangesAsync(ct);
        return added;
    }

    // ------------------------------------------------------------------
    // Auxiliares
    // ------------------------------------------------------------------

    private static readonly SolutionBand Undefined = new(null, null, null);

    /// <summary>Lote en curso del circuito y desde cuándo está plantado ahí (lo que muestra la vista empieza ahí).</summary>
    private sealed record CurrentLot(int CircuitId, int LotId, string Name, DateTime SinceUtc);

    private sealed record CommandRow(ActuatorCommand Command, string Kind, string Name);

    /// <summary>
    /// Todo lo que mira el motor de un circuito, armado igual para la vista y para la evaluación. Ph/Ec: el estado con la
    /// banda del lote (lo que se muestra); el motor lo mira con su banda de acción (<see cref="ControlEngine.ActionBand"/>).
    /// </summary>
    private sealed record CircuitSnapshot(
        Circuit Circuit,
        IotNode Node,
        Lot? Lot,
        List<Actuator> Actuators,
        List<CommandRow> Commands,
        ControlEngine.Input Input,
        LotRecipe? Recipe,
        SolutionParameterStatus Ph,
        SolutionParameterStatus Ec,
        IReadOnlyList<SensorFault> Faults);

    private async Task<CircuitSnapshot?> SnapshotAsync(HydroPilotDbContext context, int circuitId, DateTime nowUtc, CancellationToken ct)
    {
        var circuit = await context.Circuits.AsNoTracking().Include(c => c.Node).FirstOrDefaultAsync(c => c.Id == circuitId, ct);
        if (circuit?.Node is not { } node)
            return null;

        var lot = await LotWithBandsAsync(context, circuitId, nowUtc, ct);
        var recipe = lot is null ? null : await RecipeService.CurrentAsync(context, lot.Id, ct);
        var freshMinutes = Math.Max(1, _dashboardOptions.KpiFreshnessMinutes);
        var phBand = BandFor(lot, recipe, ControlContract.ParameterPh);
        var ecBand = BandFor(lot, recipe, ControlContract.ParameterEc);
        var phReadings = await LatestReadingsAsync(context, circuitId, ControlContract.ParameterPh, SolutionRules.LookbackReadings, ct);
        var ecReadings = await LatestReadingsAsync(context, circuitId, ControlContract.ParameterEc, SolutionRules.LookbackReadings, ct);
        var ph = SolutionRules.Evaluate(ControlContract.ParameterPh, "pH", phReadings, null, phBand, nowUtc, freshMinutes);
        var ec = SolutionRules.Evaluate(ControlContract.ParameterEc, "mS/cm", ecReadings, null, ecBand, nowUtc, freshMinutes);
        var phAction = SolutionRules.Evaluate(ControlContract.ParameterPh, "pH", phReadings, null,
            ControlEngine.ActionBand(phBand), nowUtc, freshMinutes);
        var ecAction = SolutionRules.Evaluate(ControlContract.ParameterEc, "mS/cm", ecReadings, null,
            ControlEngine.ActionBand(ecBand), nowUtc, freshMinutes);

        var recentSince = nowUtc.AddMinutes(-Math.Max(RecentReadingsMinutes, _options.TrendMinutes));
        var recent = await UsableReadings(context, circuitId)
            .Where(r => r.ObservedAtUtc >= recentSince)
            .OrderBy(r => r.ObservedAtUtc)
            .Take(2_000)
            .Select(r => new { r.ObservedAtUtc, Type = r.Sensor!.SensorType!.Name, r.Value })
            .ToListAsync(ct);
        List<ControlEngine.Point> Points(string type) =>
            recent.Where(r => r.Type == type).Select(r => new ControlEngine.Point(r.ObservedAtUtc, r.Value)).ToList();

        // Comandos de las últimas 24 h (o TimelineHours) y los abiertos más viejos, que siguen contando.
        var since = nowUtc.AddHours(-Math.Max(24, _options.TimelineHours));
        var commands = (await context.ActuatorCommands.AsNoTracking()
                .Where(c => c.CircuitId == circuitId
                            && (c.CreatedAtUtc >= since
                                || c.Status == ControlContract.StatusPending
                                || c.Status == ControlContract.StatusDelivered))
                .OrderByDescending(c => c.CreatedAtUtc)
                .Select(c => new { Command = c, c.Actuator!.Kind, c.Actuator.Name })
                .ToListAsync(ct))
            .Select(c => new CommandRow(c.Command, c.Kind, c.Name))
            .ToList();

        var actuators = (await context.Actuators.AsNoTracking().Where(a => a.CircuitId == circuitId).ToListAsync(ct))
            .OrderBy(a => KindOrder(a.Kind))
            .ToList();
        var paused = await context.ControlPauses.AsNoTracking()
            .Where(p => p.CircuitId == circuitId && p.ResumedAtUtc == null)
            .Select(p => p.Parameter)
            .ToListAsync(ct);
        var faults = await context.SensorFaults.AsNoTracking()
            .Where(f => f.Sensor!.CircuitId == circuitId && f.ResolvedAtUtc == null)
            .ToListAsync(ct);

        var day = nowUtc.AddHours(-24);
        var pumps = actuators
            .Select(a => new ControlEngine.Pump(
                a.Id, a.Kind, a.Name, a.Unit, a.MaxPerCommand, a.MaxPerDay, a.IsActive,
                commands.Where(c => c.Command.ActuatorId == a.Id && c.Command.CreatedAtUtc >= day)
                    .Sum(c => ControlRules.UsedAmount(c.Command.Status, c.Command.Amount, c.Command.ExecutedAmount)),
                a.EffectPerUnit))
            .ToList();
        var applied = commands
            .Where(c => c.Command.CompletedAtUtc is not null && WasApplied(c.Command))
            .Select(c => new ControlEngine.AppliedDose(c.Kind, Utc(c.Command.CompletedAtUtc!.Value)))
            .ToList();

        var input = new ControlEngine.Input(
            nowUtc,
            circuit.ControlMode,
            lot is not null,
            Utc(node.LastCommandPollAtUtc),
            circuit.ReservoirLiters,
            circuit.SourceWaterEc,
            new ControlEngine.Magnitude(phAction, Points(ControlContract.ParameterPh), paused.Contains(ControlContract.ParameterPh)),
            new ControlEngine.Magnitude(ecAction, Points(ControlContract.ParameterEc), paused.Contains(ControlContract.ParameterEc)),
            pumps,
            commands.Any(c => ControlRules.IsInFlight(c.Command.Status)),
            applied,
            EngineSettings with { PollStaleMinutes = PiContactStaleMinutes(node) },
            SensorIssuesFor(faults, phReadings, ecReadings, node, nowUtc));

        return new CircuitSnapshot(circuit, node, lot, actuators, commands, input, recipe, ph, ec, faults);
    }

    /// <summary>
    /// Errores de sensor que frenan el control, uno por parámetro como mucho: uno abierto de pH o CE o, aunque el
    /// barrido todavía no lo haya abierto, la última lectura más vieja que lo que tarda un sensor en darse por sin datos.
    /// </summary>
    private IReadOnlyList<ControlEngine.SensorIssue> SensorIssuesFor(
        IReadOnlyList<SensorFault> faults, IReadOnlyList<SolutionReading> ph, IReadOnlyList<SolutionReading> ec, IotNode node, DateTime nowUtc)
    {
        var silenceAfter = SensorFaultRules.SilenceAfter(node.ExpectedIntervalSeconds, _silenceFactor);
        var issues = new List<ControlEngine.SensorIssue>();
        foreach (var (parameter, readings) in new[] { (ControlContract.ParameterPh, ph), (ControlContract.ParameterEc, ec) })
        {
            var open = faults.Where(f => f.SensorType == parameter).OrderBy(f => f.DetectedAtUtc).FirstOrDefault();
            // Sin ninguna lectura queda "sin datos" del motor (no hay con qué decidir); acá solo el corte.
            var last = readings.Count == 0 ? (DateTime?)null : readings.Max(r => r.ObservedAtUtc);
            if (open is not null)
                issues.Add(new ControlEngine.SensorIssue(parameter, open.Kind));
            else if (last is not null && SensorFaultRules.IsSilent(last, nowUtc, silenceAfter))
                issues.Add(new ControlEngine.SensorIssue(parameter, SensorFaultKinds.SinDatos));
        }
        return issues;
    }

    /// <summary>Dosis que pudo llegar al tanque: ejecutada, fallida a medias o entregada sin respuesta.</summary>
    private static bool WasApplied(ActuatorCommand c) => c.Status switch
    {
        ControlContract.StatusExecuted => (c.ExecutedAmount ?? c.Amount) > 0m,
        ControlContract.StatusFailed => c.ExecutedAmount is > 0m,
        ControlContract.StatusUnconfirmed => true,
        _ => false,
    };

    /// <summary>
    /// Inserta el comando y, con IoT Hub, lo manda a la Pi; false si otra dosis de la misma bomba ya está en curso
    /// (índice único).
    /// </summary>
    private async Task<bool> AddCommandAsync(HydroPilotDbContext context, ActuatorCommand command, CancellationToken ct)
    {
        context.ActuatorCommands.Add(command);
        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            context.Entry(command).State = EntityState.Detached;
            return false;
        }

        await DispatchAsync(context, [command], command.CreatedAtUtc, ct);
        return true;
    }

    /// <summary>
    /// Con IoT Hub, manda los comandos a la cola del dispositivo y marca cuándo. Si el hub falla, el comando queda
    /// PENDING sin mandar: el barrido lo reintenta, y si vence, nunca se ejecuta.
    /// </summary>
    private async Task DispatchAsync(HydroPilotDbContext context, IReadOnlyList<ActuatorCommand> commands, DateTime nowUtc, CancellationToken ct)
    {
        if (!_channel.PushesCommands || commands.Count == 0)
            return;

        foreach (var dto in await ToDeviceCommandsAsync(context, commands, ct))
        {
            try
            {
                await _channel.DispatchAsync(dto, ct);
                await context.ActuatorCommands
                    .Where(c => c.Id == dto.CommandId && c.DispatchedAtUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.DispatchedAtUtc, nowUtc), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "No se pudo mandar el comando {CommandId} a {Node}; se reintenta en el barrido.",
                    dto.CommandId, dto.NodeId);
            }
        }
    }

    /// <summary>Comandos tal como los recibe la Pi (por HTTP o por IoT Hub), en el mismo orden.</summary>
    private static async Task<IReadOnlyList<DeviceCommandDto>> ToDeviceCommandsAsync(
        HydroPilotDbContext context, IReadOnlyList<ActuatorCommand> commands, CancellationToken ct)
    {
        if (commands.Count == 0)
            return [];

        var actuatorIds = commands.Select(c => c.ActuatorId).Distinct().ToList();
        var keys = await context.Actuators.AsNoTracking()
            .Where(a => actuatorIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.TechnicalKey, ct);
        var nodeIds = commands.Select(c => c.NodeId).Distinct().ToList();
        var identifiers = await context.IotNodes.AsNoTracking()
            .Where(n => nodeIds.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, n => n.Identifier, ct);
        var readingIds = commands.Where(c => c.TriggerReadingId is not null)
            .Select(c => c.TriggerReadingId!.Value).Distinct().ToList();
        var externalIds = readingIds.Count == 0
            ? new Dictionary<int, string>()
            : await context.SensorReadings.AsNoTracking()
                .Where(r => readingIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, r => r.ExternalReadingId, ct);

        return commands.Select(c => new DeviceCommandDto(
                DeviceContract.TypeDoseCommand,
                c.Id,
                identifiers[c.NodeId],
                keys[c.ActuatorId],
                c.Amount,
                c.Unit,
                Utc(c.CreatedAtUtc),
                Utc(c.ExpiresAtUtc),
                c.Source.ToLowerInvariant(),
                new DeviceCommandReason(
                    c.Parameter,
                    c.ValueBefore,
                    c.Target,
                    c.TriggerReadingId is { } id ? externalIds.GetValueOrDefault(id) : null)))
            .ToList();
    }

    /// <summary>
    /// Minutos sin señal de la Pi después de los cuales el motor no dosifica. Por HTTP la Pi consulta cada pocos
    /// segundos; por IoT Hub la señal son sus mensajes, que llegan cada intervalo de telemetría.
    /// </summary>
    private int PiContactStaleMinutes(IotNode node) =>
        _channel.PushesCommands
            ? Math.Max(Math.Max(1, _options.PollStaleMinutes), (int)Math.Ceiling(2 * node.ExpectedIntervalSeconds / 60.0))
            : Math.Max(1, _options.PollStaleMinutes);

    private static decimal EffectOf(Actuator actuator) =>
        actuator.EffectPerUnit is > 0m ? actuator.EffectPerUnit.Value : ControlContract.DefaultFor(actuator.Kind)?.Effect ?? 1m;

    /// <summary>Banda y objetivo del lote: los niveles de la etapa corridos por la receta aprendida (si hay).</summary>
    private static SolutionBand BandFor(Lot? lot, LotRecipe? recipe, string parameter) =>
        lot is null ? Undefined
        : parameter == ControlContract.ParameterPh ? RecipeRules.Shift(SolutionRules.PhBand(lot.CropType), recipe?.PhOffset ?? 0m)
        : RecipeRules.Shift(SolutionRules.EcBand(lot.AppliedPhenologicalStage, lot.CropType), recipe?.EcOffset ?? 0m);

    /// <summary>Cuánto corrió la receta el objetivo (null si no lo corrió).</summary>
    private static decimal? OffsetApplied(SolutionBand stage, SolutionBand shifted) =>
        stage.Target is { } a && shifted.Target is { } b && a != b ? b - a : null;

    /// <summary>Lote en curso de cada circuito, solo si está en curso (Activo / En pausa).</summary>
    private static async Task<Dictionary<int, CurrentLot>> CurrentLotsAsync(
        HydroPilotDbContext context, IReadOnlyList<int> circuitIds, DateTime nowUtc, CancellationToken ct)
    {
        var rows = await context.CircuitAssignments.AsNoTracking()
            .Where(a => circuitIds.Contains(a.CircuitId)
                        && a.ValidFromUtc <= nowUtc && (a.ValidUntilUtc == null || a.ValidUntilUtc > nowUtc)
                        && a.Lot!.Status != null && OpenLotStatuses.Contains(a.Lot.Status.Name))
            .OrderByDescending(a => a.ValidFromUtc)
            .Select(a => new { a.CircuitId, a.LotId, a.Lot!.Name, a.ValidFromUtc })
            .ToListAsync(ct);
        return rows
            .GroupBy(r => r.CircuitId)
            .ToDictionary(g => g.Key, g => g.Select(r => new CurrentLot(r.CircuitId, r.LotId, LotName(r.LotId, r.Name), Utc(r.ValidFromUtc))).First());
    }

    /// <summary>Desde cuándo el circuito tiene su lote en curso; null sin lote (entonces la vista muestra el circuito entero).</summary>
    private static async Task<DateTime?> LotSinceAsync(HydroPilotDbContext context, int circuitId, DateTime nowUtc, CancellationToken ct) =>
        (await CurrentLotsAsync(context, [circuitId], nowUtc, ct)).GetValueOrDefault(circuitId)?.SinceUtc;

    /// <summary>El más reciente de dos momentos (null = sin límite).</summary>
    private static DateTime Later(DateTime from, DateTime? other) => other is { } o && o > from ? o : from;

    private static async Task<Lot?> LotWithBandsAsync(HydroPilotDbContext context, int circuitId, DateTime nowUtc, CancellationToken ct)
    {
        var current = (await CurrentLotsAsync(context, [circuitId], nowUtc, ct)).GetValueOrDefault(circuitId);
        return current is null
            ? null
            : await context.Lots.AsNoTracking()
                .Include(l => l.CropType)
                .Include(l => l.AppliedPhenologicalStage)
                .FirstOrDefaultAsync(l => l.Id == current.LotId, ct);
    }

    /// <summary>Lecturas usables (válida, sospechosa u obsoleta) de pH y CE del circuito, o de una sola magnitud.</summary>
    private static IQueryable<SensorReading> UsableReadings(HydroPilotDbContext context, int circuitId, string? sensorType = null)
    {
        var readings = context.SensorReadings.AsNoTracking()
            .Where(TelemetryQualityPolicy.OperationallyUsableReading)
            .Where(r => r.Sensor != null && r.Sensor.CircuitId == circuitId && r.Sensor.SensorType != null);
        return sensorType is null
            ? readings.Where(r => r.Sensor!.SensorType!.Name == ControlContract.ParameterPh
                                  || r.Sensor!.SensorType!.Name == ControlContract.ParameterEc)
            : readings.Where(r => r.Sensor!.SensorType!.Name == sensorType);
    }

    private static async Task<IReadOnlyList<SolutionReading>> LatestReadingsAsync(
        HydroPilotDbContext context, int circuitId, string sensorType, int take, CancellationToken ct) =>
        await UsableReadings(context, circuitId, sensorType)
            .OrderByDescending(r => r.ObservedAtUtc)
            .Take(take)
            .Select(r => new SolutionReading(sensorType, r.Quality, r.Value, r.ObservedAtUtc))
            .ToListAsync(ct);

    private sealed record TriggerReading(int Id, decimal Value);

    private static async Task<TriggerReading?> LatestReadingWithIdAsync(
        HydroPilotDbContext context, int circuitId, string sensorType, CancellationToken ct) =>
        await UsableReadings(context, circuitId, sensorType)
            .OrderByDescending(r => r.ObservedAtUtc)
            .Select(r => new TriggerReading(r.Id, r.Value))
            .FirstOrDefaultAsync(ct);

    private static ControlCommandDto ToDto(ActuatorCommand c, string kind, string name) => new(
        c.Id, c.ActuatorId, kind, name, c.Amount, c.Unit, c.Status, c.Source, c.RequestedBy,
        Utc(c.CreatedAtUtc), Utc(c.ExpiresAtUtc), Utc(c.DeliveredAtUtc), Utc(c.CompletedAtUtc),
        c.ExecutedAmount, c.ResultError, c.Parameter, c.ValueBefore, c.Target,
        c.Strength, c.ExpectedEffect, c.ValueAfter, c.ObservedEffect, c.Effective, Utc(c.EffectEvaluatedAtUtc),
        Utc(c.DispatchedAtUtc));

    private static ControlPauseDto ToDto(ControlPause p) =>
        new(p.Id, p.Parameter, Utc(p.PausedAtUtc), p.Reason, Utc(p.ResumedAtUtc), p.ResumedBy);

    private static int KindOrder(string kind)
    {
        for (var i = 0; i < ControlContract.DefaultActuators.Count; i++)
        {
            if (ControlContract.DefaultActuators[i].Kind == kind)
                return i;
        }
        return int.MaxValue;
    }

    private static string LotName(int id, string? name) => string.IsNullOrWhiteSpace(name) ? $"Lote #{id}" : name;

    /// <summary>SQL Server devuelve las fechas sin zona: se marcan UTC para que el JSON lleve la "Z".</summary>
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException sql && (sql.Number == 2601 || sql.Number == 2627);
}
