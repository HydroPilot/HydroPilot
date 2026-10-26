using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Forecasting;

namespace HydroPilotWeb.Services.Lotes;

/// <summary>
/// Objetivos editables del lote. El umbral de cosecha decide el "listo" de la regla híbrida
/// en Forecasting y Simulación; la vista Lotes no lo usa (sus cortes van por
/// GDD). Las dos metas del ciclo MIDEN la producción planificada: el % de plantas que se
/// cosechan como Baby Leaf y el % de plantas que se espera que lleguen al final sin descartarse.
/// Null = sin definir (nunca se reemplaza por una constante).
/// </summary>
public sealed record LotObjectives(
    decimal? HarvestThresholdPercent,
    decimal? BabyLeafGoalPercent,
    decimal? ExpectedActivePercent);

public enum ActiveGoalStatus { SinMeta, SinPlantas, Dentro, PorDebajo }

/// <summary>
/// Metas de producción del ciclo (lote): avance de la meta Baby Leaf y plantas activas contra
/// las esperadas al final. Se miden sobre las plantas del lote (activas + cosechadas +
/// descartadas); las posiciones vacías de la grilla no cuentan. Lógica pura.
/// </summary>
public static class LotGoalsRules
{
    public sealed record Input(
        int TotalPlants,
        int HarvestedAsBabyLeaf,
        int AptaActive,
        int CandidateActive,
        int Discarded,
        decimal? BabyLeafGoalPercent,
        decimal? ExpectedActivePercent);

    public sealed record Result(
        int AchievedCount,           // cosechadas como Baby Leaf + aptas
        decimal AchievedPercent,
        int PotentialCount,          // + candidatas (si llegan a aptas)
        decimal PotentialPercent,
        int ActiveCount,             // plantas no descartadas (activas + cosechadas)
        decimal ActivePercent,       // ActiveCount / total
        ActiveGoalStatus ActiveStatus);

    public static Result Evaluate(Input input)
    {
        var total = Math.Max(0, input.TotalPlants);
        var achieved = input.HarvestedAsBabyLeaf + input.AptaActive;
        var potential = achieved + input.CandidateActive;

        var achievedPct = Percent(achieved, total);
        var potentialPct = Percent(potential, total);
        // Una planta cosechada no se perdió: cuenta como activa hasta el final del ciclo.
        var active = Math.Max(0, total - input.Discarded);
        int? expectedPlants = input.ExpectedActivePercent is { } expected ? PlantsFor(expected, total) : null;

        // Se compara en plantas (ceil(meta × total)), no en % redondeado: 55 de 60 = 91,7 % no llega a una meta de 91,7 % (56).
        var activeStatus = expectedPlants switch
        {
            null => ActiveGoalStatus.SinMeta,
            _ when total == 0 => ActiveGoalStatus.SinPlantas,
            { } needed when active < needed => ActiveGoalStatus.PorDebajo,
            _ => ActiveGoalStatus.Dentro
        };

        return new Result(achieved, achievedPct, potential, potentialPct, active, Percent(active, total), activeStatus);
    }

    /// <summary>Plantas que representa un % del lote: ceil(% × total), lo mínimo para llegar al %.</summary>
    public static int PlantsFor(decimal percent, int total) =>
        (int)Math.Ceiling(percent * Math.Max(0, total) / 100m);

    private static decimal Percent(int count, int total) =>
        total > 0 ? Math.Round(count * 100m / total, 1) : 0m;

    /// <summary>
    /// % a mostrar por tramo de una barra apilada, con redondeo acumulado: cada tramo es la
    /// diferencia entre % acumulados redondeados con la misma regla que las metas. Así las sumas
    /// parciales coinciden con el logrado y el potencial Baby Leaf, y el total da 100 %, aunque
    /// un tramo suelto pueda diferir en 0,1 de su propio redondeo.
    /// </summary>
    public static IReadOnlyList<decimal> CumulativeShares(IReadOnlyList<int> counts, int total)
    {
        var shares = new List<decimal>(counts.Count);
        var cumulative = 0;
        var previous = 0m;
        foreach (var count in counts)
        {
            cumulative += Math.Max(0, count);
            var rounded = Percent(cumulative, total);
            shares.Add(rounded - previous);
            previous = rounded;
        }
        return shares;
    }
}

