using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Forecasting;

namespace HydroPilotWeb.Services.Lotes;

/// <summary>
/// Óptimo de corte Baby Leaf de un lote o escenario (<see cref="BabyLeafOptimumRules"/>): la curva de aptas del lote
/// (de sus evaluaciones diarias) y el pico de los lotes cerrados parecidos. Lo usan Lotes, Simulación y el dashboard.
/// </summary>
public class BabyLeafOptimumService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;

    public BabyLeafOptimumService(IDbContextFactory<HydroPilotDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
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
        var curve = lotId is { } id ? await CurveAsync(context, id, ct) : [];
        return BabyLeafOptimumRules.Resolve(
            curve,
            BabyLeafOptimumRules.HistoricalPeak(history, sowingDate, expectedAverageDailyGdd),
            accumulatedGdd,
            windowOpenGdd,
            windowCloseGdd);
    }

    /// <summary>Curva de aptas del lote: por día, el GDD de la evaluación y cuántas plantas salieron Baby Leaf.</summary>
    public static async Task<IReadOnlyList<AptCurvePoint>> CurveAsync(HydroPilotDbContext context, int lotId, CancellationToken ct = default)
    {
        var rows = await context.BabyLeafEvaluations.AsNoTracking()
            .Where(e => e.Plant!.LotId == lotId)
            .GroupBy(e => e.EvaluatedAtUtc.Date)
            .Select(g => new
            {
                Day = g.Key,
                Gdd = g.Average(e => e.GddAtEvaluation),
                Apt = g.Count(e => e.Result == CommercialStageNames.BabyLeafApta),
                Evaluated = g.Count(),
            })
            .ToListAsync(ct);
        return rows
            .OrderBy(r => r.Day)
            .Select(r => new AptCurvePoint(DateOnly.FromDateTime(r.Day), Math.Round(r.Gdd, 1), r.Apt, r.Evaluated))
            .ToList();
    }

    /// <summary>GDD del pico de aptas del lote (se guarda al cerrar el ciclo); null si nunca hubo aptas.</summary>
    public static async Task<decimal?> PeakGddAsync(HydroPilotDbContext context, int lotId, CancellationToken ct = default) =>
        BabyLeafOptimumRules.Peak(await CurveAsync(context, lotId, ct))?.Gdd;

    private static async Task<IReadOnlyList<PeakHistoryLot>> HistoryAsync(
        HydroPilotDbContext context, int cropTypeId, int? excludeLotId, CancellationToken ct) =>
        await context.Lots.AsNoTracking()
            .Where(l => l.CropTypeId == cropTypeId && l.Id != excludeLotId && l.BabyLeafPeakGdd != null && l.Status != null
                        && (l.Status.Name == LotStatusNames.Cosechado || l.Status.Name == LotStatusNames.Descartado))
            .Select(l => new PeakHistoryLot(l.SowingDate, l.BabyLeafPeakGdd!.Value, l.CycleAverageDailyGdd))
            .ToListAsync(ct);
}
