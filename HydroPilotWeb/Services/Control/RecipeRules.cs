using HydroPilotWeb.Services.Anomalies;
using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Services.Control;

/// <summary>
/// Receta aprendida (fase 5): cuánto correr los objetivos de pH y CE de la etapa para un lote.
/// - Historial: de los lotes cerrados parecidos (temporada × clima, los mismos pesos que la previsión de Lotes)
///   se miran los que dieron Baby Leaf por encima del promedio, y se toma cuánto más alto o más bajo trabajaron
///   (pH y CE medidos respecto de los niveles de su etapa) que el conjunto de los parecidos. Se compara contra el
///   conjunto y no contra el objetivo porque el control deja la solución siempre del mismo lado del objetivo (el pH
///   sube solo y se corrige al salir de la banda): ese corrimiento lo tienen todos los lotes y no dice qué funcionó.
///   Con pocos lotes el ajuste se achica (evidencia); sin lotes, o con uno solo, es 0.
/// - Lote actual: si con el ajuste puesto el score Baby Leaf de sus plantas viene cayendo (las mismas plantas, día
///   contra día), la confianza en el ajuste baja a la mitad cada día (vuelve hacia los niveles de la etapa); si se
///   mantiene o sube, se recupera.
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

    /// <summary>Dos evaluaciones de la misma planta cuentan como "día contra día" si las separan hasta 2 días.</summary>
    public const int MaxScoreGapDays = 2;

    /// <summary>Una evaluación diaria de una planta (la tarea de etapa comercial guarda una por planta y día).</summary>
    public sealed record ScoreSample(int PlantId, DateOnly Day, decimal Score, decimal Gdd);

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

        decimal? WeightedMean(IEnumerable<(PastLot Lot, decimal Weight)> lots, Func<PastLot, decimal?> pick)
        {
            var items = lots.Where(x => pick(x.Lot) is not null).ToList();
            var weight = items.Sum(x => x.Weight);
            return weight == 0m ? null : items.Sum(x => x.Weight * pick(x.Lot)!.Value) / weight;
        }

        // Los buenos contra el conjunto de los parecidos.
        decimal Offset(Func<PastLot, decimal?> pick, decimal max)
        {
            if (WeightedMean(good, pick) is not { } goodMean || WeightedMean(weighted, pick) is not { } allMean)
                return 0m;
            return Math.Clamp(RoundStep((goodMean - allMean) * shrink), -max, max);
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
    /// Cambio diario del score de las mismas plantas: por día, el promedio de (score − score anterior de esa planta) ÷
    /// días entre las dos evaluaciones. El promedio del lote no sirve: al cortar Baby Leaf se van las mejores plantas, y
    /// el criterio de ventana suma o resta 20 puntos a todas cuando el lote entra o sale de la ventana. Por eso se
    /// comparan las mismas plantas y no se cuentan los pares que cruzan un borde de la ventana
    /// (<paramref name="windowMin"/>, <paramref name="windowMax"/>).
    /// </summary>
    public static IReadOnlyList<(DateOnly Day, decimal Change)> DailyScoreChanges(
        IReadOnlyList<ScoreSample> samples, decimal windowMin, decimal windowMax)
    {
        static int Side(decimal gdd, decimal min, decimal max) => gdd < min ? 0 : gdd < max ? 1 : 2;

        var changes = new List<(DateOnly Day, decimal Change)>();
        foreach (var plant in samples.GroupBy(s => s.PlantId))
        {
            var ordered = plant.OrderBy(s => s.Day).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var (before, after) = (ordered[i - 1], ordered[i]);
                var gap = after.Day.DayNumber - before.Day.DayNumber;
                if (gap < 1 || gap > MaxScoreGapDays
                    || Side(before.Gdd, windowMin, windowMax) != Side(after.Gdd, windowMin, windowMax))
                    continue;
                changes.Add((after.Day, (after.Score - before.Score) / gap));
            }
        }
        return changes
            .GroupBy(c => c.Day)
            .Select(g => (g.Key, Math.Round(g.Average(c => c.Change), 2)))
            .OrderBy(c => c.Key)
            .ToList();
    }

    /// <summary>
    /// Tendencia del score de las plantas del lote, en puntos por día: el promedio del cambio diario
    /// (<see cref="DailyScoreChanges"/>) de los últimos <see cref="ScoreTrendDays"/> días que lo tienen. Null con menos
    /// de <see cref="MinScoreDays"/> días.
    /// </summary>
    public static decimal? ScoreTrendPerDay(IReadOnlyList<(DateOnly Day, decimal Change)> dailyChanges)
    {
        var last = dailyChanges.OrderByDescending(d => d.Day).Take(ScoreTrendDays).ToList();
        return last.Count < MinScoreDays ? null : Math.Round(last.Average(d => d.Change), 2);
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
