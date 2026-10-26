using System.Globalization;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Control;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Services.Control;

/// <summary>
/// Receta aprendida de cada lote en curso (fase 5), recalculada una vez por día (<see cref="RecipeRules"/>).
/// - Lotes cerrados parecidos: la lógica de Lotes (<see cref="ILotHistorySource"/>: Baby Leaf aptas, siembra y
///   GDD diario de cada ciclo), más con qué pH y CE trabajó cada uno respecto de los niveles de su etapa, medido
///   con sus lecturas y el nivel vigente guardado (<see cref="LotSetpointHistory"/>).
/// - Lote actual: la tendencia de su score Baby Leaf diario mueve la confianza en el ajuste.
/// Sin historial el ajuste es 0 y rigen los niveles de la etapa, como antes.
/// </summary>
public sealed class RecipeService
{
    private static readonly string[] OpenLotStatuses = [LotStatusNames.Activo, LotStatusNames.EnPausa];
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-AR");

    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly ILotHistorySource _history;
    private readonly ILogger<RecipeService> _logger;

    public RecipeService(IDbContextFactory<HydroPilotDbContext> dbFactory, ILotHistorySource history, ILogger<RecipeService> logger)
    {
        _dbFactory = dbFactory;
        _history = history;
        _logger = logger;
    }

    /// <summary>Recalcula la receta de los lotes en curso que no se recalcularon hoy. Devuelve cuántos.</summary>
    public async Task<int> RefreshAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var lotIds = await context.Lots.AsNoTracking()
            .Where(l => l.Status != null && OpenLotStatuses.Contains(l.Status.Name))
            .Select(l => l.Id)
            .ToListAsync(ct);