/// <summary>Validación y normalización de los objetivos editables del lote.</summary>
public static class LotObjectivesRules
{
    public static IReadOnlyList<string> Validate(LotObjectives input)
    {
        var errors = new List<string>();

        if (input.HarvestThresholdPercent is { } threshold && (threshold <= 0m || threshold > 100m))
            errors.Add("El umbral de cosecha tiene que ser mayor a 0 % y hasta 100 %.");

        if (input.BabyLeafGoalPercent is { } goal && (goal < 0m || goal > 100m))
            errors.Add("La meta Baby Leaf tiene que estar entre 0 % y 100 %.");

        if (input.ExpectedActivePercent is { } active && (active < 0m || active > 100m))
            errors.Add("Las plantas activas esperadas tienen que estar entre 0 % y 100 %.");

        return errors;
    }

    /// <summary>Redondea a 2 decimales (precisión de las columnas decimal(5,2)).</summary>
    public static LotObjectives Normalize(LotObjectives input) => new(
        Round(input.HarvestThresholdPercent),
        Round(input.BabyLeafGoalPercent),
        Round(input.ExpectedActivePercent));

    private static decimal? Round(decimal? value) =>
        value is { } v ? Math.Round(v, 2, MidpointRounding.AwayFromZero) : null;
}

/// <summary>
/// Peso promedio por planta (g), parámetro del sistema (Ajustes &gt; Parámetros). En hidroponía
/// cada planta tiene su hueco, así que el rendimiento se estima por plantas y no por m².
/// Null = sin definir: no se inventa un valor.
/// </summary>
public sealed record YieldReference(decimal? BabyLeafGramsPerPlant, decimal? ConventionalGramsPerPlant)
{
    public bool IsComplete => BabyLeafGramsPerPlant is not null && ConventionalGramsPerPlant is not null;
}

/// <summary>
/// Precio por kg vigente de cada destino, cargado por el usuario (los precios demo del catálogo
/// no cuentan); null = sin definir.
/// </summary>
public sealed record YieldPrices(decimal? BabyLeafPerKg, decimal? ConventionalPerKg, string Currency)
{
    public static readonly YieldPrices None = new(null, null, "ARS");

    public bool IsComplete => BabyLeafPerKg is not null && ConventionalPerKg is not null;
}

/// <summary>Kg y $ de un destino: plantas × peso de referencia (× precio). Null si falta el peso o el precio.</summary>
public sealed record DestinationYield(int Plants, decimal? Kg, decimal? Revenue);

public sealed record YieldScenario(DestinationYield BabyLeaf, DestinationYield Conventional);

/// <summary>
/// Cómo terminaron los lotes cerrados del invernadero y cultivo que se usan para el esperado: % de las
/// plantas sembradas cosechadas como Baby Leaf y como convencional (el resto se descartó).
/// SameSeason = lotes sembrados en la misma época del año; false = todos los cerrados (no había suficientes).
/// </summary>
public sealed record YieldHistory(int Lots, bool SameSeason, decimal BabyLeafShare, decimal ConventionalShare);

/// <summary>
/// De dónde sale el esperado: LotWeight = peso de la situación actual del lote (0 = solo el histórico,
/// 1 = solo el lote), según la fase del ciclo; History = null si no hay lotes cerrados. AdultRoom = huecos libres de
/// Adultas que el lote todavía puede usar (null = sin tope); AdultsCapped = el tope bajó el esperado de adultas.
/// </summary>
public sealed record YieldBasis(decimal LotWeight, YieldHistory? History, int? AdultRoom = null, bool AdultsCapped = false);

/// <summary>
/// Rendimiento del lote por destino. Actual: lo cosechado más lo listo para cosechar hoy (aptas para Baby Leaf;
/// adultas que llegaron a su corte). Esperado: lo que va a dar el ciclo completo (incluye lo ya cosechado), mezclando
/// el histórico con la situación del lote según la fase (ver LotYieldRules.Estimate).
/// </summary>
public sealed record LotYieldEstimate(YieldScenario Current, YieldScenario Expected, YieldBasis Basis, YieldReference Reference, YieldPrices Prices);

