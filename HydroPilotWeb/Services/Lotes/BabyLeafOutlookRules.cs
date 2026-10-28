namespace HydroPilotWeb.Services.Lotes;

/// <summary>
/// Resultado de un ciclo cerrado, para el histórico de la previsión. Los datos del ciclo vienen
/// cargados a mano (lotes previos) o calculados de las plantas, su historial y las lecturas.
/// </summary>
public sealed record LotCycleOutcome(
    int LotId,
    string Name,
    DateOnly SowingDate,
    int TotalPlants,
    int HarvestedBabyLeaf,
    int HarvestedConventional,
    int Discarded,
    int? AptAtOptimum,      // aptas a Baby Leaf en el óptimo (capacidad del lote, no lo que se decidió cortar)
    int? EarlyLosses,       // descartadas hasta que cerró la ventana Baby Leaf
    int? LateLosses,        // descartadas después, esperando al convencional (con trasplante: las adultas descartadas)
    decimal? AverageDailyGdd,
    bool IsManual,
    int? LateLossBase = null) // con trasplante: las trasplantadas; null = las que siguieron a convencional (sin trasplante)
{
    /// <summary>Parte de las plantas vivas en el óptimo que estaban aptas a Baby Leaf (0..1).</summary>
    public decimal? AptRate
    {
        get
        {
            if (AptAtOptimum is not { } apt)
                return null;
            var alive = TotalPlants - (EarlyLosses ?? 0);
            return alive > 0 ? Math.Min(1m, (decimal)apt / alive) : null;
        }
    }

    /// <summary>Parte del total que se perdió antes de cerrar la ventana Baby Leaf (0..1).</summary>
    public decimal? EarlyLossRate =>
        EarlyLosses is { } early && TotalPlants > 0 ? Math.Min(1m, (decimal)early / TotalPlants) : null;

    /// <summary>
    /// Parte de las que siguieron a Adulta que se perdió esperando (0..1). Con trasplante, sobre las trasplantadas (las
    /// del denso que no entraron en Adultas no siguieron: no son pérdidas tardías). Sin trasplante (lotes de antes o
    /// cargados a mano), sobre las que no se cortaron como Baby Leaf ni se perdieron temprano.
    /// </summary>
    public decimal? LateLossRate
    {
        get
        {
            if (LateLosses is not { } late || EarlyLosses is not { } early)
                return null;
            var continued = LateLossBase ?? TotalPlants - early - HarvestedBabyLeaf;
            return continued > 0 ? Math.Min(1m, (decimal)late / continued) : null;
        }
    }
}

/// <summary>Lote cerrado usado en la previsión, con su peso por temporada y por clima (0..1).</summary>
public sealed record BabyLeafOutlookLot(LotCycleOutcome Outcome, decimal SeasonWeight, decimal ClimateWeight)
{
    public decimal Weight => SeasonWeight * ClimateWeight;
}

/// <summary>
/// Lo que dice el histórico (D): promedios pesados de los lotes cerrados parecidos. UsedAllLots =
/// no había lotes de temporada o clima parecido y se usó el promedio de todos.
/// </summary>
public sealed record BabyLeafHistory(
    decimal? AptRate,
    decimal? EarlyLossRate,
    decimal? LateLossRate,
    IReadOnlyList<BabyLeafOutlookLot> Lots,
    bool UsedAllLots)
{
    public static readonly BabyLeafHistory None = new(null, null, null, [], false);
}

/// <summary>
/// Previsión de Baby Leaf y pérdidas para un escenario: el histórico (D) combinado con el estado
/// actual del lote (E), que pesa más cuanto más cerca está del óptimo. Las pérdidas son las que
/// FALTAN según la etapa: las tempranas hasta que cierra la ventana Baby Leaf y las tardías
/// mientras se espera al convencional.
/// </summary>
public sealed record BabyLeafOutlook(
    decimal? AptRate,              // previsto (0..1) de las plantas vivas en el óptimo; null = sin datos
    BabyLeafHistory History,
    decimal? CurrentAptRate,       // aptas + candidatas sobre activas; null sin lote o sin evaluaciones
    decimal CurrentWeight,         // peso del estado actual (0..1)
    bool LotHasEvaluations,
    decimal RemainingEarlyLossRate,
    decimal RemainingLateLossRate)
{
    public static readonly BabyLeafOutlook None = new(null, BabyLeafHistory.None, null, 0m, false, 0m, 0m);
}

/// <summary>
/// Reglas de la previsión de Baby Leaf (lógica pura, testeable sin I/O):
/// <list type="bullet">
/// <item>Temporada: pesa 1 un lote sembrado a ±30 días en el calendario (sin importar el año) y baja
/// hasta 0 a los 90 días.</item>
/// <item>Clima: pesa 1 si su GDD diario promedio difiere ≤ 1 del esperado y baja hasta 0 a 4 de
/// diferencia; sin dato de clima, solo cuenta la temporada.</item>
/// <item>Si los lotes parecidos suman menos de 1 de peso, se usa el promedio de todos (con aviso).</item>
/// <item>Estado actual: pesa 0 hasta el fin del establecimiento (150 GDD) y 1 desde el óptimo
/// Baby Leaf (300 GDD).</item>
/// </list>
/// </summary>
public static class BabyLeafOutlookRules
{
    public const int SeasonFullDays = 30;
    public const int SeasonZeroDays = 90;
    public const decimal ClimateFullGdd = 1m;
    public const decimal ClimateZeroGdd = 4m;
    public const decimal MinSimilarWeight = 1m;

