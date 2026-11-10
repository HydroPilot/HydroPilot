using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Forecasting;

namespace HydroPilotWeb.Services.Lotes;

/// <summary>
/// Óptimo de corte Baby Leaf de un lote o escenario (<see cref="BabyLeafOptimumRules"/>): la curva del lote (aptas y
/// score general, de sus evaluaciones diarias) y el pico de los lotes cerrados parecidos. Lo usan Lotes, Simulación y el
/// dashboard.
/// </summary>
public class BabyLeafOptimumService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly IMemoryCache? _cache;

    public BabyLeafOptimumService(IDbContextFactory<HydroPilotDbContext> dbFactory, IMemoryCache? cache = null)
    {
        _dbFactory = dbFactory;
        _cache = cache;
    }

    public async Task<BabyLeafOptimum> ResolveAsync(
        int cropTypeId,
        DateOnly sowingDate,
        decimal accumulatedGdd,
        decimal? expectedAverageDailyGdd,
        decimal windowOpenGdd,
        decimal windowCloseGdd,
        int? lotId,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var history = await HistoryAsync(context, cropTypeId, lotId, ct);
        var curve = lotId is { } id ? await CurveAsync(context, id, ct, _cache) : [];
        return BabyLeafOptimumRules.Resolve(
            curve,
            BabyLeafOptimumRules.HistoricalPeak(history, sowingDate, expectedAverageDailyGdd),
            accumulatedGdd,
            windowOpenGdd,
            windowCloseGdd);
    }

    public async Task<IReadOnlyList<AptCurvePoint>> CurveAsync(int lotId, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        return await CurveAsync(context, lotId, ct, _cache);
    }

    /// <summary>
    /// Curva del lote: por día, el GDD de la evaluación, cuántas plantas salieron Baby Leaf y, de las que eran de la ruta
    /// Baby Leaf ese día (no trasplantadas, o el mismo día del trasplante, que se evalúa antes), cuántas y su score
    /// promedio (el score general).
    /// </summary>
    public static async Task<IReadOnlyList<AptCurvePoint>> CurveAsync(
        HydroPilotDbContext context,
        int lotId,
        CancellationToken ct = default,
        IMemoryCache? cache = null)
    {
        var cacheKey = $"babyleaf-opt-curve:{lotId}:{LotViewCache.GlobalVersion}:{LotViewCache.GetLotVersion(lotId)}";
        if (cache is not null && cache.TryGetValue(cacheKey, out IReadOnlyList<AptCurvePoint>? cached) && cached is not null)
            return cached;

        var rows = await context.BabyLeafEvaluations.AsNoTracking()
            .Where(e => e.Plant!.LotId == lotId)
            .Select(e => new { e.EvaluatedAtUtc, e.GddAtEvaluation, e.Result, e.BabyLeafScore, e.Plant!.TransplantedOn })
            .ToListAsync(ct);

        IReadOnlyList<AptCurvePoint> result = rows
            .GroupBy(e => DateOnly.FromDateTime(e.EvaluatedAtUtc))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var dense = g.Where(e => e.TransplantedOn is not { } transplanted || g.Key <= transplanted).ToList();
                return new AptCurvePoint(
                    g.Key,
                    Math.Round(g.Average(e => e.GddAtEvaluation), 1),
                    g.Count(e => e.Result == CommercialStageNames.BabyLeafApta),
                    dense.Count,
                    dense.Count > 0 ? Math.Round(dense.Average(e => e.BabyLeafScore), 2) : null);
            })
            .ToList();

        cache?.Set(cacheKey, result, TimeSpan.FromSeconds(60));
        return result;
    }

    /// <summary>GDD del pico de aptas del lote (se guarda al cerrar el ciclo); null si nunca hubo aptas.</summary>
    public static async Task<decimal?> PeakGddAsync(HydroPilotDbContext context, int lotId, CancellationToken ct = default) =>
        BabyLeafOptimumRules.Peak(await CurveAsync(context, lotId, ct))?.Gdd;

    private async Task<IReadOnlyList<PeakHistoryLot>> HistoryAsync(
        HydroPilotDbContext context, int cropTypeId, int? excludeLotId, CancellationToken ct)
    {
        var cacheKey = $"babyleaf-opt-history:{cropTypeId}:{excludeLotId}:{LotViewCache.GlobalVersion}";
        if (_cache is not null && _cache.TryGetValue(cacheKey, out IReadOnlyList<PeakHistoryLot>? cached) && cached is not null)
            return cached;

        IReadOnlyList<PeakHistoryLot> result = await context.Lots.AsNoTracking()
            .Where(l => l.CropTypeId == cropTypeId && l.Id != excludeLotId && l.BabyLeafPeakGdd != null && l.Status != null
                        && (l.Status.Name == LotStatusNames.Cosechado || l.Status.Name == LotStatusNames.Descartado))
            .Select(l => new PeakHistoryLot(l.SowingDate, l.BabyLeafPeakGdd!.Value, l.CycleAverageDailyGdd))
            .ToListAsync(ct);

        _cache?.Set(cacheKey, result, TimeSpan.FromMinutes(15));
        return result;
    }
}