/// <summary>
/// Rendimiento real de un ciclo cerrado: plantas cosechadas de cada destino × peso y precio
/// guardados al cosechar el lote (no los parámetros de hoy).
/// </summary>
public sealed record LotHarvestYield(YieldScenario Real, YieldReference Reference, YieldPrices Prices)
{
    /// <summary>Ingreso total; null si falta el de algún destino que tuvo plantas cosechadas.</summary>
    public decimal? TotalRevenue =>
        (Real.BabyLeaf.Plants == 0 || Real.BabyLeaf.Revenue is not null)
        && (Real.Conventional.Plants == 0 || Real.Conventional.Revenue is not null)
            ? (Real.BabyLeaf.Revenue ?? 0m) + (Real.Conventional.Revenue ?? 0m)
            : null;
}

public static class LotYieldRules
{
    /// <summary>
    /// Plantas del lote por estado; las cosechadas cuentan para el actual y el esperado (son parte del ciclo).
    /// AdultsReady = las adultas llegaron a su corte (<see cref="CycleTimeline.AdultCutReached"/>).
    /// AdultRoom = huecos libres de Adultas que el lote todavía puede usar (0 si ya no se puede trasplantar; null = sin
    /// tope, sin trasplante).
    /// Developing = las en desarrollo del circuito denso; Conventional = las activas de la ruta Adulta (trasplantadas, en
    /// desarrollo o ya adultas): ver <see cref="LotPlantBreakdown.DenseDeveloping"/> y <see cref="LotPlantBreakdown.Adults"/>.
    /// </summary>
    public sealed record Input(
        int Apta, int Candidate, int Developing, int Conventional,
        int Risk = 0, int HarvestedBabyLeaf = 0, int HarvestedConventional = 0, bool AdultsReady = false,
        int? AdultRoom = null);

    /// <summary>
    /// Actual: lo cosechado de cada destino más lo listo hoy: aptas × peso Baby Leaf y, si llegaron a su corte, las
    /// adultas × peso adulta.
    /// Esperado del ciclo = (1 − w) × histórico + w × situación del lote, con w = LotWeight:
    /// - Situación del lote: cosechadas de cada destino + aptas y candidatas a Baby Leaf; las adultas y las en
    ///   desarrollo que entran en los huecos libres de Adultas, a adulta (las en riesgo y las que no entran se
    ///   cuentan como pérdida).
    /// - Histórico: cosechadas + las activas repartidas con el % de Baby Leaf y de convencional de los
    ///   lotes cerrados (el resto de ese reparto es lo que se suele descartar).
    /// Sin histórico, solo la situación del lote. Las plantas se redondean al entero. Después:
    /// - adultas: como mucho las que ya lo son (cosechadas y activas) más los huecos libres (son las trasplantadas);
    /// - cada destino, nunca menos que el actual (lo cosechado y lo listo ya están).
    /// </summary>
    public static LotYieldEstimate Estimate(
        Input plants, YieldReference reference, YieldPrices prices, YieldHistory? history = null, decimal lotWeight = 1m)
    {
        DestinationYield BabyLeaf(int count) => Destination(count, reference.BabyLeafGramsPerPlant, prices.BabyLeafPerKg);
        DestinationYield Conventional(int count) => Destination(count, reference.ConventionalGramsPerPlant, prices.ConventionalPerKg);

        var weight = history is null ? 1m : Math.Clamp(lotWeight, 0m, 1m);
        var adults = plants.HarvestedConventional + plants.Conventional;
        var futureAdults = plants.AdultRoom is { } room ? Math.Min(plants.Developing, room) : plants.Developing;
        decimal lotBabyLeaf = plants.HarvestedBabyLeaf + plants.Apta + plants.Candidate;
        decimal lotConventional = adults + futureAdults;
        decimal uncappedConventional = adults + plants.Developing;
        var (babyLeaf, conventional) = (lotBabyLeaf, lotConventional);

        if (history is not null)
        {
            var active = plants.Apta + plants.Candidate + plants.Developing + plants.Conventional + plants.Risk;
            var historyBabyLeaf = plants.HarvestedBabyLeaf + history.BabyLeafShare * active;
            var historyConventional = plants.HarvestedConventional + history.ConventionalShare * active;
            babyLeaf = (1m - weight) * historyBabyLeaf + weight * lotBabyLeaf;
            conventional = (1m - weight) * historyConventional + weight * lotConventional;
            uncappedConventional = (1m - weight) * historyConventional + weight * uncappedConventional;
        }

        // Las adultas son las trasplantadas: no pueden ser más que las que ya lo son más los huecos libres.
        if (plants.AdultRoom is { } free)
            conventional = Math.Min(conventional, adults + free);

        var current = new YieldScenario(
            BabyLeaf(plants.HarvestedBabyLeaf + plants.Apta),
            Conventional(plants.HarvestedConventional + (plants.AdultsReady ? plants.Conventional : 0)));
        var capped = plants.AdultRoom is not null && Plants(uncappedConventional) > Plants(conventional);
        return new LotYieldEstimate(
            current,
            new YieldScenario(
                BabyLeaf(Math.Max(Plants(babyLeaf), current.BabyLeaf.Plants)),
                Conventional(Math.Max(Plants(conventional), current.Conventional.Plants))),
            new YieldBasis(weight, history, plants.AdultRoom, capped),
            reference,
            prices);
    }