        var updated = 0;
        foreach (var lotId in lotIds)
        {
            if (await RefreshLotAsync(context, lotId, nowUtc, force: false, ct) is not null)
                updated++;
        }
        return updated;
    }

    /// <summary>Recalcula la receta de un lote ahora (aunque ya se haya recalculado hoy).</summary>
    public async Task<LotRecipe?> RefreshLotAsync(int lotId, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        return await RefreshLotAsync(context, lotId, nowUtc, force: true, ct);
    }

    /// <summary>Receta vigente del lote, o null (rigen los niveles de la etapa).</summary>
    public static Task<LotRecipe?> CurrentAsync(HydroPilotDbContext context, int lotId, CancellationToken ct = default) =>
        context.LotRecipes.AsNoTracking().FirstOrDefaultAsync(r => r.LotId == lotId, ct);

    private async Task<LotRecipe?> RefreshLotAsync(HydroPilotDbContext context, int lotId, DateTime nowUtc, bool force, CancellationToken ct)
    {
        var lot = await context.Lots.AsNoTracking().Include(l => l.CropType).FirstOrDefaultAsync(l => l.Id == lotId, ct);
        if (lot is null)
            return null;

        var row = await context.LotRecipes.FirstOrDefaultAsync(r => r.LotId == lotId, ct);
        if (!force && row is not null && row.UpdatedAtUtc.Date == nowUtc.Date)
            return null;

        // Lotes cerrados del cultivo (lógica de Lotes) y con qué pH/CE trabajó cada uno.
        var outcomes = (await _history.GetCycleOutcomesAsync(lot.CropTypeId, ct)).Where(o => o.LotId != lotId).ToList();
        var measured = await MeasuredOffsetsAsync(context, outcomes.Select(o => o.LotId).ToList(), lot.CropType, ct);
        var past = outcomes
            .Select(o => new RecipeRules.PastLot(
                o.LotId, o.SowingDate, o.AverageDailyGdd, BabyLeafRate(o),
                measured.GetValueOrDefault(o.LotId).Ph, measured.GetValueOrDefault(o.LotId).Ec))
            .ToList();

        var days = Math.Max(1, DateOnly.FromDateTime(nowUtc).DayNumber - lot.SowingDate.DayNumber);
        decimal? expectedGdd = lot.AccumulatedGdd is > 0m ? Math.Round(lot.AccumulatedGdd.Value / days, 2) : null;
        var history = RecipeRules.FromHistory(past, lot.SowingDate, expectedGdd);

        // El score del lote solo juzga un ajuste que ya estaba puesto.
        var wasAdjusted = row is not null && (row.PhOffset != 0m || row.EcOffset != 0m);
        var trend = RecipeRules.ScoreTrendPerDay(await DailyScoreChangesAsync(context, lot, nowUtc, ct));
        var adjusted = history.PhOffset != 0m || history.EcOffset != 0m;
        var trust = RecipeRules.NextTrust(row?.Trust ?? 1m, wasAdjusted ? trend : null, adjusted);
        var ph = RecipeRules.Apply(history.PhOffset, trust);
        var ec = RecipeRules.Apply(history.EcOffset, trust);

        if (row is null)
        {
            row = new LotRecipe { LotId = lotId };
            context.LotRecipes.Add(row);
        }
        var changed = row.PhOffset != ph || row.EcOffset != ec;

        row.HistoryPhOffset = history.PhOffset;
        row.HistoryEcOffset = history.EcOffset;
        row.PhOffset = ph;
        row.EcOffset = ec;
        row.Trust = trust;
        row.SimilarLots = history.SimilarLots;
        row.Evidence = history.Evidence;
        row.ScoreTrend = trend;
        row.Reason = Reason(history, trust, trend);
        row.UpdatedAtUtc = nowUtc;
        if (changed)
            row.LastChangeAtUtc = nowUtc;
        await context.SaveChangesAsync(ct);

        if (changed)
            _logger.LogInformation("Receta del lote {LotId}: pH {Ph:+0.00;-0.00;0}, CE {Ec:+0.00;-0.00;0} (confianza {Trust:P0}, {Lots} lotes parecidos).",
                lotId, ph, ec, trust, history.SimilarLots);
        return row;
    }

    /// <summary>Baby Leaf logrado: aptas en el óptimo sobre las vivas (Lotes); si no, cortadas como Baby Leaf sobre el total.</summary>
    private static decimal? BabyLeafRate(LotCycleOutcome outcome) =>
        outcome.AptRate ?? (outcome.TotalPlants > 0 ? Math.Round((decimal)outcome.HarvestedBabyLeaf / outcome.TotalPlants, 4) : null);

    /// <summary>
    /// Con qué pH y CE trabajó cada lote cerrado respecto de los niveles de su etapa: promedio de (lectura − objetivo
    /// de la etapa vigente), pesado por lecturas. El objetivo de la etapa sale del nivel guardado menos el ajuste de
    /// la receta que tenía. Lotes sin niveles guardados: el pH contra el objetivo del cultivo; la CE no se puede medir.
    /// </summary>
    private static async Task<Dictionary<int, (decimal? Ph, decimal? Ec)>> MeasuredOffsetsAsync(
        HydroPilotDbContext context, IReadOnlyList<int> lotIds, CropType? crop, CancellationToken ct)
    {
        var result = new Dictionary<int, (decimal? Ph, decimal? Ec)>();
        if (lotIds.Count == 0)
            return result;

        var periodsByLot = (await context.LotSetpointHistories.AsNoTracking()
                .Where(h => lotIds.Contains(h.LotId))
                .OrderBy(h => h.ValidFromUtc)
                .ToListAsync(ct))
            .GroupBy(h => h.LotId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var lotId in lotIds)
        {
            if (periodsByLot.TryGetValue(lotId, out var periods) && periods.Count > 0)
            {
                result[lotId] = (
                    await PeriodOffsetAsync(context, lotId, AnomalyContract.SensorTypePh, periods,
                        p => p.PhTarget is { } t ? t - (p.PhOffset ?? 0m) : null, ct),
                    await PeriodOffsetAsync(context, lotId, AnomalyContract.SensorTypeCe, periods,
                        p => p.EcTarget is { } t ? t - (p.EcOffset ?? 0m) : null, ct));
            }
            else
            {
                var (mean, count) = await MeanAsync(context, lotId, AnomalyContract.SensorTypePh, null, null, ct);
                result[lotId] = (count > 0 && crop?.OptimalPhTarget is { } target ? Math.Round(mean - target, 4) : null, null);
            }
        }
        return result;
    }

    private static async Task<decimal?> PeriodOffsetAsync(
        HydroPilotDbContext context,
        int lotId,
        string sensorType,
        IReadOnlyList<LotSetpointHistory> periods,
        Func<LotSetpointHistory, decimal?> baseTarget,
        CancellationToken ct)
    {
        decimal sum = 0m;
        var total = 0;
        for (var i = 0; i < periods.Count; i++)
        {
            if (baseTarget(periods[i]) is not { } target)
                continue;
            var until = i + 1 < periods.Count ? periods[i + 1].ValidFromUtc : (DateTime?)null;
            var (mean, count) = await MeanAsync(context, lotId, sensorType, periods[i].ValidFromUtc, until, ct);
            sum += (mean - target) * count;
            total += count;
        }
        return total > 0 ? Math.Round(sum / total, 4) : null;
    }

    private static async Task<(decimal Mean, int Count)> MeanAsync(
        HydroPilotDbContext context, int lotId, string sensorType, DateTime? from, DateTime? until, CancellationToken ct)
    {
        var readings = context.SensorReadings.AsNoTracking()
            .Where(TelemetryQualityPolicy.OperationallyUsableReading)
            .Where(r => r.LotId == lotId && r.Sensor != null && r.Sensor.SensorType != null && r.Sensor.SensorType.Name == sensorType);
        if (from is { } start)
            readings = readings.Where(r => r.ObservedAtUtc >= start);
        if (until is { } end)
            readings = readings.Where(r => r.ObservedAtUtc < end);

        var count = await readings.CountAsync(ct);
        return count == 0 ? (0m, 0) : (await readings.AverageAsync(r => r.Value, ct), count);
    }

    /// <summary>Cambio diario del score de las mismas plantas del lote (sus evaluaciones de los últimos días).</summary>
    private static async Task<IReadOnlyList<(DateOnly Day, decimal Change)>> DailyScoreChangesAsync(
        HydroPilotDbContext context, Lot lot, DateTime nowUtc, CancellationToken ct)
    {
        var since = nowUtc.Date.AddDays(-(RecipeRules.ScoreTrendDays + RecipeRules.MaxScoreGapDays + 1));
        var rows = await context.BabyLeafEvaluations.AsNoTracking()
            .Where(e => e.Plant != null && e.Plant.LotId == lot.Id && e.EvaluatedAtUtc >= since)
            .Select(e => new { e.PlantId, e.EvaluatedAtUtc, e.BabyLeafScore, e.GddAtEvaluation })
            .ToListAsync(ct);
        var window = await context.BabyLeafConfigs.AsNoTracking()
            .Where(c => c.CropTypeId == lot.CropTypeId && c.IsActive)
            .OrderByDescending(c => c.Id)
            .Select(c => new { c.GddMin, c.GddMax })
            .FirstOrDefaultAsync(ct);
        return RecipeRules.DailyScoreChanges(
            rows.Select(r => new RecipeRules.ScoreSample(r.PlantId, DateOnly.FromDateTime(r.EvaluatedAtUtc), r.BabyLeafScore, r.GddAtEvaluation))
                .ToList(),
            window?.GddMin ?? 250m,
            window?.GddMax ?? 450m);
    }

    internal static string Reason(RecipeRules.HistoryRecipe history, decimal trust, decimal? trend)
    {
        if (history.SimilarLots == 0)
            return "Sin lotes cerrados parecidos (temporada y clima): rigen los niveles de la etapa.";

        if (history.SimilarLots == 1)
            return "1 lote parecido: hace falta compararlo con otros para ajustar; rigen los niveles de la etapa.";

        var lots = $"{history.SimilarLots} lotes parecidos";
        if (history.PhOffset == 0m && history.EcOffset == 0m)
            return $"{lots}: los de más Baby Leaf trabajaron con pH y CE parecidos al resto; rigen los niveles de la etapa.";

        var worked = string.Join(" y ", new[]
        {
            history.PhOffset != 0m ? $"pH más {(history.PhOffset > 0m ? "alto" : "bajo")}" : null,
            history.EcOffset != 0m ? $"CE más {(history.EcOffset > 0m ? "alta" : "baja")}" : null,
        }.Where(p => p is not null));
        var adjustment = string.Join(" · ", new[]
        {
            history.PhOffset != 0m ? $"pH {Signed(history.PhOffset)}" : null,
            history.EcOffset != 0m ? $"CE {Signed(history.EcOffset)}" : null,
        }.Where(p => p is not null));
        var reason = $"{lots}: los de más Baby Leaf (≥ {(history.MeanBabyLeafRate ?? 0m).ToString("P0", Es)}) trabajaron con " +
                     $"{worked} que el resto (ajuste {adjustment}).";
        if (trust < 1m)
            reason += trend is { } t
                ? $" Confianza {trust.ToString("P0", Es)}: el score del lote viene {(t < 0m ? "bajando" : "estable")} ({Signed(t)} por día)."
                : $" Confianza {trust.ToString("P0", Es)}.";
        return reason;
    }

    private static string Signed(decimal value) =>
        (value > 0m ? "+" : value < 0m ? "−" : string.Empty) + Math.Abs(value).ToString("0.00", Es);
}
