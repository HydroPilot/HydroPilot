using HydroPilotWeb.Services.Anomalies;
using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Services.Control;

/// <summary>
/// Receta aprendida (fase 5): cuánto correr los objetivos de pH y CE de la etapa para un lote.
/// - Historial: de los lotes cerrados parecidos (temporada × clima, los mismos pesos que la previsión de Lotes)
///   se miran los que dieron Baby Leaf por encima del promedio, y se toma con cuánto pH y CE trabajaron respecto
///   de los niveles de su etapa. Con pocos lotes el ajuste se achica (evidencia); sin lotes es 0.
/// - Lote actual: si con el ajuste puesto el score Baby Leaf promedio del lote viene cayendo, la confianza en el
///   ajuste baja a la mitad cada día (vuelve hacia los niveles de la etapa); si se mantiene o sube, se recupera.
/// El objetivo se corre dentro de la banda de la etapa, que sigue siendo el límite seguro. Lógica pura
/// (notas/ux/logica-vista-control.md, sección 8).
/// </summary>
public static class RecipeRules
{
    public const decimal MaxPhOffset = 0.3m;
    public const decimal MaxEcOffset = 0.2m;
    public const decimal OffsetStep = 0.05m;

    /// <summary>Lotes "en cero" que se suman a la evidencia: con 1 lote parecido se aplica el 25 % de lo que sugiere.</summary>
    public const decimal EvidencePrior = 3m;

    /// <summary>Score cayendo: al menos este descenso por día.</summary>
    public const decimal FallingScorePerDay = 1m;
    public const int ScoreTrendDays = 5;
    public const int MinScoreDays = 3;

    /// <summary>Un lote cerrado: siembra (temporada), clima, Baby Leaf logrado y con qué pH/CE trabajó (respecto de su etapa).</summary>
    public sealed record PastLot(
        int LotId,
        DateOnly SowingDate,
        decimal? AverageDailyGdd,
        decimal? BabyLeafRate,
        decimal? PhOffset,
        decimal? EcOffset);

    public sealed record HistoryRecipe(
        decimal PhOffset,
        decimal EcOffset,
        int SimilarLots,
        decimal Evidence,
        decimal? MeanBabyLeafRate,
        int GoodLots);

    public static readonly HistoryRecipe None = new(0m, 0m, 0, 0m, null, 0);

    public static HistoryRecipe FromHistory(IReadOnlyList<PastLot> lots, DateOnly sowingDate, decimal? expectedAverageDailyGdd)
    {
        var weighted = lots
            .Where(l => l.BabyLeafRate is not null && (l.PhOffset is not null || l.EcOffset is not null))
            .Select(l => (Lot: l, Weight:
                BabyLeafOutlookRules.SeasonWeight(BabyLeafOutlookRules.SeasonDistanceDays(l.SowingDate, sowingDate))
                * BabyLeafOutlookRules.ClimateWeight(l.AverageDailyGdd, expectedAverageDailyGdd)))
            .Where(x => x.Weight > 0m)
            .ToList();
        if (weighted.Count == 0)
            return None;

        var total = weighted.Sum(x => x.Weight);
        var mean = weighted.Sum(x => x.Weight * x.Lot.BabyLeafRate!.Value) / total;
        var good = weighted.Where(x => x.Lot.BabyLeafRate >= mean).ToList();
        var evidence = total * total / weighted.Sum(x => x.Weight * x.Weight);
        var shrink = evidence / (evidence + EvidencePrior);

        decimal Offset(Func<PastLot, decimal?> pick, decimal max)
        {
            var items = good.Where(x => pick(x.Lot) is not null).ToList();
            var weight = items.Sum(x => x.Weight);
            if (weight == 0m)
                return 0m;
            var raw = items.Sum(x => x.Weight * pick(x.Lot)!.Value) / weight;
            return Math.Clamp(RoundStep(raw * shrink), -max, max);
        }

        return new HistoryRecipe(
            Offset(l => l.PhOffset, MaxPhOffset),
            Offset(l => l.EcOffset, MaxEcOffset),
            weighted.Count,
            Math.Round(evidence, 2),
            Math.Round(mean, 4),
            good.Count);
    }

    /// <summary>
    /// Tendencia del score Baby Leaf promedio del lote, en puntos por día (mínimos cuadrados) sobre los últimos
    /// <see cref="ScoreTrendDays"/> días con score. Null con menos de <see cref="MinScoreDays"/> días.
    /// </summary>
    public static decimal? ScoreTrendPerDay(IReadOnlyList<(DateOnly Day, decimal Score)> daily)
    {
        var last = daily.OrderByDescending(d => d.Day).Take(ScoreTrendDays).ToList();
        if (last.Count < MinScoreDays)
            return null;

        var first = last.Min(d => d.Day.DayNumber);
        var xs = last.Select(d => (decimal)(d.Day.DayNumber - first)).ToList();
        var ys = last.Select(d => d.Score).ToList();
        var meanX = xs.Average();
        var meanY = ys.Average();
        var sxx = xs.Sum(x => (x - meanX) * (x - meanX));
        if (sxx == 0m)
            return null;
        var sxy = xs.Zip(ys, (x, y) => (x - meanX) * (y - meanY)).Sum();
        return Math.Round(sxy / sxx, 2);
    }

    /// <summary>
    /// Confianza en el ajuste para el día: sin ajuste, 1. Con ajuste: score cayendo (≤ −1 por día) → la mitad;
    /// score estable o subiendo → +0,25 (hasta 1); sin score → igual que ayer.
    /// </summary>
    public static decimal NextTrust(decimal trust, decimal? scoreTrendPerDay, bool adjusted)
    {
        if (!adjusted)
            return 1m;
        if (scoreTrendPerDay is not { } trend)
            return trust;
        if (trend <= -FallingScorePerDay)
            return Math.Round(trust * 0.5m, 2);
        return trend >= 0m ? Math.Min(1m, trust + 0.25m) : trust;
    }

    /// <summary>Ajuste aplicado: el del historial por la confianza, redondeado a 0,05.</summary>
    public static decimal Apply(decimal offset, decimal trust) => RoundStep(offset * trust);

    /// <summary>
    /// Corre el objetivo (y la banda de acción) por el ajuste, sin salirse de la banda de la etapa: el motor nunca
    /// deja pasar más de lo que permiten los niveles fijos, y como mucho mueve el objetivo media media-banda.
    /// </summary>
    public static SolutionBand Shift(SolutionBand band, decimal offset)
    {
        if (offset == 0m || band is not { Min: { } min, Max: { } max, Target: { } target } || max <= min)
            return band;

        var limit = (max - min) / 4m;
        var shift = Math.Clamp(offset, -limit, limit);
        return new SolutionBand(Math.Max(min, min + shift), Math.Min(max, max + shift), target + shift);
    }

    public static decimal RoundStep(decimal value) =>
        Math.Round(value / OffsetStep, MidpointRounding.AwayFromZero) * OffsetStep;
}
