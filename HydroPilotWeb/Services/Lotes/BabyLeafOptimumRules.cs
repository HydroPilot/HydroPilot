namespace HydroPilotWeb.Services.Lotes;

/// <summary>Un día de la curva de aptas del lote: GDD de ese día, cuántas plantas salieron Baby Leaf y cuántas se evaluaron.</summary>
public sealed record AptCurvePoint(DateOnly Date, decimal Gdd, int Apt, int Evaluated = 0);

/// <summary>De dónde sale el óptimo: la curva del propio lote, el pico de los lotes parecidos o el de fábrica.</summary>
public enum OptimumSource { Lote, Historico, Fabrica }

/// <summary>Óptimo de corte Baby Leaf: GDD, de dónde sale y si el pico del lote ya pasó (cortar ahora).</summary>
public sealed record BabyLeafOptimum(decimal Gdd, OptimumSource Source, bool PeakReached, int? PeakApt)
{
    public static readonly BabyLeafOptimum Factory = new(BabyLeafOptimumRules.FactoryOptimumGdd, OptimumSource.Fabrica, false, null);
}

/// <summary>Lote cerrado con el GDD del pico de sus aptas (para el óptimo histórico).</summary>
public sealed record PeakHistoryLot(DateOnly SowingDate, decimal PeakGdd, decimal? AverageDailyGdd);

/// <summary>
/// Óptimo de corte Baby Leaf por la curva de aptas (lógica pura): las plantas llegan a Baby Leaf cada una en su
/// momento (la curva sube) y las primeras se pasan de tamaño (baja); el óptimo es el pico, el día con más Baby Leafs a
/// la vez y menos desperdicio. Hay una sola fecha óptima (los cortes pueden ser varios).
/// <list type="bullet">
/// <item>En la ventana, con la curva del lote: si las aptas bajaron del mejor día, el pico ya pasó (fue ese día) y se
/// corta ahora; si siguen subiendo, el óptimo es el histórico o, si ya se pasó, mañana.</item>
/// <item>Antes: el pico de los lotes cerrados parecidos (pesados por temporada × clima, como la previsión) o, sin
/// historial, 300 GDD.</item>
/// </list>
/// </summary>
public static class BabyLeafOptimumRules
{
    public const decimal FactoryOptimumGdd = 300m;

    /// <summary>El mejor día de la curva (el primero con más aptas); null si nunca hubo aptas.</summary>
    public static AptCurvePoint? Peak(IReadOnlyList<AptCurvePoint> curve) =>
        curve.Where(p => p.Apt > 0).OrderByDescending(p => p.Apt).ThenBy(p => p.Date).FirstOrDefault();

    /// <summary>Parte de las evaluadas que tiene que estar apta para que el mejor día cuente como pico (no el ruido del principio).</summary>
    public const decimal MinPeakShare = 0.1m;

    /// <summary>
    /// El pico pasó: el último día tiene menos aptas que el mejor, y el mejor tuvo al menos el 10 % de las evaluadas (un
    /// par de aptas al abrir la ventana no es el pico).
    /// </summary>
    public static bool PeakReached(IReadOnlyList<AptCurvePoint> curve)
    {
        if (Peak(curve) is not { } peak)
            return false;
        var minimum = Math.Max(1, (int)Math.Ceiling(peak.Evaluated * MinPeakShare));
        var last = curve.OrderBy(p => p.Date).Last();
        return peak.Apt >= minimum && last.Date > peak.Date && last.Apt < peak.Apt;
    }

    public static BabyLeafOptimum Resolve(
        IReadOnlyList<AptCurvePoint> curve,
        decimal? historicalPeakGdd,
        decimal accumulatedGdd,
        decimal windowOpenGdd,
        decimal windowCloseGdd)
    {
        decimal Within(decimal value) => Math.Min(Math.Max(value, windowOpenGdd), windowCloseGdd - 1m);

        if (Peak(curve) is { } peak && PeakReached(curve))
            return new BabyLeafOptimum(Within(peak.Gdd), OptimumSource.Lote, true, peak.Apt);

        var expected = historicalPeakGdd ?? FactoryOptimumGdd;
        var source = historicalPeakGdd is null ? OptimumSource.Fabrica : OptimumSource.Historico;
        if (accumulatedGdd >= windowOpenGdd && accumulatedGdd < windowCloseGdd && curve.Count > 0)
        {
            // En la ventana y las aptas siguen subiendo: no es hoy. Si el esperado ya pasó, mañana.
            return new BabyLeafOptimum(Within(Math.Max(expected, accumulatedGdd + 1m)), OptimumSource.Lote, false, Peak(curve)?.Apt);
        }
        return new BabyLeafOptimum(Within(expected), source, false, null);
    }

    /// <summary>
    /// Pico histórico: promedio del GDD del pico de los lotes cerrados, pesado por temporada × clima (los mismos pesos
    /// que la previsión, <see cref="BabyLeafOutlookRules"/>). Si los parecidos pesan menos de 1, el promedio de todos.
    /// </summary>
    public static decimal? HistoricalPeak(IReadOnlyList<PeakHistoryLot> lots, DateOnly sowingDate, decimal? expectedAverageDailyGdd)
    {
        if (lots.Count == 0)
            return null;
        var weighted = lots
            .Select(l => (Lot: l, Weight: BabyLeafOutlookRules.SeasonWeight(BabyLeafOutlookRules.SeasonDistanceDays(l.SowingDate, sowingDate))
                                         * BabyLeafOutlookRules.ClimateWeight(l.AverageDailyGdd, expectedAverageDailyGdd)))
            .ToList();
        if (weighted.Sum(w => w.Weight) < BabyLeafOutlookRules.MinSimilarWeight)
            weighted = lots.Select(l => (Lot: l, Weight: 1m)).ToList();
        var total = weighted.Sum(w => w.Weight);
        return total > 0 ? Math.Round(weighted.Sum(w => w.Weight * w.Lot.PeakGdd) / total, 1) : null;
    }
}