    /// <summary>
    /// Peso de la situación del lote en el esperado, según la fase: en desarrollo 0 (todo histórico);
    /// en la ventana Baby Leaf crece con el GDD, de 0 cuando abre a 1 cuando cierra; en la ventana
    /// convencional y después, 1 (las plantas ya tienen destino). Sin línea de tiempo, 1.
    /// </summary>
    public static decimal LotWeight(CycleTimeline? timeline)
    {
        if (timeline is null)
            return 1m;
        var opens = timeline.BabyLeafOpens.Gdd;
        var closes = timeline.BabyLeafCloses.Gdd;
        return timeline.Phase switch
        {
            CyclePhase.Desarrollo => 0m,
            CyclePhase.VentanaBabyLeaf when closes > opens =>
                Math.Round(Math.Clamp((timeline.AccumulatedGdd - opens) / (closes - opens), 0m, 1m), 2),
            _ => 1m
        };
    }

    private static int Plants(decimal count) => (int)Math.Round(Math.Max(0m, count), MidpointRounding.AwayFromZero);

    /// <summary>
    /// Trasplantadas activas que todavía no son "Adulta" (siguen en desarrollo hasta su corte), sin las en riesgo.
    /// Son de la ruta Adulta: no cuentan como en desarrollo del denso.
    /// </summary>
    public static int TransplantedDeveloping(IEnumerable<PlantCellDto> cells) =>
        cells.Count(c => c.IsAdultRoute && c.PlantId is not null && c.OperationalState == PlantOperationalState.Activa
                         && c.CommercialStageName is not (CommercialStageNames.CosechaConvencional or CommercialStageNames.RiesgoFueraDeVentana));

    /// <summary>Ciclo cerrado: cosechadas como Baby Leaf y como convencional, con la referencia de la cosecha.</summary>
    public static LotHarvestYield Harvested(int harvestedBabyLeaf, int harvestedConventional, YieldReference reference, YieldPrices prices) => new(
        new YieldScenario(
            Destination(harvestedBabyLeaf, reference.BabyLeafGramsPerPlant, prices.BabyLeafPerKg),
            Destination(harvestedConventional, reference.ConventionalGramsPerPlant, prices.ConventionalPerKg)),
        reference,
        prices);

    private static DestinationYield Destination(int plants, decimal? gramsPerPlant, decimal? pricePerKg)
    {
        var count = Math.Max(0, plants);
        if (gramsPerPlant is not { } grams)
            return new DestinationYield(count, null, null);

        var kg = count * grams / 1000m;
        return new DestinationYield(
            count,
            Math.Round(kg, 2, MidpointRounding.AwayFromZero),
            pricePerKg is { } price ? Math.Round(kg * price, 0, MidpointRounding.AwayFromZero) : null);
    }
}

/// <summary>
/// Histórico para el rendimiento esperado: cómo terminaron los lotes cerrados (cosechados o
/// descartados) del mismo invernadero y cultivo. Se usan los sembrados en la misma época del año
/// (± SeasonDays, de cualquier año) si hay al menos MinSeasonLots; si no, todos los cerrados.
/// Los % son sobre el total de plantas sembradas de esos lotes (un lote grande pesa más).
/// </summary>
public static class LotYieldHistoryRules
{
    public const int SeasonDays = 45;
    public const int MinSeasonLots = 3;

