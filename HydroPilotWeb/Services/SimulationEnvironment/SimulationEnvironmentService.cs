using System.Diagnostics;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Control;
using HydroPilotWeb.Models.Notifications;
using HydroPilotWeb.Models.Optimization;
using HydroPilotWeb.Services.Circuits;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Services.SimulationEnvironment;

public sealed record SimulationEnvironmentResult(bool Succeeded, string Message, TimeSpan Elapsed, IReadOnlyList<string> Story);

/// <summary>
/// Arma el entorno de simulación en la base configurada: borra los datos de lotes, telemetría, control, anomalías,
/// optimización, notificaciones y precios, y carga el historial generado (<see cref="HistoryGenerator"/>). Después
/// calcula los pronósticos de cada lote con ForecastService (la lógica real). Solo con
/// <see cref="SimulationEnvironmentOptions.Enabled"/> y contra LocalDB: nunca toca Azure.
/// </summary>
public sealed class SimulationEnvironmentService
{
    public const string SeededAtKey = "SimulationEnvironment:SeededAtUtc";
    /// <summary>Una planta Baby Leaf (a 6,5 cm) y una adulta (a 22,5 cm, trasplantada).</summary>
    public const decimal BabyLeafGramsPerPlant = 30m;
    public const decimal ConventionalGramsPerPlant = 280m;

    /// <summary>Mesadas de la demo: 2 densas de 10 × 20 (donde se siembra) y 5 de Adultas de 4 × 5 (100 huecos).</summary>
    public const int DenseBenches = 2;
    public const int AdultBenches = 5;

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly IServiceScopeFactory _scopes;
    private readonly SimulationEnvironmentOptions _options;
    private readonly bool _useLocalDb;
    private readonly ILogger<SimulationEnvironmentService> _logger;