    /// <summary>Días entre dos fechas en el calendario, sin importar el año (20/12 y 10/1 → 21).</summary>
    public static int SeasonDistanceDays(DateOnly a, DateOnly b)
    {
        var distance = Math.Abs(a.DayOfYear - b.DayOfYear);
        return Math.Min(distance, 365 - distance);
    }

    public static decimal SeasonWeight(int distanceDays) =>
        distanceDays <= SeasonFullDays ? 1m
        : distanceDays >= SeasonZeroDays ? 0m
        : Math.Round((SeasonZeroDays - distanceDays) / (decimal)(SeasonZeroDays - SeasonFullDays), 2);

    public static decimal ClimateWeight(decimal? lotAverageDailyGdd, decimal? expectedAverageDailyGdd)
    {
        if (lotAverageDailyGdd is not { } lot || expectedAverageDailyGdd is not { } expected)
            return 1m;
        var difference = Math.Abs(lot - expected);
        return difference <= ClimateFullGdd ? 1m
            : difference >= ClimateZeroGdd ? 0m
            : Math.Round((ClimateZeroGdd - difference) / (ClimateZeroGdd - ClimateFullGdd), 2);
    }

    /// <summary>Promedios pesados por temporada × clima de los lotes cerrados (D, con caída a A).</summary>
    public static BabyLeafHistory FromHistory(
        IReadOnlyList<LotCycleOutcome> lots,
        DateOnly sowingDate,
        decimal? expectedAverageDailyGdd)
    {
        if (lots.Count == 0)
            return BabyLeafHistory.None;

        var weighted = lots
            .Select(l => new BabyLeafOutlookLot(
                l,
                SeasonWeight(SeasonDistanceDays(l.SowingDate, sowingDate)),
                ClimateWeight(l.AverageDailyGdd, expectedAverageDailyGdd)))
            .ToList();

        var usedAll = weighted.Sum(w => w.Weight) < MinSimilarWeight;
        if (usedAll)
            weighted = lots.Select(l => new BabyLeafOutlookLot(l, 1m, 1m)).ToList();

        var used = weighted.Where(w => w.Weight > 0).OrderByDescending(w => w.Weight).ToList();

        decimal? Mean(Func<LotCycleOutcome, decimal?> rate)
        {
            var items = used.Where(w => rate(w.Outcome) is not null).ToList();
            var total = items.Sum(w => w.Weight);
            return total > 0 ? Math.Round(items.Sum(w => w.Weight * rate(w.Outcome)!.Value) / total, 4) : null;
        }

        return new BabyLeafHistory(
            Mean(o => o.AptRate), Mean(o => o.EarlyLossRate), Mean(o => o.LateLossRate), used, usedAll);
    }

    /// <summary>
    /// Peso del estado actual del lote: 0 mientras está en desarrollo (antes de que abra la ventana Baby Leaf ninguna
    /// planta puede ser Baby Leaf, así que su estado no dice nada); desde la apertura crece hasta 1 en el óptimo.
    /// </summary>
    public static decimal CurrentStateWeight(decimal accumulatedGdd, decimal babyLeafOpensGdd, decimal babyLeafOptimumGdd)
    {
        if (accumulatedGdd < babyLeafOpensGdd)
            return 0m;
        if (babyLeafOptimumGdd <= babyLeafOpensGdd)
            return 1m;
        return Math.Round(Math.Clamp((accumulatedGdd - babyLeafOpensGdd) / (babyLeafOptimumGdd - babyLeafOpensGdd), 0m, 1m), 2);
    }

    /// <summary>Combina el histórico con el estado actual según el peso del estado actual.</summary>
    public static decimal? Blend(decimal? history, decimal? current, decimal currentWeight)
    {
        if (current is not { } now || currentWeight <= 0)
            return history;
        if (history is not { } past)
            return now;
        return Math.Round(currentWeight * now + (1 - currentWeight) * past, 4);
    }

    /// <summary>Pérdidas tempranas que faltan: la tasa histórica en proporción a lo que queda hasta el cierre de la ventana Baby Leaf.</summary>
    public static decimal RemainingEarlyLoss(decimal? rate, decimal accumulatedGdd, decimal windowCloseGdd) =>
        rate is { } r && windowCloseGdd > 0
            ? Math.Round(r * Math.Clamp(1 - accumulatedGdd / windowCloseGdd, 0m, 1m), 4)
            : 0m;

    /// <summary>Pérdidas tardías que faltan: la tasa histórica en proporción a lo que queda del cierre Baby Leaf al óptimo convencional.</summary>
    public static decimal RemainingLateLoss(decimal? rate, decimal accumulatedGdd, decimal windowCloseGdd, decimal conventionalOptimumGdd)
    {
        if (rate is not { } r)
            return 0m;
        if (conventionalOptimumGdd <= windowCloseGdd)
            return accumulatedGdd >= conventionalOptimumGdd ? 0m : r;
        return Math.Round(r * Math.Clamp(1 - (accumulatedGdd - windowCloseGdd) / (conventionalOptimumGdd - windowCloseGdd), 0m, 1m), 4);
    }
}