    public sealed record ClosedLot(DateOnly SowingDate, int TotalPlants, int HarvestedBabyLeaf, int HarvestedConventional);

    public static YieldHistory? Build(DateOnly sowingDate, IEnumerable<ClosedLot> closedLots)
    {
        var usable = closedLots.Where(l => l.TotalPlants > 0).ToList();
        if (usable.Count == 0)
            return null;

        var season = usable.Where(l => DaysApartInYear(l.SowingDate, sowingDate) <= SeasonDays).ToList();
        var sameSeason = season.Count >= MinSeasonLots;
        var lots = sameSeason ? season : usable;

        decimal total = lots.Sum(l => l.TotalPlants);
        return new YieldHistory(
            lots.Count,
            sameSeason,
            lots.Sum(l => l.HarvestedBabyLeaf) / total,
            lots.Sum(l => l.HarvestedConventional) / total);
    }

    /// <summary>Días entre dos fechas mirando solo el día del año (15/12 y 10/01 están a 26 días).</summary>
    public static int DaysApartInYear(DateOnly a, DateOnly b)
    {
        var days = Math.Abs(a.DayOfYear - b.DayOfYear);
        return Math.Min(days, 365 - days);
    }
}

/// <summary>
/// Parámetros de rendimiento del sistema (Ajustes &gt; Parámetros): peso promedio por planta y
/// precio por kg de cada destino. Un campo vacío lo deja sin definir, como en las metas.
/// </summary>
public sealed record YieldReferenceInput(
    decimal? BabyLeafGrams,
    decimal? ConventionalGrams,
    decimal? BabyLeafPricePerKg,
    decimal? ConventionalPricePerKg);

public static class YieldReferenceRules
{
    public const decimal MaxGramsPerPlant = 5000m;
    public const decimal MaxPricePerKg = 10_000_000m;

    public static IReadOnlyList<string> Validate(YieldReferenceInput input)
    {
        var errors = new List<string>();
        Weight(input.BabyLeafGrams, "una planta Baby Leaf");
        Weight(input.ConventionalGrams, "una lechuga adulta");
        Price(input.BabyLeafPricePerKg, "Baby Leaf");
        Price(input.ConventionalPricePerKg, "adulta");
        return errors;

        void Weight(decimal? grams, string what)
        {
            if (grams is { } value && (value <= 0m || value > MaxGramsPerPlant))
                errors.Add($"El peso promedio de {what} tiene que ser mayor a 0 g y hasta {MaxGramsPerPlant:0} g.");
        }

        void Price(decimal? price, string what)
        {
            if (price is { } value && (value <= 0m || value > MaxPricePerKg))
                errors.Add($"El precio por kg {what} tiene que ser mayor a 0.");
        }
    }

    /// <summary>Gramos a 1 decimal y precios a centavos; los vacíos quedan sin definir.</summary>
    public static YieldReferenceInput Normalize(YieldReferenceInput input) => new(
        Round(input.BabyLeafGrams, 1),
        Round(input.ConventionalGrams, 1),
        Round(input.BabyLeafPricePerKg, 2),
        Round(input.ConventionalPricePerKg, 2));

    private static decimal? Round(decimal? value, int decimals) =>
        value is { } v ? Math.Round(v, decimals, MidpointRounding.AwayFromZero) : null;
}

public enum DataQualityAlertKind { SyntheticReadings, NoReadings, MissingDays }

/// <summary>Problema de datos visible sin abrir el detalle técnico: título corto y qué implica.</summary>
public sealed record DataQualityAlert(DataQualityAlertKind Kind, string Title, string Consequence);

