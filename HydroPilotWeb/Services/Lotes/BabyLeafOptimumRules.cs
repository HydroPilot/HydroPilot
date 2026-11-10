namespace HydroPilotWeb.Services.Lotes;

/// <summary>
/// Un día de la curva del lote: GDD de ese día, cuántas plantas salieron Baby Leaf, cuántas de la ruta Baby Leaf (el
/// denso) se evaluaron y el score Baby Leaf general (el promedio de esas; null = sin dato, curvas anteriores).
/// </summary>
public sealed record AptCurvePoint(DateOnly Date, decimal Gdd, int Apt, int Evaluated = 0, decimal? MeanScore = null);

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
/// Óptimo de corte Baby Leaf (lógica pura): el día con más plantas en su punto. Desde 2026-11-10 se mira el score Baby
/// Leaf general del lote (el promedio de las plantas del denso): sube mientras las plantas crecen hacia el tamaño ideal
/// y deja de mejorar cuando la mayoría llegó (las primeras ya empiezan a pasarse). Se corta ese día, en la cima, sin
/// esperar a que baje ni a perder plantas. Hay una sola fecha óptima (los cortes pueden ser varios).
/// <list type="bullet">
/// <item>En la ventana, con la curva del lote: el día en que el score general sube menos de <see cref="MinDailyGain"/>
/// o baja, con al menos la mitad de las evaluadas aptas, es el de cortar (<see cref="CutDay"/>): "Cortar ahora". Si
/// sigue mejorando, el óptimo se estima con lo que viene subiendo (<see cref="PlateauGdd"/>) o, si no alcanza, es el
/// histórico; nunca antes de mañana.</item>
/// <item>Antes: el pico de los lotes cerrados parecidos (pesados por temporada × clima, como la previsión) o, sin
/// historial, 300 GDD.</item>
/// <item>Curvas sin score general (anteriores): el pico de aptas, que se confirma cuando bajan.</item>
/// </list>
/// </summary>
public static class BabyLeafOptimumRules
{
    public const decimal FactoryOptimumGdd = 300m;

    /// <summary>Parte de las evaluadas del denso que tiene que estar apta para cortar (antes, cortar pierde Baby Leafs).</summary>
    public const decimal MinCutShare = 0.5m;

    /// <summary>Lo mínimo que tiene que subir el score general en un día para que siga valiendo la pena esperar (puntos).</summary>
    public const decimal MinDailyGain = 0.5m;

    /// <summary>
    /// El día de cortar: el primero en que el score general deja de mejorar (sube menos de <see cref="MinDailyGain"/>
    /// respecto del día anterior, o baja) con al menos la mitad de las evaluadas aptas. Null si todavía no pasó (o la
    /// curva no tiene score general).
    /// </summary>
    public static AptCurvePoint? CutDay(IReadOnlyList<AptCurvePoint> curve)
    {
        var days = curve.Where(p => p.MeanScore is not null).OrderBy(p => p.Date).ToList();
        for (var i = 1; i < days.Count; i++)
        {
            var (before, day) = (days[i - 1], days[i]);
            if (day.Evaluated > 0 && day.Apt >= day.Evaluated * MinCutShare && day.MeanScore - before.MeanScore < MinDailyGain)
                return day;
        }
        return null;
    }

    /// <summary>
    /// El mejor día de la curva: el de cortar o, si el score general no llegó a dejar de mejorar (se cortó en subida), el
    /// de mayor score general con la mitad aptas; sin score general, el primero con más aptas. Null si nunca hubo aptas.
    /// </summary>
    public static AptCurvePoint? Peak(IReadOnlyList<AptCurvePoint> curve) =>
        CutDay(curve)
        ?? curve.Where(p => p.MeanScore is not null && p.Evaluated > 0 && p.Apt >= p.Evaluated * MinCutShare)
            .OrderByDescending(p => p.MeanScore).ThenBy(p => p.Date).FirstOrDefault()
        ?? curve.Where(p => p.Apt > 0).OrderByDescending(p => p.Apt).ThenBy(p => p.Date).FirstOrDefault();

    /// <summary>Parte de las evaluadas que tiene que estar apta para que el mejor día cuente como pico (no el ruido del principio).</summary>
    public const decimal MinPeakShare = 0.1m;

    /// <summary>
    /// Ya es el día de cortar: con score general, desde que deja de mejorar (<see cref="CutDay"/>). Sin score general
    /// (curvas anteriores): el último día tiene menos aptas que el mejor, y el mejor tuvo al menos el 10 % de las
    /// evaluadas (un par de aptas al abrir la ventana no es el pico).
    /// </summary>
    public static bool PeakReached(IReadOnlyList<AptCurvePoint> curve)
    {
        if (curve.Any(p => p.MeanScore is not null))
            return CutDay(curve) is not null;
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

        // Ya es el día de cortar: nunca después de lo acumulado hoy (el GDD de la evaluación puede quedar apenas arriba del
        // que la vista calcula con las lecturas, y el corte pasaría a "mañana").
        if (Peak(curve) is { } peak && PeakReached(curve))
            return new BabyLeafOptimum(Within(Math.Min(peak.Gdd, accumulatedGdd)), OptimumSource.Lote, true, peak.Apt);

        var expected = historicalPeakGdd ?? FactoryOptimumGdd;
        var source = historicalPeakGdd is null ? OptimumSource.Fabrica : OptimumSource.Historico;
        if (accumulatedGdd >= windowOpenGdd && accumulatedGdd < windowCloseGdd && curve.Count > 0)
        {
            // En la ventana y el score general sigue mejorando: no es hoy. Con lo que viene subiendo o, si no alcanza,
            // el esperado; si ya pasó, mañana.
            var trend = PlateauGdd(curve, windowOpenGdd) ?? expected;
            return new BabyLeafOptimum(Within(Math.Max(trend, accumulatedGdd + 1m)), OptimumSource.Lote, false, Peak(curve)?.Apt);
        }
        return new BabyLeafOptimum(Within(expected), source, false, null);
    }

    /// <summary>
    /// GDD en que el score general dejaría de mejorar, con lo que viene subiendo: si la suba de cada día se achica, los
    /// días que faltan para que quede debajo de <see cref="MinDailyGain"/> (al GDD por día de hoy, hasta 15). Solo con 3
    /// días dentro de la ventana (el día que abre, el criterio de GDD hace saltar el score) y al menos un cuarto aptas.
    /// Null si no hay con qué estimar.
    /// </summary>
    public static decimal? PlateauGdd(IReadOnlyList<AptCurvePoint> curve, decimal windowOpenGdd)
    {
        var days = curve.Where(p => p.MeanScore is not null && p.Gdd >= windowOpenGdd).OrderBy(p => p.Date).TakeLast(3).ToList();
        if (days.Count < 3 || days[2].Evaluated <= 0 || days[2].Apt < days[2].Evaluated * MinCutShare / 2)
            return null;
        var earlier = days[1].MeanScore!.Value - days[0].MeanScore!.Value;
        var last = days[2].MeanScore!.Value - days[1].MeanScore!.Value;
        var gddPerDay = days[2].Gdd - days[1].Gdd;
        if (last >= earlier || last < MinDailyGain || gddPerDay <= 0)
            return null;
        var daysLeft = Math.Min(15m, Math.Max(1m, Math.Ceiling((last - MinDailyGain) / (earlier - last))));
        return days[2].Gdd + daysLeft * gddPerDay;
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
