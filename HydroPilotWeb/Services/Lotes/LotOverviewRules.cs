using HydroPilotWeb.Services.Forecasting;

namespace HydroPilotWeb.Services.Lotes;

/// <summary>
/// Objetivos editables del lote. El umbral de cosecha DECIDE el corte del lote
/// (regla híbrida); las dos metas del ciclo MIDEN la producción planificada.
/// Null = sin definir (nunca se reemplaza por una constante).
/// </summary>
public sealed record LotObjectives(
    decimal? HarvestThresholdPercent,
    decimal? BabyLeafGoalPercent,
    decimal? MaxLossPercent);

public enum BabyLeafGoalStatus { SinMeta, SinPlantas, Cumplida, Alcanzable, PorDebajo }

public enum LossGoalStatus { SinMeta, SinPlantas, Dentro, EnRiesgo, Excedida }

/// <summary>
/// Metas de producción del ciclo (lote): avance de la meta Baby Leaf y control de
/// pérdidas. Se miden sobre las plantas del lote (activas + cosechadas +
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
        int RiskActive,
        decimal? BabyLeafGoalPercent,
        decimal? MaxLossPercent);

    public sealed record Result(
        int AchievedCount,           // cosechadas como Baby Leaf + aptas
        decimal AchievedPercent,
        int PotentialCount,          // + candidatas (si llegan a aptas)
        decimal PotentialPercent,
        int PlantsMissingForGoal,    // plantas que faltan para la meta con lo logrado
        int? GoalPlants,             // plantas que representa la meta: ceil(meta × total); null sin meta
        BabyLeafGoalStatus BabyLeafStatus,
        decimal LossPercent,         // descartadas / total
        decimal LossWithRiskPercent, // (descartadas + en riesgo) / total
        LossGoalStatus LossStatus);

    public static Result Evaluate(Input input)
    {
        var total = Math.Max(0, input.TotalPlants);
        var achieved = input.HarvestedAsBabyLeaf + input.AptaActive;
        var potential = achieved + input.CandidateActive;

        var achievedPct = Percent(achieved, total);
        var potentialPct = Percent(potential, total);
        var lossPct = Percent(input.Discarded, total);
        var lossWithRiskPct = Percent(input.Discarded + input.RiskActive, total);

        int? goalPlants = input.BabyLeafGoalPercent is { } goal
            ? (int)Math.Ceiling(goal * total / 100m)
            : null;
        var missing = goalPlants is { } needed ? Math.Max(0, needed - achieved) : 0;

        var babyLeafStatus = input.BabyLeafGoalPercent switch
        {
            null => BabyLeafGoalStatus.SinMeta,
            _ when total == 0 => BabyLeafGoalStatus.SinPlantas,
            { } g when achievedPct >= g => BabyLeafGoalStatus.Cumplida,
            { } g when potentialPct >= g => BabyLeafGoalStatus.Alcanzable,
            _ => BabyLeafGoalStatus.PorDebajo
        };

        var lossStatus = input.MaxLossPercent switch
        {
            null => LossGoalStatus.SinMeta,
            _ when total == 0 => LossGoalStatus.SinPlantas,
            { } max when lossPct > max => LossGoalStatus.Excedida,
            { } max when lossWithRiskPct > max => LossGoalStatus.EnRiesgo,
            _ => LossGoalStatus.Dentro
        };

        return new Result(achieved, achievedPct, potential, potentialPct, missing, goalPlants,
            babyLeafStatus, lossPct, lossWithRiskPct, lossStatus);
    }

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

        if (input.MaxLossPercent is { } loss && (loss < 0m || loss > 100m))
            errors.Add("Las pérdidas máximas tienen que estar entre 0 % y 100 %.");

        return errors;
    }

    /// <summary>Redondea a 2 decimales (precisión de las columnas decimal(5,2)).</summary>
    public static LotObjectives Normalize(LotObjectives input) => new(
        Round(input.HarvestThresholdPercent),
        Round(input.BabyLeafGoalPercent),
        Round(input.MaxLossPercent));

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
/// Rendimiento del lote por destino. Actual: aptas (Baby Leaf) y convencionales.
/// Potencial: aptas + candidatas (Baby Leaf) y en desarrollo + convencionales.
/// </summary>
public sealed record LotYieldEstimate(YieldScenario Current, YieldScenario Potential, YieldReference Reference, YieldPrices Prices);

public static class LotYieldRules
{
    public sealed record Input(int Apta, int Candidate, int Developing, int Conventional);

    public static LotYieldEstimate Estimate(Input plants, YieldReference reference, YieldPrices prices)
    {
        DestinationYield BabyLeaf(int count) => Destination(count, reference.BabyLeafGramsPerPlant, prices.BabyLeafPerKg);
        DestinationYield Conventional(int count) => Destination(count, reference.ConventionalGramsPerPlant, prices.ConventionalPerKg);

        return new LotYieldEstimate(
            new YieldScenario(BabyLeaf(plants.Apta), Conventional(plants.Conventional)),
            new YieldScenario(BabyLeaf(plants.Apta + plants.Candidate), Conventional(plants.Developing + plants.Conventional)),
            reference,
            prices);
    }

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
        Weight(input.ConventionalGrams, "una lechuga convencional");
        Price(input.BabyLeafPricePerKg, "Baby Leaf");
        Price(input.ConventionalPricePerKg, "convencional");
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
    /// Prefijo del aviso del umbral de cosecha (LotAggregateService / ForecastService). La vista Lotes
    /// no muestra el umbral y su % (aptas / activas) contradice al logrado de la card Baby Leaf.
    /// </summary>
    public const string HarvestThresholdWarningPrefix = "Porcentaje de aptas (";

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
    /// ni el del umbral de cosecha.
    /// </summary>
    public static IReadOnlyList<string> RemainingWarnings(IEnumerable<string> warnings, IReadOnlyList<DataQualityAlert> alerts)
    {
        var covered = alerts.SelectMany(a => CoveredWarnings[a.Kind]).ToList();
        return warnings
            .Where(w => !w.StartsWith(HarvestThresholdWarningPrefix, StringComparison.Ordinal))
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

public sealed record CycleTimeline(
    DateOnly SowingDate,
    DateOnly AsOfDate,
    int CycleDay,
    CyclePhase Phase,
    decimal AccumulatedGdd,
    CycleMilestone BabyLeafOpens,
    CycleMilestone BabyLeafCloses,
    CycleMilestone Overmaturity);

/// <summary>
/// Línea de tiempo del ciclo: convierte los umbrales de GDD (apertura y cierre de la
/// ventana Baby Leaf, sobremadurez) en fechas. Hitos ya alcanzados → fecha observada
/// en el historial; hitos futuros → GddDateLogic sobre la proyección. Lógica pura.
/// </summary>
public static class CycleTimelineLogic
{
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
        decimal overmaturity) => new(
            sowingDate,
            asOfDate,
            Math.Max(0, asOfDate.DayNumber - sowingDate.DayNumber),
            PhaseFor(accumulated, babyLeafMin, babyLeafMax, overmaturity),
            accumulated,
            Milestone(babyLeafMin, accumulated, history, projection),
            Milestone(babyLeafMax, accumulated, history, projection),
            Milestone(overmaturity, accumulated, history, projection));
}