/// <summary>
/// Calidad del dato que resta confiabilidad a la fase y las fechas del lote (se marca en la sección
/// de detalles técnicos): temperaturas sintéticas o días sin lecturas. La fecha extrapolada no
/// entra: es lo normal antes de los últimos 7 días del ciclo y ya se marca con "≈".
/// </summary>
public static class LotDataQualityRules
{
    /// <summary>
    /// Avisos de la fecha de cosecha del forecast (umbral de aptas, fecha por GDD objetivo, criterio de
    /// tamaño convencional; LotAggregateService / ForecastService). La vista Lotes no usa esa fecha: sus
    /// cortes salen de los óptimos por GDD (CycleTimelineLogic) y la extrapolación se marca con "≈".
    /// </summary>
    public static readonly IReadOnlyList<string> HarvestDateWarningPrefixes =
    [
        "Porcentaje de Baby Leafs (",
        "Porcentaje objetivo de Baby Leafs no configurado",
        "La fecha por GDD (",
        "Cosecha adulta: sin criterio de tamaño",
    ];

    public static IReadOnlyList<DataQualityAlert> Alerts(ForecastSourceKind sourceKind, int coverageDays, int missingDays)
    {
        var alerts = new List<DataQualityAlert>();
        if (sourceKind == ForecastSourceKind.Mock)
            alerts.Add(new(DataQualityAlertKind.SyntheticReadings, "Temperaturas sintéticas (datos demo)",
                "la fase y las fechas del lote no son reales."));

        if (missingDays > 0 && coverageDays == 0)
            alerts.Add(new(DataQualityAlertKind.NoReadings, "Sin lecturas de temperatura",
                "el GDD no avanza y la fase y las fechas no son confiables."));
        else if (missingDays > 0)
            alerts.Add(new(DataQualityAlertKind.MissingDays,
                $"Faltan lecturas de temperatura de {missingDays} {(missingDays == 1 ? "día" : "días")}",
                "el GDD puede estar subestimado."));

        return alerts;
    }

    /// <summary>
    /// Avisos técnicos (GddService / LotAggregateService) que cada alerta ya explica con otras palabras.
    /// ForecastService detecta los datos sintéticos con el mismo texto.
    /// </summary>
    private static readonly IReadOnlyDictionary<DataQualityAlertKind, string[]> CoveredWarnings =
        new Dictionary<DataQualityAlertKind, string[]>
        {
            [DataQualityAlertKind.SyntheticReadings] = ["lecturas sintéticas"],
            [DataQualityAlertKind.NoReadings] =
            [
                "sin lectura usable", "Sin lecturas de temperatura", "No hay lecturas de temperatura",
                "Sin promedio de días observados",
            ],
            [DataQualityAlertKind.MissingDays] = ["sin lectura usable"],
        };

