using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Services.Anomalies;

/// <summary>
/// Consultas del módulo: los episodios de un nodo (Optimización) y las reglas vigentes (Parámetros).
/// Vista-agnóstico: devuelve DTOs y nunca decide si una lectura es anómala.
/// </summary>
public sealed class AnomalyQueryService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly AnomalyOptions _options;

    public AnomalyQueryService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        IOptions<AnomalyOptions> options)
    {
        _dbFactory = dbFactory;
        _options = options.Value;
    }

    /// <summary>Episodios con filtros (severidad, tipo, lote, nodo, estado, rango), los más recientes primero.</summary>
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
        if (filter.NodeId is { } nodeId)
            query = query.Where(e => e.NodeId == nodeId
                || context.Sensors.Any(s => s.Id == e.SensorId && s.NodeId == nodeId));
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

    /// <summary>
    /// Reglas del cultivo para Ajustes &gt; Parámetros (solo lectura): banda de cada una (pH la del cultivo;
    /// CE la de cada etapa fenológica; temperatura y humedad solo con umbral aprobado), cuántas lecturas
    /// seguidas abren un episodio y cuánto se espera antes de abrir otro.
    /// </summary>
    public async Task<IReadOnlyList<AnomalyRuleOverview>> GetRulesAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var crop = await context.CropTypes.AsNoTracking().OrderBy(c => c.Id).FirstOrDefaultAsync(ct);
        if (crop is null)
            return [];

        var stages = await context.PhenologicalStages.AsNoTracking()
            .Where(s => s.CropTypeId == crop.Id && s.IsActive)
            .OrderBy(s => s.Order)
            .ToListAsync(ct);
        var byCode = await context.AnomalyRuleCatalogs.AsNoTracking()
            .Where(c => c.CropTypeId == crop.Id)
            .ToDictionaryAsync(c => c.Code, ct);

        var rules = new List<AnomalyRuleOverview>();
        foreach (var template in AnomalyRuleRegistry.Templates)
        {
            byCode.TryGetValue(template.Code, out var row);
            IReadOnlyList<AnomalyRuleBand> bands = template.SensorTypeName switch
            {
                AnomalyContract.SensorTypePh when crop.OptimalPhMin is not null && crop.OptimalPhMax is not null =>
                    [new AnomalyRuleBand(null, crop.OptimalPhMin, crop.OptimalPhMax, crop.OptimalPhTarget)],
                AnomalyContract.SensorTypeCe =>
                    stages.Select(s => new AnomalyRuleBand(s.Name, s.EcMin, s.EcMax, s.EcObjective)).ToList(),
                AnomalyContract.SensorTypePh => [],
                _ when row?.OperationalMin is not null || row?.OperationalMax is not null =>
                    [new AnomalyRuleBand(null, row!.OperationalMin, row.OperationalMax, row.TargetValue)],
                _ => new List<AnomalyRuleBand>()
            };

            rules.Add(new AnomalyRuleOverview(
                template.Name,
                template.SensorTypeName,
                bands,
                row?.ConsecutiveToOpen ?? _options.DefaultConsecutiveToOpen,
                row?.CooldownMinutes ?? _options.DefaultCooldownMinutes,
                // Sin banda (umbral sin aprobar) la regla nunca dispara aunque figure activa.
                Active: (row?.IsActive ?? true) && bands.Count > 0));
        }
        return rules;
    }

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
                ResolutionReason: e.ResolutionReason,
                PeakValue: e.PeakValue);
        }).ToList();
    }
}