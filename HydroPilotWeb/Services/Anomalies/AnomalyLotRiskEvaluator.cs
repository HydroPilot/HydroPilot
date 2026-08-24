using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Services.Anomalies;

/// <summary>
/// Evaluador compartido del riesgo por planta/lote (ANO-07 + ANO-08): episodios
/// ABIERTOS o RECONOCIDOS de la solución compartida (pH/CE) señalan en riesgo a
/// TODAS las plantas ACTIVAS del lote (la solución es común a todo el cultivo).
/// No convierte lotes en descartados: la señal alimenta la evaluación comercial de
/// lotes (PlantDecisionEngine la prioriza), y el estado operativo lo decide el dominio.
/// </summary>
public sealed class AnomalyLotRiskEvaluator
{
    public sealed record LotRiskState(
        IReadOnlyList<AnomalyEvent> OpenEpisodes,
        int ActivePlants,
        string? RiskReason);

    public async Task<LotRiskState> EvaluateAsync(int lotId, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var episodes = await context.AnomalyEvents
            .Where(e => e.LotId == lotId
                && (e.Status == AnomalyContract.StatusAbierta || e.Status == AnomalyContract.StatusReconocida))
            .OrderBy(e => e.FirstObservedAtUtc)
            .ToListAsync(ct);

        var activePlants = await context.Plants
            .CountAsync(p => p.LotId == lotId && p.OperationalState == PlantOperationalState.Activa, ct);

        string? reason = null;
        var first = episodes.FirstOrDefault();
        if (first is not null)
        {
            var name = AnomalyRuleRegistry.Templates.FirstOrDefault(t => t.Code == first.RuleCode)?.Name
                       ?? first.RuleCode;
            reason = episodes.Count > 1
                ? $"{episodes.Count} episodios abiertos ({name}: valor {first.ObservedValue:0.##}, objetivo {first.TargetValue:0.##}, desde {first.FirstObservedAtUtc:g})"
                : $"{name}: valor {first.ObservedValue:0.##} fuera de banda"
                  + (first.TargetValue.HasValue ? $", objetivo {first.TargetValue:0.##}" : string.Empty)
                  + $"; desde {first.FirstObservedAtUtc:g}";
        }

        return new LotRiskState(episodes, activePlants, reason);
    }

    /// <summary>Ids de plantas ACTIVAS del lote (las únicas que reciben señal de riesgo).</summary>
    public async Task<IReadOnlyList<int>> GetActivePlantIdsAsync(int lotId, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        return await context.Plants
            .Where(p => p.LotId == lotId && p.OperationalState == PlantOperationalState.Activa)
            .Select(p => p.Id)
            .ToListAsync(ct);
    }

    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;

    public AnomalyLotRiskEvaluator(IDbContextFactory<HydroPilotDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }
}