    /// <summary>
    /// Avisos técnicos a listar: sin los que ya explica una alerta (para no decir lo mismo dos veces)
    /// ni los de la fecha de cosecha del forecast.
    /// </summary>
    public static IReadOnlyList<string> RemainingWarnings(IEnumerable<string> warnings, IReadOnlyList<DataQualityAlert> alerts)
    {
        var covered = alerts.SelectMany(a => CoveredWarnings[a.Kind]).ToList();
        return warnings
            .Where(w => !HarvestDateWarningPrefixes.Any(prefix => w.StartsWith(prefix, StringComparison.Ordinal)))
            .Where(w => !covered.Any(marker => w.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}

/// <summary>Fase del lote según su GDD (mismos límites que PlantDecisionEngine).</summary>
public enum CyclePhase { Desarrollo, VentanaBabyLeaf, VentanaConvencional, Sobremadurez }

/// <summary>
/// Hito del ciclo por GDD. Date null = no estimable. IsEstimate = todavía no ocurrió
/// (sale del pronóstico); IsExtrapolated = cae más allá del horizonte del pronóstico.
/// </summary>
public sealed record CycleMilestone(decimal Gdd, DateOnly? Date, bool IsEstimate, bool IsExtrapolated);

public enum HarvestDestination { BabyLeaf, Convencional }

/// <summary>
/// Próximo corte del lote: el óptimo y el límite (cierre de la ventana) de su destino.
/// CutNow = ya pasó el óptimo: se corta lo que corresponda a ese destino, se llegue o no a
/// la meta; lo que no se cortó como Baby Leaf sigue creciendo para convencional.
/// </summary>
public sealed record HarvestCut(HarvestDestination Destination, CycleMilestone Optimum, CycleMilestone Limit, bool CutNow);

public sealed record CycleTimeline(
    DateOnly SowingDate,
    DateOnly AsOfDate,
    int CycleDay,
    CyclePhase Phase,
    decimal AccumulatedGdd,
    CycleMilestone BabyLeafOpens,
    CycleMilestone BabyLeafOptimum,
    CycleMilestone BabyLeafCloses,
    CycleMilestone ConventionalOptimum,
    CycleMilestone Overmaturity,
    CycleMilestone? Transplant = null,                          // decisión de trasplante a Adultas (GDD de Parámetros)
    OptimumSource BabyLeafOptimumSource = OptimumSource.Fabrica) // de dónde sale el óptimo Baby Leaf
{
    /// <summary>Corte Baby Leaf hasta que cierra su ventana; después, el convencional. Null en sobremadurez.</summary>
    public HarvestCut? NextCut => Phase switch
    {
        CyclePhase.Desarrollo or CyclePhase.VentanaBabyLeaf => new HarvestCut(
            HarvestDestination.BabyLeaf, BabyLeafOptimum, BabyLeafCloses, AccumulatedGdd >= BabyLeafOptimum.Gdd),
        CyclePhase.VentanaConvencional => new HarvestCut(
            HarvestDestination.Convencional, ConventionalOptimum, Overmaturity, AdultCutReached),
        _ => null
    };

    /// <summary>
    /// La Adulta llegó a su corte: el GDD de su óptimo (650) y los días de estrés del trasplante después (la fecha del
    /// óptimo ya los suma). Sin fecha (el historial no tiene el día), alcanza con el GDD.
    /// </summary>
    public bool AdultCutReached =>
        AccumulatedGdd >= ConventionalOptimum.Gdd && (ConventionalOptimum.Date is not { } date || AsOfDate >= date);
}

/// <summary>
/// Línea de tiempo del ciclo: convierte los umbrales de GDD (apertura, óptimo de corte y
/// cierre de cada ventana) en fechas. Hitos ya alcanzados → fecha observada en el
/// historial; hitos futuros → GddDateLogic sobre la proyección. Lógica pura.
/// </summary>
public static class CycleTimelineLogic
{
    /// <summary>
    /// Óptimos de corte por GDD de fábrica. El de Baby Leaf sale de la curva de aptas (<see cref="BabyLeafOptimumRules"/>)
    /// y este es el respaldo sin historial; el de Adulta (convencional) es el margen antes de la sobremadurez, y su
    /// fecha se corre los días de estrés del trasplante.
    /// </summary>
    public const decimal BabyLeafOptimumGdd = BabyLeafOptimumRules.FactoryOptimumGdd;
    public const decimal ConventionalOptimumGdd = 650m;

    public static CyclePhase PhaseFor(decimal gdd, decimal babyLeafMin, decimal babyLeafMax, decimal overmaturity) =>
        gdd < babyLeafMin ? CyclePhase.Desarrollo
        : gdd < babyLeafMax ? CyclePhase.VentanaBabyLeaf
        : gdd <= overmaturity ? CyclePhase.VentanaConvencional
        : CyclePhase.Sobremadurez;

    public static CycleMilestone Milestone(
        decimal threshold,
        decimal accumulated,
        IReadOnlyList<DailyGddPoint> history,
        IReadOnlyList<DailyGddPoint> projection)
    {
        if (accumulated >= threshold)
        {
            decimal cumulative = 0m;
            foreach (var point in history)
            {
                cumulative += point.Gdd;
                if (cumulative >= threshold)
                    return new CycleMilestone(threshold, point.Date, IsEstimate: false, IsExtrapolated: false);
            }
            return new CycleMilestone(threshold, null, IsEstimate: false, IsExtrapolated: false);
        }

        var date = GddDateLogic.EstimateCrossDate(accumulated, projection, threshold);
        var extrapolated = date is { } d && (projection.Count == 0 || d > projection[^1].Date);
        return new CycleMilestone(threshold, date, IsEstimate: true, IsExtrapolated: extrapolated);
    }

    public static CycleTimeline Build(
        DateOnly sowingDate,
        DateOnly asOfDate,
        decimal accumulated,
        IReadOnlyList<DailyGddPoint> history,
        IReadOnlyList<DailyGddPoint> projection,
        decimal babyLeafMin,
        decimal babyLeafMax,
        decimal overmaturity,
        decimal babyLeafOptimum = BabyLeafOptimumGdd,
        decimal conventionalOptimum = ConventionalOptimumGdd,
        int adultDelayDays = 0,
        decimal? transplantGdd = null,
        OptimumSource babyLeafOptimumSource = OptimumSource.Fabrica)
    {
        // Cada óptimo queda dentro de su ventana aunque la configuración del cultivo cambie.
        static decimal Within(decimal value, decimal min, decimal max) => Math.Min(Math.Max(value, min), max);

        // La Adulta frena los días de estrés del trasplante: su corte y su sobremadurez se corren esos días.
        CycleMilestone Delayed(CycleMilestone milestone) =>
            adultDelayDays > 0 && milestone.Date is { } date ? milestone with { Date = date.AddDays(adultDelayDays) } : milestone;

        return new(
            sowingDate,
            asOfDate,
            Math.Max(0, asOfDate.DayNumber - sowingDate.DayNumber),
            PhaseFor(accumulated, babyLeafMin, babyLeafMax, overmaturity),
            accumulated,
            Milestone(babyLeafMin, accumulated, history, projection),
            Milestone(Within(babyLeafOptimum, babyLeafMin, babyLeafMax), accumulated, history, projection),
            Milestone(babyLeafMax, accumulated, history, projection),
            Delayed(Milestone(Within(conventionalOptimum, babyLeafMax, overmaturity), accumulated, history, projection)),
            Delayed(Milestone(overmaturity, accumulated, history, projection)),
            transplantGdd is { } decision ? Milestone(decision, accumulated, history, projection) : null,
            babyLeafOptimumSource);
    }
}

/// <summary>
/// Día de la curva de GDD: GDD del día y acumulado al cierre del día. Source null = día sin
/// lectura de temperatura (no suma: el acumulado queda plano).
/// </summary>
public sealed record GddCurveDay(DateOnly Date, decimal? DailyGdd, decimal Cumulative, GddPointSource? Source);

/// <summary>GDD acumulado día a día: lo medido (de la siembra a la fecha) y la proyección que sigue.</summary>
public sealed record GddCurve(DateOnly SowingDate, DateOnly AsOfDate, IReadOnlyList<GddCurveDay> Measured, IReadOnlyList<GddCurveDay> Projected)
{
    public DateOnly EndDate => Projected.Count > 0 ? Projected[^1].Date : AsOfDate;

    public decimal MaxCumulative => Projected.Count > 0 ? Projected[^1].Cumulative : Measured.Count > 0 ? Measured[^1].Cumulative : 0m;
}

/// <summary>
/// Arma la curva de GDD del lote con los mismos datos del pronóstico (historial y proyección de
/// 7 días). Los días sin lectura quedan marcados para verlos en el gráfico. Lógica pura.
/// </summary>
public static class GddCurveLogic
{
    public static GddCurve Build(
        DateOnly sowingDate,
        DateOnly asOfDate,
        IReadOnlyList<DailyGddPoint> history,
        IReadOnlyList<DailyGddPoint> projection)
    {
        // Siembra futura (escenario de Simulación para planificar): no hay nada medido todavía y la
        // proyección arranca el día de la siembra.
        var byDate = history.DistinctBy(p => p.Date).ToDictionary(p => p.Date);
        var measured = new List<GddCurveDay>();
        var cumulative = 0m;
        for (var date = sowingDate; date <= asOfDate; date = date.AddDays(1))
        {
            if (byDate.TryGetValue(date, out var point))
            {
                cumulative += point.Gdd;
                measured.Add(new GddCurveDay(date, point.Gdd, cumulative, point.Source));
            }
            else
            {
                measured.Add(new GddCurveDay(date, null, cumulative, null));
            }
        }

        var projected = new List<GddCurveDay>();
        foreach (var point in projection.Where(p => p.Date > asOfDate && p.Date >= sowingDate).OrderBy(p => p.Date))
        {
            cumulative += point.Gdd;
            projected.Add(new GddCurveDay(point.Date, point.Gdd, cumulative, point.Source));
        }

        return new GddCurve(sowingDate, asOfDate, measured, projected);
    }
}
