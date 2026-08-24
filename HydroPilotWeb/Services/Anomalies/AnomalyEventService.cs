using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Services.Anomalies;

/// <summary>Resumen de una corrida del barrido de episodios (logging + UI de admin).</summary>
public sealed record AnomalySweepResult(
    int LotsEvaluated,
    int RulesEvaluated,
    int EventsCreated,
    int EventsUpdated,
    int EventsUpgraded,
    int EventsResolved,
    int CooldownSkips);

/// <summary>
/// Motor de episodios de anomalía (plan 15 / ANO-03 + ANO-04): barrido idempotente
/// sobre lecturas usables recientes que abre, actualiza, promueve (Seguimiento →
/// Abierta) y resuelve episodios. El fingerprint por corrida + la recomputación por
/// delta hacen que un reinicio del worker NO duplique episodios.
///
/// Reglas aplicadas (todas configurables o documentadas, sin constantes ocultas):
/// - Solo lecturas operativamente usables (TelemetryQualityPolicy: VALID/SUSPECT/STALE).
///   Un valor FÍSICAMENTE inválido (INVALID/FUTURE/NO_DATA/SENSOR_ERROR) nunca abre episodio:
///   esa decisión es de IoT; acá la calidad de entrada ya lo excluye.
/// - &lt; N consecutivas fuera de banda = registro en Seguimiento (Advertencia, sin episodio crítico).
/// - &gt;= N consecutivas = episodio Abierta (Crítica). N = AnomalyOptions.DefaultConsecutiveToOpen (2).
/// - Recuperación: lecturas normales consecutivas (DefaultRecoveryCount, 2) tras la corrida.
///   La falta de datos NO cuenta como normalidad (los huecos nunca resuelven).
/// - Cooldown: tras resolver, no se abre un episodio nuevo de la misma regla dentro del lapso.
/// - Lotes COSECHADO/DESCARTADO: no se evalúan y sus episodios abiertos se resuelven (ANO-04).
/// </summary>
public sealed class AnomalyEventService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly AnomalyOptions _options;
    private readonly ILogger<AnomalyEventService> _logger;
    private readonly SemaphoreSlim _sweepGate = new(1, 1);

    public AnomalyEventService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        IOptions<AnomalyOptions> options,
        ILogger<AnomalyEventService> logger)
    {
        _dbFactory = dbFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Barrido completo (worker o disparo manual). Serializado con un semáforo para
    /// que una corrida manual no se pise con la del worker. Idempotente: re-ejecutar
    /// sobre los mismos datos no duplica episodios (fingerprint único + delta).
    /// </summary>
    public async Task<AnomalySweepResult> SweepAsync(CancellationToken ct)
    {
        await _sweepGate.WaitAsync(ct);
        try
        {
            return await SweepCoreAsync(ct);
        }
        finally
        {
            _sweepGate.Release();
        }
    }

    private async Task<AnomalySweepResult> SweepCoreAsync(CancellationToken ct)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var windowStart = now.AddHours(-_options.SweepWindowHours);

        var created = 0; var updated = 0; var upgraded = 0;
        var resolved = 0; var cooldownSkips = 0; var rulesEvaluated = 0;

        // 1) Lotes finalizados: sus episodios abiertos se resuelven (ANO-04: un lote
        // cosechado no sigue generando anomalías operativas).
        var finishedStatusIds = await context.LotStatuses
            .Where(s => s.Name == "COSECHADO" || s.Name == "DESCARTADO")
            .Select(s => s.Id)
            .ToListAsync(ct);
        if (finishedStatusIds.Count > 0)
        {
            var staleOpen = await context.AnomalyEvents
                .Where(e => e.Status != AnomalyContract.StatusResuelta && finishedStatusIds.Contains(e.Lot.StatusId))
                .ToListAsync(ct);
            foreach (var ev in staleOpen)
            {
                Resolve(ev, "lote finalizado (cosechado o descartado): no genera más anomalías operativas", now);
                resolved++;
            }
        }

        // 2) Lotes evaluables: ACTIVO y EN_PAUSA (pausa ≠ fin de ciclo).
        var evaluableLots = await context.Lots
            .Include(l => l.CropType)
            .Include(l => l.AppliedPhenologicalStage)
            .Where(l => l.Status.Name == "ACTIVO" || l.Status.Name == "EN_PAUSA")
            .ToListAsync(ct);

        foreach (var lot in evaluableLots)
        {
            var rules = await LoadRulesAsync(context, lot, ct);
            foreach (var rule in rules.Where(r => r.IsActive && r.Band is not null))
            {
                rulesEvaluated++;

                var readings = await context.SensorReadings
                    .Where(TelemetryQualityPolicy.OperationallyUsableReading)
                    .Where(r => r.LotId == lot.Id
                        && r.ObservedAtUtc >= windowStart && r.ObservedAtUtc <= now
                        && r.Sensor != null && r.Sensor.SensorType != null
                        && r.Sensor.SensorType.Name == rule.SensorTypeName)
                    .OrderBy(r => r.SensorId).ThenBy(r => r.ObservedAtUtc)
                    .Select(r => new { r.SensorId, r.ObservedAtUtc, r.Value })
                    .ToListAsync(ct);

                foreach (var group in readings.GroupBy(r => r.SensorId))
                {
                    var points = group
                        .Select(g => new AnomalyReadingPoint(g.SensorId, g.ObservedAtUtc, g.Value))
                        .ToList();

                    var eventsForSensor = await context.AnomalyEvents
                        .Where(e => e.LotId == lot.Id && e.SensorId == group.Key
                            && e.RuleCode == rule.Code && e.Status != AnomalyContract.StatusResuelta)
                        .ToListAsync(ct);

                    var (c, u, up, cs) = await ProcessSensorAsync(
                        context, lot, rule, group.Key, points, now, eventsForSensor, ct);
                    created += c; updated += u; upgraded += up; cooldownSkips += cs;

                    resolved += await ResolveRecoveredAsync(
                        context, lot, rule, group.Key, eventsForSensor, now, ct);
                }
            }
        }

        await context.SaveChangesAsync(ct);

        var result = new AnomalySweepResult(
            LotsEvaluated: evaluableLots.Count,
            RulesEvaluated: rulesEvaluated,
            EventsCreated: created,
            EventsUpdated: updated,
            EventsUpgraded: upgraded,
            EventsResolved: resolved,
            CooldownSkips: cooldownSkips);

        if (result.EventsCreated + result.EventsUpdated + result.EventsResolved > 0)
            _logger.LogInformation(
                "Barrido de anomalías: lotes {Lots}, reglas {Rules}, creados {Created}, actualizados {Updated}, promovidos {Upgraded}, resueltos {Resolved}, cooldown {Cooldown}",
                result.LotsEvaluated, result.RulesEvaluated, result.EventsCreated,
                result.EventsUpdated, result.EventsUpgraded, result.EventsResolved, result.CooldownSkips);

        return result;
    }

    /// <summary>
    /// Procesa los segmentos de un (lote, sensor, regla): crea, actualiza por delta y
    /// promueve episodios. La coincidencia por fingerprint reencuentra el mismo episodio
    /// tras un reinicio; la continuidad por borde de ventana evita duplicados en corridas
    /// que arrancaron antes de la ventana actual.
    /// </summary>
    private async Task<(int Created, int Updated, int Upgraded, int CooldownSkips)> ProcessSensorAsync(
        HydroPilotDbContext context,
        Lot lot,
        ResolvedRule rule,
        int sensorId,
        IReadOnlyList<AnomalyReadingPoint> points,
        DateTime now,
        List<AnomalyEvent> openEvents,
        CancellationToken ct)
    {
        var band = rule.Band!;
        var segments = AnomalyEvaluator.SegmentRuns(points, band);
        var created = 0; var updated = 0; var upgraded = 0; var cooldownSkips = 0;

        foreach (var seg in segments.Where(s => s.Kind == SegmentKind.OutOfBand))
        {
            var fingerprint = AnomalyFingerprint.Compute(lot.Id, sensorId, rule.Code, seg.FirstObservedAtUtc);

            var existing = openEvents.FirstOrDefault(e => e.Fingerprint == fingerprint)
                ?? openEvents
                    .Where(e => seg.FirstObservedAtUtc <= e.LastObservedAtUtc.AddMinutes(_options.ContinuationGraceMinutes))
                    .OrderByDescending(e => e.LastObservedAtUtc)
                    .FirstOrDefault();

            if (existing is null)
            {
                if (await InCooldownAsync(context, lot.Id, sensorId, rule.Code, seg.FirstObservedAtUtc, rule.CooldownMinutes, ct))
                {
                    cooldownSkips++;
                    continue; // cooldown: no reabrir la misma regla recién resuelta
                }

                var critical = seg.Count >= rule.ConsecutiveToOpen;
                context.AnomalyEvents.Add(new AnomalyEvent
                {
                    Type = rule.Code,
                    LotId = lot.Id,
                    GreenhouseId = lot.GreenhouseId,
                    NodeId = await context.Sensors
                        .Where(s => s.Id == sensorId)
                        .Select(s => (int?)s.NodeId)
                        .FirstOrDefaultAsync(ct),
                    SensorId = sensorId,
                    Severity = critical ? AnomalyContract.SeverityCritica : AnomalyContract.SeverityAdvertencia,
                    ObservedValue = seg.LastValue,
                    TargetValue = band.Target,
                    OperationalMin = band.OperationalMin,
                    OperationalMax = band.OperationalMax,
                    RuleCode = rule.Code,
                    RuleDescription = AnomalyRuleDescriptions.Describe(rule.Name, rule.SensorTypeName, band),
                    FirstObservedAtUtc = seg.FirstObservedAtUtc,
                    LastObservedAtUtc = seg.LastObservedAtUtc,
                    ReadingCount = seg.Count,
                    Status = critical ? AnomalyContract.StatusAbierta : AnomalyContract.StatusSeguimiento,
                    Fingerprint = fingerprint,
                    Origin = AnomalyContract.OriginTelemetria,
                    CreatedAtUtc = now
                });
                created++;
            }
            else
            {
                // Delta determinista: solo lecturas NUEVAS (posteriores a la última
                // observación conocida) suman al conteo; re-barridos no duplican.
                var newPoints = points.Count(p =>
                    p.ObservedAtUtc > existing.LastObservedAtUtc
                    && AnomalyEvaluator.IsOutOfBand(p.Value, band));
                existing.ReadingCount += newPoints;

                if (seg.LastObservedAtUtc > existing.LastObservedAtUtc)
                {
                    existing.LastObservedAtUtc = seg.LastObservedAtUtc;
                    existing.ObservedValue = seg.LastValue;
                }

                if (existing.Status == AnomalyContract.StatusSeguimiento
                    && existing.ReadingCount >= rule.ConsecutiveToOpen)
                {
                    existing.Status = AnomalyContract.StatusAbierta;
                    existing.Severity = AnomalyContract.SeverityCritica;
                    upgraded++;
                }
                updated++;
            }
        }

        return (created, updated, upgraded, cooldownSkips);
    }

    /// <summary>
    /// Resuelve episodios abiertos cuya corrida terminó: lecturas normales consecutivas
    /// (consulta directa tras la última observación, sin depender de bordes de ventana).
    /// </summary>
    private async Task<int> ResolveRecoveredAsync(
        HydroPilotDbContext context,
        Lot lot,
        ResolvedRule rule,
        int sensorId,
        List<AnomalyEvent> openEvents,
        DateTime now,
        CancellationToken ct)
    {
        var resolvedCount = 0;
        foreach (var ev in openEvents)
        {
            var normals = await context.SensorReadings
                .Where(TelemetryQualityPolicy.OperationallyUsableReading)
                .Where(r => r.LotId == lot.Id && r.SensorId == sensorId
                    && r.ObservedAtUtc > ev.LastObservedAtUtc
                    && r.Sensor != null && r.Sensor.SensorType != null
                    && r.Sensor.SensorType.Name == rule.SensorTypeName)
                .OrderBy(r => r.ObservedAtUtc)
                .Take(_options.DefaultRecoveryCount + 1)
                .Select(r => r.Value)
                .ToListAsync(ct);

            // TomaWhile se corta en la primera lectura fuera de banda: si la anomalía
            // vuelve antes de completar la recuperación, el episodio sigue abierto.
            var consecutiveNormals = normals
                .TakeWhile(v => !AnomalyEvaluator.IsOutOfBand(v, rule.Band!))
                .Count();

            if (consecutiveNormals >= _options.DefaultRecoveryCount)
            {
                Resolve(ev, $"recuperación: {consecutiveNormals} lecturas normales consecutivas", now);
                resolvedCount++;
            }
        }
        return resolvedCount;
    }

    /// <summary>Cooldown: última resolución de la misma regla demasiado reciente → no reabrir.</summary>
    private static async Task<bool> InCooldownAsync(
        HydroPilotDbContext context,
        int lotId,
        int sensorId,
        string ruleCode,
        DateTime runStartUtc,
        int cooldownMinutes,
        CancellationToken ct)
    {
        var lastResolved = await context.AnomalyEvents
            .Where(e => e.LotId == lotId && e.SensorId == sensorId && e.RuleCode == ruleCode
                && e.Status == AnomalyContract.StatusResuelta)
            .OrderByDescending(e => e.ResolvedAtUtc)
            .FirstOrDefaultAsync(ct);

        return lastResolved?.ResolvedAtUtc is { } resolvedAt
            && runStartUtc <= resolvedAt.AddMinutes(cooldownMinutes);
    }

    private static void Resolve(AnomalyEvent ev, string reason, DateTime now)
    {
        ev.Status = AnomalyContract.StatusResuelta;
        ev.ResolvedAtUtc = now;
        ev.ResolutionReason = reason;
    }

    /// <summary>
    /// Carga la política de reglas del cultivo: filas del catálogo AnomalyRuleCatalog
    /// (si existen) o política por defecto de AnomalyOptions. La banda se resuelve en
    /// evaluación desde el dominio (crop/etapa) para pH y CE; temperatura/humedad solo
    /// con baseline explícito en el catálogo (hoy pendiente → IsActive=false).
    /// </summary>
    private async Task<IReadOnlyList<ResolvedRule>> LoadRulesAsync(
        HydroPilotDbContext context,
        Lot lot,
        CancellationToken ct)
    {
        var catalog = await context.AnomalyRuleCatalogs
            .Where(c => c.CropTypeId == lot.CropTypeId)
            .ToListAsync(ct);
        var byCode = catalog.ToDictionary(c => c.Code);

        var rules = new List<ResolvedRule>();
        foreach (var template in AnomalyRuleRegistry.Templates)
        {
            byCode.TryGetValue(template.Code, out var row);

            var needsBaseline = template.SensorTypeName is AnomalyContract.SensorTypeTemperatura
                or AnomalyContract.SensorTypeHumedad;
            var band = AnomalyBandResolver.ResolveOperative(
                lot.CropType!, lot.AppliedPhenologicalStage, template.SensorTypeName, row);

            // Temperatura/humedad sin baseline explícito → pendiente (nunca dispara).
            var hasExplicitBaselineOverride = row?.OperationalMin is not null || row?.OperationalMax is not null;

            rules.Add(new ResolvedRule(
                Code: template.Code,
                Type: template.Code,
                Name: template.Name,
                SensorTypeName: template.SensorTypeName,
                ConsecutiveToOpen: row?.ConsecutiveToOpen ?? _options.DefaultConsecutiveToOpen,
                CooldownMinutes: row?.CooldownMinutes ?? _options.DefaultCooldownMinutes,
                IsActive: needsBaseline ? hasExplicitBaselineOverride : (row?.IsActive ?? true),
                Source: row?.Source ?? "default-policy",
                Notes: row?.Notes ?? (needsBaseline
                    ? "Sin umbral agronómico aprobado: pendiente de definición, no se evalúa."
                    : "Política por defecto de AnomalyOptions."),
                Band: band));
        }

        return rules;
    }

    // ---------------------------------------------------------------------------
    // Acciones explícitas del ciclo (ANO-04): reconocimiento y cierre manual.
    // ---------------------------------------------------------------------------

    /// <summary>Reconoce un episodio (acción del usuario). No resuelve: el riesgo sigue activo.</summary>
    public async Task<bool> AcknowledgeAsync(int eventId, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var ev = await context.AnomalyEvents.FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (ev is null || ev.Status == AnomalyContract.StatusResuelta)
            return false;

        ev.Status = AnomalyContract.StatusReconocida;
        ev.AcknowledgedAtUtc ??= DateTime.UtcNow;
        await context.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Cierre explícito de un episodio (ANO-04: "o una acción explícita de cierre").</summary>
    public async Task<bool> ResolveManuallyAsync(int eventId, string? reason = null, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var ev = await context.AnomalyEvents.FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (ev is null || ev.Status == AnomalyContract.StatusResuelta)
            return false;

        Resolve(ev, string.IsNullOrWhiteSpace(reason) ? "cierre manual" : reason!, DateTime.UtcNow);
        await context.SaveChangesAsync(ct);
        return true;
    }
}