    public SimulationEnvironmentService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        IServiceScopeFactory scopes,
        IOptions<SimulationEnvironmentOptions> options,
        IConfiguration configuration,
        ILogger<SimulationEnvironmentService> logger)
    {
        _dbFactory = dbFactory;
        _scopes = scopes;
        _options = options.Value;
        _useLocalDb = configuration.GetValue("HydroPilot:UseLocalDb", false);
        _logger = logger;
    }

    /// <summary>Encendido y contra LocalDB.</summary>
    public bool Enabled => _options.Enabled && _useLocalDb;

    public SimulationEnvironmentOptions Options => _options;

    /// <summary>Al arrancar: si el entorno está encendido y la base todavía no tiene el historial, lo genera.</summary>
    public async Task EnsureSeededAsync(CancellationToken ct = default)
    {
        if (!Enabled)
            return;
        await using (var context = await _dbFactory.CreateDbContextAsync(ct))
        {
            if (await context.AppSettings.AnyAsync(s => s.Key == SeededAtKey, ct))
                return;
        }
        var result = await RegenerateAsync(ct);
        if (!result.Succeeded)
            throw new InvalidOperationException(result.Message);
    }

    public async Task<DateTime?> SeededAtAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var value = await context.AppSettings.Where(s => s.Key == SeededAtKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : null;
    }

    /// <summary>Borra lo transaccional y genera el historial de nuevo, hasta hoy.</summary>
    public async Task<SimulationEnvironmentResult> RegenerateAsync(CancellationToken ct = default)
    {
        if (!Enabled)
            return new(false, "El entorno de simulación está apagado (SimulationEnvironment:Enabled) o la base no es LocalDB.", TimeSpan.Zero, []);

        await Gate.WaitAsync(ct);
        var watch = Stopwatch.StartNew();
        try
        {
            // Desde Mantenimiento la app sigue corriendo: los barridos y la API esperan a que termine.
            await RegenerationPause.PauseAsync(ct);
            _logger.LogWarning("Entorno de simulación: generando {Months} meses de historial (la base se reemplaza)…", _options.HistoryMonths);
            await ClearAsync(ct);
            var world = await PrepareWorldAsync(ct);
            var now = DateTime.UtcNow;
            await using (var context = await _dbFactory.CreateDbContextAsync(ct))
                await BulkWriter.BeginLoadAsync(context, LoadedTypes, ct);

            // Generación en un hilo aparte: las lecturas se van descargando a la base mientras se generan.
            await using var telemetry = await _dbFactory.CreateDbContextAsync(ct);
            var readings = 0;
            var evaluations = 0;
            var data = await Task.Run(() => HistoryGenerator.Generate(world, _options, now, chunk =>
            {
                readings += chunk.Readings.Count;
                evaluations += chunk.Evaluations.Count;
                FlushImmutable(telemetry, chunk);
            }), ct);
            readings += data.Readings.Count;
            evaluations += data.Evaluations.Count;

            await WriteAsync(data, ct);
            await FinishAsync(data, now, ct);
            var predictions = await PredictionsAsync(data, now, ct);

            await using (var context = await _dbFactory.CreateDbContextAsync(ct))
            {
                await UpsertSettingAsync(context, SeededAtKey, now.ToString("O"), ct);
                await context.SaveChangesAsync(ct);
            }

            var summary = $"Historial generado: {data.Lots.Count} lotes, {data.Plants.Count} plantas, {evaluations:N0} evaluaciones, " +
                          $"{readings:N0} lecturas, {data.Commands.Count:N0} dosis, {data.Anomalies.Count} anomalías, " +
                          $"{data.Alerts.Count} alertas, {predictions} pronósticos.";
            _logger.LogWarning("Entorno de simulación: {Summary} ({Seconds:0} s)", summary, watch.Elapsed.TotalSeconds);
            foreach (var line in data.Story)
                _logger.LogInformation("  {Line}", line);
            return new(true, summary, watch.Elapsed, data.Story);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Entorno de simulación: no se pudo generar el historial");
            await AbortLoadAsync();
            return new(false, $"No se pudo generar el historial: {ex.Message}", watch.Elapsed, []);
        }
        finally
        {
            RegenerationPause.Resume();
            Gate.Release();
        }
    }

    // ------------------------------------------------------------------

    /// <summary>Tablas que carga el historial: sus claves foráneas se apagan durante la carga.</summary>
    private static readonly Type[] LoadedTypes =
    [
        typeof(Lot), typeof(CircuitAssignment), typeof(Plant), typeof(PlantStageHistory), typeof(PlantImage),
        typeof(PlantImageAnalysis), typeof(BabyLeafEvaluation), typeof(TelemetryBatch), typeof(SensorReading),
        typeof(TelemetryRejection), typeof(ActuatorCommand), typeof(LotSetpointHistory), typeof(LotRecipe),
        typeof(ControlPause), typeof(AnomalyEvent), typeof(NotificationAlert), typeof(NotificationDelivery), typeof(NotificationAttempt),
        typeof(CostPriceCatalog), typeof(DailyWeatherForecast), typeof(WeatherRecord),
    ];

    /// <summary>Si la carga falló: claves foráneas prendidas otra vez.</summary>
    private async Task AbortLoadAsync()
    {
        try
        {
            await using var context = await _dbFactory.CreateDbContextAsync();
            context.Database.SetCommandTimeout(TimeSpan.FromMinutes(15));
            await BulkWriter.EndLoadAsync(context, LoadedTypes, default);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Entorno de simulación: no se pudieron volver a prender las claves foráneas");
        }
    }

    private static readonly string[] ClearedTables =
    [
        "AnomalyEvents", "PlantStageHistories", "BabyLeafEvaluations", "PlantImageAnalyses", "PlantImages", "Plants",
        "ActuatorCommands", "ControlPauses", "SensorFaults", "LotSetpointHistories", "LotRecipes",
        "NotificationAttempts", "NotificationDeliveries", "NotificationAlerts",
        "SensorReadings", "CircuitAssignments", "Predictions", "TelemetryRejections", "TelemetryBatches",
        "CostPriceCatalogs", "Lots", "DailyWeatherForecasts", "WeatherRecords",
    ];

    /// <summary>Tablas sin identidad (clave Guid o el Id del lote): no se les reinicia el contador.</summary>
    private static readonly HashSet<string> WithoutIdentity = ["LotRecipes", "ActuatorCommands"];

    private async Task ClearAsync(CancellationToken ct)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        context.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
        // Los nombres de tabla son constantes de esta clase. Se borra de a tandas para no llenar el log de una vez.
        foreach (var table in ClearedTables)
        {
            var delete = $"SET QUOTED_IDENTIFIER ON; WHILE 1 = 1 BEGIN DELETE TOP (50000) FROM [{table}]; IF @@ROWCOUNT = 0 BREAK; END";
            await context.Database.ExecuteSqlRawAsync(delete, ct);
            if (WithoutIdentity.Contains(table))
                continue;
            var reseed = $"DBCC CHECKIDENT ('[{table}]', RESEED, 0) WITH NO_INFOMSGS;";
            await context.Database.ExecuteSqlRawAsync(reseed, ct);
        }
        await context.IotNodes.ExecuteUpdateAsync(s => s.SetProperty(n => n.LastCommandPollAtUtc, (DateTime?)null), ct);
        await context.Circuits.ExecuteUpdateAsync(s => s.SetProperty(c => c.ControlMode, ControlContract.ModeManual), ct);
    }

    /// <summary>
    /// Lo que el historial necesita y la base de desarrollo puede no traer: las mesadas en la Pi del invernadero (2 densas
    /// de 10 × 20 y 5 de Adultas de 4 × 5; se agregan las que falten y se les pone esa grilla), los sensores de
    /// temperatura y humedad del invernadero, los usuarios de ejemplo (productora, operario y soporte, con correo .test
    /// que nunca se envía) y sus preferencias.
    /// </summary>
    private async Task<SimWorld> PrepareWorldAsync(CancellationToken ct)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var greenhouse = await context.Greenhouses.AsNoTracking().OrderBy(g => g.Id).FirstAsync(ct);
        var pi = await CircuitSetup.PiOfAsync(context, greenhouse.Id, ct)
                 ?? throw new InvalidOperationException("No hay Pi en el invernadero: falta el seed de infraestructura.");
        var existing = await context.Circuits
            .Where(c => c.GreenhouseId == greenhouse.Id && c.IsActive)
            .OrderBy(c => c.Id)
            .ToListAsync(ct);
        var dense = existing.Where(c => c.Kind == CircuitKinds.Dense).Take(DenseBenches).ToList();
        while (dense.Count < DenseBenches)
        {
            var name = await CircuitSetup.NextNameAsync(context, greenhouse.Id, ct);
            dense.Add(await CircuitSetup.CreateAsync(context, greenhouse.Id, pi.Id, name, ControlContract.DefaultReservoirLiters, ct,
                CircuitKinds.Dense, 10, 20));
        }
        var adults = existing.Where(c => c.Kind == CircuitKinds.Adult).Take(AdultBenches).ToList();
        for (var n = 1; adults.Count < AdultBenches; n++)
        {
            var name = $"Adultas {n}";
            if (await context.Circuits.AnyAsync(c => c.GreenhouseId == greenhouse.Id && c.Name == name, ct))
                continue;
            adults.Add(await CircuitSetup.CreateAsync(context, greenhouse.Id, pi.Id, name, ControlContract.DefaultReservoirLiters, ct,
                CircuitKinds.Adult, 4, 5));
        }
        foreach (var circuit in dense)
            (circuit.GridRows, circuit.GridColumns) = (10, 20);
        foreach (var circuit in adults)
            (circuit.GridRows, circuit.GridColumns) = (4, 5);
        await context.SaveChangesAsync(ct);
        var circuits = dense.Concat(adults).ToList();
        await CircuitSetup.EnsureAmbientSensorsAsync(context, greenhouse.Id, pi.Id, ct);

        var producer = await EnsureUserAsync(context, "carla.gomez@hydropilot.test", "Carla", "Gómez", "Productor", ct);
        var @operator = await EnsureUserAsync(context, "diego.ruiz@hydropilot.test", "Diego", "Ruiz", "Operador", ct);
        await EnsureUserAsync(context, "lucia.perez@hydropilot.test", "Lucía", "Pérez", "Soporte Técnico", ct);
        await EnsurePreferenceAsync(context, producer.Id, emailEnabled: true, ct);
        await EnsurePreferenceAsync(context, @operator.Id, emailEnabled: false, ct);
        await context.SaveChangesAsync(ct);

        var crop = await context.CropTypes.AsNoTracking().OrderBy(c => c.Id).FirstAsync(ct);
        var units = await context.MeasurementUnits.AsNoTracking().ToListAsync(ct);
        var unitSymbols = units.ToDictionary(u => u.Id, u => u.Symbol);
        SimSensor ToSim(int id, string type, int? unitId, string key) =>
            new(id, type, unitId, unitId is { } u ? unitSymbols.GetValueOrDefault(u) ?? string.Empty : string.Empty, key);

        // Temperatura y humedad del invernadero: las manda la primera mesada.
        var ambient = (await context.Sensors.AsNoTracking()
                .Where(s => s.CircuitId == null && s.IsActive && s.Node!.GreenhouseId == greenhouse.Id)
                .Select(s => new { s.Id, Type = s.SensorType!.Name, s.MeasurementUnitId, s.TechnicalKey })
                .ToListAsync(ct))
            .GroupBy(s => s.Type)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Id).First());

        var simBenches = new List<SimBench>();
        for (var i = 0; i < circuits.Count; i++)
        {
            var circuit = circuits[i];
            var identifier = await context.IotNodes.AsNoTracking().Where(n => n.Id == circuit.NodeId).Select(n => n.Identifier).SingleAsync(ct);
            var sensors = (await context.Sensors.AsNoTracking()
                    .Where(s => s.CircuitId == circuit.Id && s.IsActive)
                    .Select(s => new { s.Id, Type = s.SensorType!.Name, s.MeasurementUnitId, s.TechnicalKey })
                    .ToListAsync(ct))
                .GroupBy(s => s.Type)
                .ToDictionary(g => g.Key, g =>
                {
                    var s = g.OrderBy(x => x.Id).First();
                    return ToSim(s.Id, s.Type, s.MeasurementUnitId, s.TechnicalKey);
                });
            if (i == 0)
            {
                foreach (var (type, s) in ambient)
                    sensors[type] = ToSim(s.Id, s.Type, s.MeasurementUnitId, s.TechnicalKey);
            }
            simBenches.Add(new SimBench(
                circuit.NodeId,
                identifier,
                ((char)('A' + i)).ToString(),
                sensors,
                await context.Actuators.AsNoTracking().Where(a => a.CircuitId == circuit.Id).ToListAsync(ct),
                circuit.Id,
                circuit.Name,
                circuit.Kind,
                circuit.GridRows,
                circuit.GridColumns));
        }

        var config = await context.BabyLeafConfigs.AsNoTracking()
            .Where(c => c.CropTypeId == crop.Id && c.IsActive).OrderByDescending(c => c.Id).FirstAsync(ct);
        var recipients = await context.Users.AsNoTracking()
            .Where(u => u.Role != "no-asignado")
            .Select(u => new SimUser(u.Id, u.Email, (u.GivenName + " " + u.Surname).Trim(), u.Role))
            .ToListAsync(ct);

        return new SimWorld(
            greenhouse.Id,
            GddService.ResolveGreenhouseTimeZone(greenhouse.TimeZoneId, []),
            crop,
            await context.PhenologicalStages.AsNoTracking().Where(s => s.CropTypeId == crop.Id && s.IsActive).OrderBy(s => s.Order).ToListAsync(ct),
            await context.CommercialStages.AsNoTracking().Where(s => s.CropTypeId == crop.Id).ToDictionaryAsync(s => s.Name, s => s.Id, ct),
            config,
            await context.BabyLeafCriteria.AsNoTracking().Where(c => c.BabyLeafConfigId == config.Id).ToListAsync(ct),
            await context.LotStatuses.AsNoTracking().ToDictionaryAsync(s => s.Name, s => s.Id, ct),
            await context.AnomalyRuleCatalogs.AsNoTracking().Where(r => r.CropTypeId == crop.Id).ToDictionaryAsync(r => r.SensorTypeName, ct),
            simBenches,
            new SimUser(producer.Id, producer.Email, "Carla Gómez", producer.Role),
            new SimUser(@operator.Id, @operator.Email, "Diego Ruiz", @operator.Role),
            recipients,
            BabyLeafGramsPerPlant,
            ConventionalGramsPerPlant,
            await SystemParametersService.LoadTransplantAsync(context, ct));
    }

    private static async Task<User> EnsureUserAsync(
        HydroPilotDbContext context, string email, string givenName, string surname, string role, CancellationToken ct)
    {
        var user = await context.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is not null)
            return user;
        user = new User
        {
            GoogleSub = $"sim-{email}",
            Email = email,
            GivenName = givenName,
            Surname = surname,
            Role = role,
            CreatedAt = DateTime.UtcNow.AddMonths(-25),
            LastLoginAt = DateTime.UtcNow.AddDays(-1),
        };
        context.Users.Add(user);
        await context.SaveChangesAsync(ct);
        return user;
    }

    private static async Task EnsurePreferenceAsync(HydroPilotDbContext context, int userId, bool emailEnabled, CancellationToken ct)
    {
        if (await context.NotificationPreferences.AnyAsync(p => p.UserId == userId, ct))
            return;
        context.NotificationPreferences.Add(new NotificationPreference
        {
            UserId = userId,
            EmailEnabled = emailEnabled,
            MinSeverityEmail = NotificationSeverities.Alta,
            QuietHoursEnabled = emailEnabled,
        });
    }

    /// <summary>Lo que ya no cambia se va guardando mientras se genera, para no tener todo el historial en memoria.</summary>
    private static void FlushImmutable(HydroPilotDbContext context, HistoryData chunk)
    {
        BulkWriter.Write(context, chunk.Batches);
        BulkWriter.Write(context, chunk.Readings);
        BulkWriter.Write(context, chunk.StageHistories);
        BulkWriter.Write(context, chunk.Images);
        BulkWriter.Write(context, chunk.Analyses);
        BulkWriter.Write(context, chunk.Evaluations);
        chunk.Batches.Clear();
        chunk.Readings.Clear();
        chunk.StageHistories.Clear();
        chunk.Images.Clear();
        chunk.Analyses.Clear();
        chunk.Evaluations.Clear();
    }

    /// <summary>Todo lo generado, en orden de dependencias (lo que no cambia ya se fue descargando).</summary>
    private async Task WriteAsync(HistoryData data, CancellationToken ct)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        context.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
        await BulkWriter.WriteAsync(context, data.Prices, ct);
        await BulkWriter.WriteAsync(context, data.Lots, ct);
        await BulkWriter.WriteAsync(context, data.Assignments, ct);
        await BulkWriter.WriteAsync(context, data.Plants, ct);
        await BulkWriter.WriteAsync(context, data.StageHistories, ct);
        await BulkWriter.WriteAsync(context, data.Images, ct);
        await BulkWriter.WriteAsync(context, data.Analyses, ct);
        await BulkWriter.WriteAsync(context, data.Evaluations, ct);
        await BulkWriter.WriteAsync(context, data.Batches, ct);
        await BulkWriter.WriteAsync(context, data.Readings, ct);
        await BulkWriter.WriteAsync(context, data.Rejections, ct);
        await BulkWriter.WriteAsync(context, data.Commands, ct);
        await BulkWriter.WriteAsync(context, data.Setpoints, ct);
        await BulkWriter.WriteAsync(context, data.Recipes, ct);
        await BulkWriter.WriteAsync(context, data.Pauses, ct);
        await BulkWriter.WriteAsync(context, data.Anomalies, ct);
        await BulkWriter.WriteAsync(context, data.Alerts, ct);
        await BulkWriter.WriteAsync(context, data.Deliveries, ct);
        await BulkWriter.WriteAsync(context, data.Attempts, ct);
        await BulkWriter.WriteAsync(context, data.Forecasts, ct);
        await BulkWriter.WriteAsync(context, data.Weather, ct);

        context.Database.SetCommandTimeout(TimeSpan.FromMinutes(15));
        await BulkWriter.EndLoadAsync(context, LoadedTypes, ct);
    }

    /// <summary>Estado final de nodos, bombas y sondas, y los parámetros con que se valorizó el historial.</summary>
    private async Task FinishAsync(HistoryData data, DateTime now, CancellationToken ct)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        foreach (var group in data.Nodes.GroupBy(n => n.NodeId))
        {
            var node = await context.IotNodes.SingleAsync(n => n.Id == group.Key, ct);
            node.LastConnection = group.Max(n => n.LastConnectionUtc);
            node.LastAcceptedAt = group.Max(n => n.LastAcceptedUtc);
            node.LastRejectedAt = group.Max(n => n.LastRejectedUtc);
            node.LastCommandPollAtUtc = group.Max(n => n.LastCommandPollUtc);
            node.FirmwareVersion = group.First().FirmwareVersion;
            node.ConnectionState = NodeConnectionEvaluator.Evaluate(node.LastAcceptedAt, now, node.ExpectedIntervalSeconds, 2);
        }
        foreach (var state in data.Nodes)
        {
            var circuit = await context.Circuits.SingleAsync(c => c.Id == state.CircuitId, ct);
            circuit.ControlMode = state.ControlMode;
            circuit.ReservoirLiters = ControlContract.DefaultReservoirLiters;

            foreach (var (actuatorId, (effect, samples)) in state.Actuators)
            {
                var actuator = await context.Actuators.SingleAsync(a => a.Id == actuatorId, ct);
                actuator.EffectPerUnit = effect;
                actuator.EffectSamples = samples;
            }
            foreach (var (sensorId, calibratedAt) in state.SensorCalibrations)
            {
                var sensor = await context.Sensors.SingleAsync(s => s.Id == sensorId, ct);
                sensor.LastCalibrationDate = calibratedAt;
            }
        }
        foreach (var (key, value) in data.Settings)
            await UpsertSettingAsync(context, key, value, ct);
        await context.SaveChangesAsync(ct);
    }

    /// <summary>Pronósticos de cada lote cada 4 días de su ciclo (y el último día), con ForecastService a esa fecha.</summary>
    private async Task<int> PredictionsAsync(HistoryData data, DateTime now, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var forecasts = scope.ServiceProvider.GetRequiredService<ForecastService>();
        var today = DateOnly.FromDateTime(now);
        var count = 0;
        foreach (var lot in data.Lots)
        {
            var end = lot.ActualHarvestDate ?? data.Plants.Where(p => p.LotId == lot.Id).Max(p => p.HarvestDate ?? p.DiscardDate) ?? today;
            if (end > today)
                end = today;
            var dates = new List<DateOnly>();
            for (var date = lot.SowingDate.AddDays(3); date < end; date = date.AddDays(4))
                dates.Add(date);
            dates.Add(end);
            foreach (var date in dates.Distinct())
            {
                await forecasts.GetForecastAsync(lot.Id, date == today ? null : date, persistSnapshot: true, ct);
                count++;
            }
        }
        return count;
    }

    private static async Task UpsertSettingAsync(HydroPilotDbContext context, string key, string value, CancellationToken ct)
    {
        var setting = await context.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
            context.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else
            setting.Value = value;
    }
}
