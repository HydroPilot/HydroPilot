using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Services.Anomalies;

/// <summary>
/// Consultas del módulo para la UI (ANO-05) y consumidores externos (dashboard
/// puede usar <see cref="GetSummaryAsync"/>; reports, la timeline). Vista-agnóstico:
/// devuelve DTOs y nunca decide si una lectura es anómala.
/// </summary>
public sealed class AnomalyQueryService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly AnomalyOptions _options;
    private readonly AnomalyLotRiskEvaluator _riskEvaluator;

    public AnomalyQueryService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        IOptions<AnomalyOptions> options,
        AnomalyLotRiskEvaluator riskEvaluator)
    {
        _dbFactory = dbFactory;
        _options = options.Value;
        _riskEvaluator = riskEvaluator;
    }

    /// <summary>Timeline de episodios con filtros (severidad, tipo, lote, estado, rango).</summary>
    public async Task<IReadOnlyList<AnomalyEventDto>> GetTimelineAsync(
        AnomalyFilter filter,
        int limit = 200,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var query = context.AnomalyEvents.AsNoTracking().Include(e => e.Lot).AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Severity))
            query = query.Where(e => e.Severity == filter.Severity);
        if (!string.IsNullOrWhiteSpace(filter.Type))
            query = query.Where(e => e.Type == filter.Type);
        if (filter.LotId.HasValue)
            query = query.Where(e => e.LotId == filter.LotId.Value);
        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(e => e.Status == filter.Status);
        if (filter.From.HasValue)
            query = query.Where(e => e.FirstObservedAtUtc >= filter.From.Value.ToDateTime(TimeOnly.MinValue));
        if (filter.To.HasValue)
        {
            var toExclusive = filter.To.Value.AddDays(1).ToDateTime(TimeOnly.MinValue);
            query = query.Where(e => e.FirstObservedAtUtc < toExclusive);
        }

        var events = await query
            .OrderByDescending(e => e.LastObservedAtUtc)
            .Take(limit)
            .ToListAsync(ct);

        return await BuildDtosAsync(context, events, ct);
    }

    /// <summary>Contadores del resumen (abiertas por severidad, resueltas 7d, seguimiento).</summary>
    public async Task<AnomalySummaryDto> GetSummaryAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;

        var byStatusSeverity = await context.AnomalyEvents
            .GroupBy(e => new { e.Status, e.Severity })
            .Select(g => new { g.Key.Status, g.Key.Severity, Count = g.Count() })
            .ToListAsync(ct);

        var open = byStatusSeverity
            .Where(x => x.Status is AnomalyContract.StatusSeguimiento or AnomalyContract.StatusAbierta or AnomalyContract.StatusReconocida)
            .Sum(x => x.Count);
        var openCritical = byStatusSeverity
            .Where(x => x.Status is AnomalyContract.StatusAbierta or AnomalyContract.StatusReconocida)
            .Where(x => x.Severity == AnomalyContract.SeverityCritica)
            .Sum(x => x.Count);
        var advertencias = byStatusSeverity
            .Where(x => x.Status != AnomalyContract.StatusResuelta)
            .Where(x => x.Severity == AnomalyContract.SeverityAdvertencia)
            .Sum(x => x.Count);
        var seguimiento = byStatusSeverity
            .Where(x => x.Status == AnomalyContract.StatusSeguimiento)
            .Sum(x => x.Count);
        var resolved7d = await context.AnomalyEvents
            .CountAsync(e => e.Status == AnomalyContract.StatusResuelta && e.ResolvedAtUtc >= now.AddDays(-7), ct);
        var total = await context.AnomalyEvents.CountAsync(ct);

        // "Falta de datos no se presenta como normalidad": sin lecturas usables en la
        // ventana, la UI muestra el estado "sin datos" en vez de afirmar que todo está bien.
        var hasOperationalData = await context.SensorReadings
            .Where(TelemetryQualityPolicy.OperationallyUsableReading)
            .Where(r => r.ObservedAtUtc >= now.AddHours(-_options.SweepWindowHours))
            .AnyAsync(ct);

        return new AnomalySummaryDto(
            OpenTotal: open,
            OpenCritical: openCritical,
            OpenAdvertencias: advertencias,
            ResolvedLast7Days: resolved7d,
            SeguimientoActive: seguimiento,
            TotalEvents: total,
            HasOperationalData: hasOperationalData);
    }

    /// <summary>Opciones de lote para filtros + conteo de plantas en riesgo (ANO-08: conteo, sin umbral inventado).</summary>
    public async Task<IReadOnlyList<LotRiskSummaryDto>> GetLotRiskSummariesAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var lots = await context.Lots.AsNoTracking()
            .Select(l => new { l.Id, l.Name })
            .ToListAsync(ct);

        var result = new List<LotRiskSummaryDto>();
        foreach (var lot in lots.OrderBy(l => l.Id))
        {
            var state = await _riskEvaluator.EvaluateAsync(lot.Id, ct);
            result.Add(new LotRiskSummaryDto(
                LotId: lot.Id,
                LotName: lot.Name,
                ActivePlants: state.ActivePlants,
                PlantsAtRisk: state.OpenEpisodes.Count > 0 ? state.ActivePlants : 0,
                OpenCriticalEpisodes: state.OpenEpisodes.Count,
                RiskReason: state.RiskReason));
        }
        return result;
    }

    /// <summary>Reglas vigentes de un lote (banda resuelta) y pendientes (temp/humedad sin umbral aprobado).</summary>
    public async Task<IReadOnlyList<AnomalyRuleDto>> GetRulesForLotAsync(int lotId, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var lot = await context.Lots
            .Include(l => l.CropType)
            .Include(l => l.AppliedPhenologicalStage)
            .FirstOrDefaultAsync(l => l.Id == lotId, ct);
        if (lot is null)
            return [];

        var catalog = await context.AnomalyRuleCatalogs
            .Where(c => c.CropTypeId == lot.CropTypeId)
            .ToListAsync(ct);
        var byCode = catalog.ToDictionary(c => c.Code);

        var dtos = new List<AnomalyRuleDto>();
        foreach (var template in AnomalyRuleRegistry.Templates)
        {
            byCode.TryGetValue(template.Code, out var row);
            var needsBaseline = template.SensorTypeName is AnomalyContract.SensorTypeTemperatura
                or AnomalyContract.SensorTypeHumedad;
            var hasExplicitBaseline = row?.OperationalMin is not null || row?.OperationalMax is not null;

            var band = AnomalyBandResolver.ResolveOperative(
                lot.CropType!, lot.AppliedPhenologicalStage, template.SensorTypeName, row);
            var pending = needsBaseline ? !hasExplicitBaseline : band is null;

            dtos.Add(new AnomalyRuleDto(
                Id: row?.Id ?? 0,
                Code: template.Code,
                Name: template.Name,
                SensorTypeName: template.SensorTypeName,
                ConsecutiveToOpen: row?.ConsecutiveToOpen ?? _options.DefaultConsecutiveToOpen,
                CooldownMinutes: row?.CooldownMinutes ?? _options.DefaultCooldownMinutes,
                IsActive: needsBaseline ? hasExplicitBaseline : (row?.IsActive ?? true),
                Source: row?.Source ?? "default-policy",
                Notes: row?.Notes ?? (needsBaseline
                    ? "Sin umbral agronómico aprobado: pendiente de definición, no se evalúa."
                    : "Política por defecto de AnomalyOptions."),
                PhysicalMin: band?.PhysicalMin,
                PhysicalMax: band?.PhysicalMax,
                OperationalMin: band?.OperationalMin,
                OperationalMax: band?.OperationalMax,
                TargetValue: band?.Target,
                Pending: pending));
        }
        return dtos;
    }

    /// <summary>
    /// Estado del carril futuro de visión computacional (ANO-06): documenta el adaptador
    /// sin detector ficticio. No hay pipeline de imágenes → pendiente.
    /// </summary>
    public static VisionAnomalyAdapterNote GetVisionAdapterNote() =>
        new(
            State: "pendiente",
            Reason: "No existe pipeline de imágenes (entidad, dataset ni modelo) en el repositorio. " +
                    "El adaptador futuro recibirá imagen, lote, tipo, confianza, severidad y timestamp; " +
                    "hasta que exista un modelo real no se afirma detección por visión.");

    /// <summary>Tipos estables disponibles (filtro de la UI).</summary>
    public static IReadOnlyList<(string Code, string Name)> GetTypeOptions() =>
        AnomalyRuleRegistry.Templates.Select(t => (t.Code, t.Name)).ToList();

    /// <summary>Estados del filtro (estados internos del ciclo).</summary>
    public static IReadOnlyList<string> GetStatusOptions() =>
        [AnomalyContract.StatusSeguimiento, AnomalyContract.StatusAbierta,
         AnomalyContract.StatusReconocida, AnomalyContract.StatusResuelta];

    public static IReadOnlyList<string> GetSeverityOptions() =>
        [AnomalyContract.SeverityAdvertencia, AnomalyContract.SeverityCritica];

    private static async Task<IReadOnlyList<AnomalyEventDto>> BuildDtosAsync(
        HydroPilotDbContext context,
        IReadOnlyList<AnomalyEvent> events,
        CancellationToken ct)
    {
        if (events.Count == 0)
            return [];

        var sensorIds = events.Where(e => e.SensorId.HasValue).Select(e => e.SensorId!.Value).Distinct().ToList();
        var sensors = sensorIds.Count > 0
            ? await context.Sensors.AsNoTracking()
                .Where(s => sensorIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Name, s.NodeId })
                .ToListAsync(ct)
            : [];
        var sensorById = sensors.ToDictionary(s => s.Id);

        var nodeIds = sensors.Select(s => s.NodeId).Distinct().ToList();
        var nodes = nodeIds.Count > 0
            ? await context.IotNodes.AsNoTracking()
                .Where(n => nodeIds.Contains(n.Id))
                .Select(n => new { n.Id, n.Identifier, n.GreenhouseId })
                .ToListAsync(ct)
            : [];
        var nodeById = nodes.ToDictionary(n => n.Id);

        var greenhouseIds = nodes.Select(n => n.GreenhouseId).Distinct().ToList();
        var greenhouses = greenhouseIds.Count > 0
            ? await context.Greenhouses.AsNoTracking()
                .Where(g => greenhouseIds.Contains(g.Id))
                .Select(g => new { g.Id, g.Name })
                .ToListAsync(ct)
            : [];
        var greenhouseById = greenhouses.ToDictionary(g => g.Id);

        var typeNames = AnomalyRuleRegistry.Templates.ToDictionary(t => t.Code, t => t.Name);

        return events.Select(e =>
        {
            var sensor = e.SensorId.HasValue && sensorById.TryGetValue(e.SensorId.Value, out var s) ? s : null;
            var node = sensor is not null && nodeById.TryGetValue(sensor.NodeId, out var n) ? n : null;
            var greenhouse = node is not null && greenhouseById.TryGetValue(node.GreenhouseId, out var g) ? g : null;

            return new AnomalyEventDto(
                Id: e.Id,
                Type: e.Type,
                TypeName: typeNames.TryGetValue(e.Type, out var tn) ? tn : e.Type,
                LotId: e.LotId,
                LotName: e.Lot?.Name,
                GreenhouseId: greenhouse?.Id,
                GreenhouseName: greenhouse?.Name,
                NodeId: node?.Id,
                NodeName: node?.Identifier,
                SensorId: e.SensorId,
                SensorName: sensor?.Name,
                Severity: e.Severity,
                ObservedValue: e.ObservedValue,
                TargetValue: e.TargetValue,
                OperationalMin: e.OperationalMin,
                OperationalMax: e.OperationalMax,
                RuleCode: e.RuleCode,
                RuleDescription: e.RuleDescription,
                FirstObservedAtUtc: e.FirstObservedAtUtc,
                LastObservedAtUtc: e.LastObservedAtUtc,
                ReadingCount: e.ReadingCount,
                Status: e.Status,
                ContractStatus: AnomalyContract.ToContractStatus(e.Status),
                Fingerprint: e.Fingerprint,
                Origin: e.Origin,
                AcknowledgedAtUtc: e.AcknowledgedAtUtc,
                ResolvedAtUtc: e.ResolvedAtUtc,
                ResolutionReason: e.ResolutionReason);
        }).ToList();
    }
}