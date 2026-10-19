using System.Globalization;
using HydroPilotWeb.Models.Optimization;
using HydroPilotWeb.Services.Anomalies;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.Simulation;

namespace HydroPilotWeb.Services.Optimization;

/// <summary>Una estrategia de destino con su margen sobre nutrientes (las semillas son las mismas en todas).</summary>
public sealed record StrategyOption(SimulationStrategyKind Kind, string Name, decimal? Margin);

/// <summary>Estrategias del lote según Simulación: el plan del lote (su meta Baby Leaf) y las alternativas.</summary>
public sealed record StrategyComparison(StrategyOption Plan, IReadOnlyList<StrategyOption> Options, int HistoryLots);

/// <summary>Mínimos para recomendar cambiar de estrategia: la ganancia tiene que superar los dos.</summary>
public sealed record OptimizationThresholds(decimal MinGainAmount, decimal MinGainPercent);

/// <summary>Todo lo que miran las reglas de un lote en curso (sale de Lotes, Simulación y Anomalías).</summary>
public sealed record LotSituation(
    DateOnly Today,
    CycleTimeline? Timeline,
    LotPlantBreakdown Plants,
    LotGoalsRules.Result Goals,
    decimal? BabyLeafGoalPercent,
    decimal? MaxLossPercent,
    YieldReference Weights,
    YieldPrices Prices,
    decimal? NutrientPricePerLiter,
    LotSolutionStatus? Solution,
    StrategyComparison? Strategy,
    OptimizationThresholds Thresholds,
    IReadOnlySet<string>? AutoControlled = null);

/// <summary>
/// Dato de una recomendación para mostrar como número. Kind: money | moneyPerKg | plants | number | pct | date | text.
/// </summary>
public sealed record RecommendationFigure(
    string Label,
    string Kind,
    decimal? Value = null,
    string? Unit = null,
    DateOnly? Date = null,
    string? Text = null);

/// <summary>Un problema del lote con su acción, su impacto en $ y hasta cuándo conviene resolverlo.</summary>
public sealed record OptimizationFinding(
    string Type,
    string Key,
    string Title,
    string Summary,
    string Priority,
    string Direction,
    decimal? Impact,
    string ImpactKind,
    bool IsEstimate,
    DateOnly? DueDate,
    IReadOnlyList<RecommendationFigure> Figures,
    decimal? CurrentValue = null,
    decimal? TargetValue = null)
{
    public string Fingerprint => OptimizationRules.Fingerprint(Type, Key);
}

/// <summary>
/// Resultado de evaluar un lote: los problemas presentes y qué se pudo evaluar. Una recomendación abierta
/// solo se cierra si su problema se evaluó y ya no está (sin datos frescos de pH/CE, por ejemplo, queda como está).
/// </summary>
public sealed record OptimizationEvaluation(
    IReadOnlyList<OptimizationFinding> Findings,
    IReadOnlySet<string> Evaluated,
    bool AllEvaluated = false)
{
    /// <summary>Lote cerrado: no hay nada pendiente, todas las recomendaciones abiertas se cierran.</summary>
    public static OptimizationEvaluation ClosedLot { get; } = new([], new HashSet<string>(), AllEvaluated: true);

    /// <summary>El problema de esta huella se evaluó (por tipo o por huella exacta).</summary>
    public bool WasEvaluated(string fingerprint) =>
        AllEvaluated || Evaluated.Contains(fingerprint) || Evaluated.Contains(fingerprint.Split(':')[0]);
}

/// <summary>
/// Reglas de Optimización (R1–R6), con la lógica de Lotes (ventanas y óptimos de corte por GDD, plantas
/// × peso × precio), de Simulación (estrategias) y de Anomalías (pH/CE). Lógica pura, testeable sin I/O.
/// <list type="bullet">
/// <item>R1 Cortar Baby Leaf: óptimo alcanzado con aptas (alta), o en 3 días o menos con aptas + candidatas (media).</item>
/// <item>R2 Cortar convencional: óptimo alcanzado (alta), en 3 días o menos (media) o pasado de cosecha (alta).</item>
/// <item>R3 Cambiar estrategia: otra estrategia deja más margen que el plan y supera los dos mínimos (alta).</item>
/// <item>R4 Bajar CE: CE firme por encima del rango; ahorro ≈ por semana (supuesto en duda, media).</item>
/// <item>R5 Corregir pH/CE: pH firme fuera de rango, CE firme por debajo, o anomalía abierta (alta).</item>
/// <item>R6 Metas: meta Baby Leaf por debajo del potencial, o pérdidas + en riesgo por encima del máximo (media).</item>
/// </list>
/// </summary>
public static class OptimizationRules
{
    /// <summary>Días antes del óptimo en que se avisa el corte.</summary>
    public const int UpcomingDays = 3;

    /// <summary>Días para corregir la solución (o cortar lo pasado de cosecha) antes de que una aceptada cuente como no cumplida.</summary>
    public const int SolutionDueDays = 1;

    public const string KeyBabyLeafCut = "bl-cut";
    public const string KeyConventionalCut = "conv-cut";
    public const string KeyEcHigh = "ec-high";
    public const string KeyPhLow = "ph-low";
    public const string KeyPhHigh = "ph-high";
    public const string KeyEcLow = "ec-low";
    public const string KeyAnomaly = "anomaly";
    public const string KeyGoal = "goal";
    public const string KeyLoss = "loss";

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-AR");

    public static string Fingerprint(string type, string key) => $"{type}:{key}";

    public static string StrategyKey(SimulationStrategyKind kind) => $"strategy:{kind}";

    /// <summary>Valor de una planta: gramos × precio por kg (null si falta alguno).</summary>
    public static decimal? PlantValue(decimal? grams, decimal? pricePerKg) =>
        grams is { } g && pricePerKg is { } price ? g * price / 1000m : null;

    public static OptimizationEvaluation Evaluate(LotSituation s)
    {
        var findings = new List<OptimizationFinding>();
        var evaluated = new HashSet<string>();

        var babyLeafValue = PlantValue(s.Weights.BabyLeafGramsPerPlant, s.Prices.BabyLeafPerKg);
        var conventionalValue = PlantValue(s.Weights.ConventionalGramsPerPlant, s.Prices.ConventionalPerKg);

        if (s.Timeline is { } timeline)
        {
            evaluated.Add(OptimizationContractType.BabyLeafCut);
            evaluated.Add(OptimizationContractType.ConventionalCut);
            AddIfAny(findings, BabyLeafCut(s, timeline, babyLeafValue));
            AddIfAny(findings, ConventionalCut(s, timeline, conventionalValue));
        }

        if (Strategy(s, evaluated) is { } strategy)
            findings.Add(strategy);

        if (s.Solution is { } solution)
        {
            findings.AddRange(Solution(s, solution, babyLeafValue, conventionalValue, evaluated));
        }

        findings.AddRange(Goals(s, babyLeafValue, conventionalValue, evaluated));

        return new OptimizationEvaluation(findings, evaluated);
    }

    // ------------------------------------------------------------------
    // R1 y R2: cortes (próximo corte de Lotes)
    // ------------------------------------------------------------------

    private static OptimizationFinding? BabyLeafCut(LotSituation s, CycleTimeline t, decimal? plantValue)
    {
        if (t.Phase is not (CyclePhase.Desarrollo or CyclePhase.VentanaBabyLeaf))
            return null;

        var price = new RecommendationFigure("Precio", "moneyPerKg", s.Prices.BabyLeafPerKg);
        if (t.AccumulatedGdd >= t.BabyLeafOptimum.Gdd && s.Plants.Apta > 0)
        {
            var due = t.BabyLeafCloses.Date ?? s.Today;
            return new OptimizationFinding(
                OptimizationContractType.BabyLeafCut, KeyBabyLeafCut,
                "Cortar Baby Leaf ahora",
                $"{s.Plants.Apta} aptas; la ventana Baby Leaf cierra el {Day(due)}.",
                OptimizationContractPriority.High, OptimizationContractDirection.Cut,
                Times(s.Plants.Apta, plantValue), OptimizationImpactKind.Capture, IsEstimate: false, due,
                [
                    new("Aptas", "plants", s.Plants.Apta),
                    price,
                    new("Ventana cierra", "date", Date: due),
                ]);
        }

        if (t.BabyLeafOptimum is { IsEstimate: true, Date: { } optimum }
            && optimum > s.Today && optimum.DayNumber - s.Today.DayNumber <= UpcomingDays
            && s.Plants.Apta + s.Plants.Candidate > 0)
        {
            var plants = s.Plants.Apta + s.Plants.Candidate;
            return new OptimizationFinding(
                OptimizationContractType.BabyLeafCut, KeyBabyLeafCut,
                "Preparar corte Baby Leaf",
                $"Óptimo de corte el {Day(optimum)}: {plants} aptas o candidatas.",
                OptimizationContractPriority.Medium, OptimizationContractDirection.Cut,
                Times(plants, plantValue), OptimizationImpactKind.Capture, IsEstimate: false, t.BabyLeafCloses.Date ?? optimum,
                [
                    new("Aptas + candidatas", "plants", plants),
                    price,
                    new("Óptimo", "date", Date: optimum),
                ]);
        }

        return null;
    }

    private static OptimizationFinding? ConventionalCut(LotSituation s, CycleTimeline t, decimal? plantValue)
    {
        if (t.Phase is not (CyclePhase.VentanaConvencional or CyclePhase.Sobremadurez))
            return null;

        // Mismas plantas que la cosecha masiva convencional de Lotes: las activas que no están en riesgo.
        var plants = Math.Max(0, s.Plants.Active - s.Plants.Risk);
        if (plants == 0)
            return null;

        var impact = Times(plants, plantValue);
        var price = new RecommendationFigure("Precio", "moneyPerKg", s.Prices.ConventionalPerKg);

        if (t.Phase == CyclePhase.Sobremadurez)
        {
            return new OptimizationFinding(
                OptimizationContractType.ConventionalCut, KeyConventionalCut,
                "Pasado de cosecha: cortar ya",
                $"{plants} plantas pasaron la sobremadurez.",
                OptimizationContractPriority.High, OptimizationContractDirection.Cut,
                impact, OptimizationImpactKind.Capture, IsEstimate: false, s.Today.AddDays(SolutionDueDays),
                [
                    new("Plantas", "plants", plants),
                    price,
                    new("Sobremadurez desde", "date", Date: t.Overmaturity.Date),
                ]);
        }

        if (t.AccumulatedGdd >= t.ConventionalOptimum.Gdd)
        {
            var due = t.Overmaturity.Date ?? s.Today;
            return new OptimizationFinding(
                OptimizationContractType.ConventionalCut, KeyConventionalCut,
                "Cortar convencional ahora",
                $"{plants} plantas; sobremadurez el {Day(due)}.",
                OptimizationContractPriority.High, OptimizationContractDirection.Cut,
                impact, OptimizationImpactKind.Capture, IsEstimate: false, due,
                [
                    new("Plantas", "plants", plants),
                    price,
                    new("Sobremadurez", "date", Date: due),
                ]);
        }

        if (t.ConventionalOptimum is { IsEstimate: true, Date: { } optimum }
            && optimum > s.Today && optimum.DayNumber - s.Today.DayNumber <= UpcomingDays)
        {
            return new OptimizationFinding(
                OptimizationContractType.ConventionalCut, KeyConventionalCut,
                "Preparar corte convencional",
                $"Óptimo de corte el {Day(optimum)}: {plants} plantas.",
                OptimizationContractPriority.Medium, OptimizationContractDirection.Cut,
                impact, OptimizationImpactKind.Capture, IsEstimate: false, t.Overmaturity.Date ?? optimum,
                [
                    new("Plantas", "plants", plants),
                    price,
                    new("Óptimo", "date", Date: optimum),
                ]);
        }

        return null;
    }

    // ------------------------------------------------------------------
    // R3: estrategia de destino (Simulación)
    // ------------------------------------------------------------------

    private static OptimizationFinding? Strategy(LotSituation s, HashSet<string> evaluated)
    {
        if (s.Timeline is not { } t)
            return null;

        // Pasada la ventana Baby Leaf ya no hay nada que decidir: el resto va a convencional.
        if (t.Phase is not (CyclePhase.Desarrollo or CyclePhase.VentanaBabyLeaf) || s.Plants.Active == 0)
        {
            evaluated.Add(OptimizationContractType.HarvestStrategy);
            return null;
        }

        if (s.Strategy is not { Plan.Margin: { } planMargin } comparison)
            return null; // sin margen (faltan precios): no se puede evaluar

        evaluated.Add(OptimizationContractType.HarvestStrategy);
        var best = comparison.Options.Where(o => o.Margin is not null).MaxBy(o => o.Margin!.Value);
        if (best is null || best.Kind == comparison.Plan.Kind)
            return null;

        var gain = Math.Round(best.Margin!.Value - planMargin, 0);
        decimal? gainPercent = planMargin > 0 ? Math.Round(gain / planMargin * 100m, 1) : null;
        if (gain < s.Thresholds.MinGainAmount || (gainPercent is { } pct && pct < s.Thresholds.MinGainPercent))
            return null;

        var due = t.BabyLeafCloses.Date ?? s.Today;
        return new OptimizationFinding(
            OptimizationContractType.HarvestStrategy, StrategyKey(best.Kind),
            $"Cambiar a {best.Name}",
            $"{best.Name} deja {Money(gain)} más que {comparison.Plan.Name}.",
            OptimizationContractPriority.High, OptimizationContractDirection.Switch,
            gain, OptimizationImpactKind.Capture, IsEstimate: false, due,
            [
                new("Plan actual", "text", Text: comparison.Plan.Name),
                new("Diferencia", "pct", gainPercent),
                new("Lotes previos", "number", comparison.HistoryLots),
            ]);
    }

    // ------------------------------------------------------------------
    // R4 y R5: solución nutritiva (Anomalías)
    // ------------------------------------------------------------------

    private static IEnumerable<OptimizationFinding> Solution(
        LotSituation s, LotSolutionStatus solution, decimal? babyLeafValue, decimal? conventionalValue, HashSet<string> evaluated)
    {
        var due = s.Today.AddDays(SolutionDueDays);
        var atRisk = PotentialValue(s.Plants, babyLeafValue, conventionalValue);
        var phEvaluable = IsEvaluable(solution.Ph);
        var ecEvaluable = IsEvaluable(solution.Ec);

        if (ecEvaluable)
            evaluated.Add(Fingerprint(OptimizationContractType.NutrientOverspend, KeyEcHigh));
        if (phEvaluable)
        {
            evaluated.Add(Fingerprint(OptimizationContractType.SolutionRisk, KeyPhLow));
            evaluated.Add(Fingerprint(OptimizationContractType.SolutionRisk, KeyPhHigh));
        }
        if (ecEvaluable)
            evaluated.Add(Fingerprint(OptimizationContractType.SolutionRisk, KeyEcLow));
        evaluated.Add(Fingerprint(OptimizationContractType.SolutionRisk, KeyAnomaly));

        // Lo que corrige el control automático (nodo en AUTO, magnitud no pausada) no se recomienda a mano:
        // las recomendaciones abiertas de esa magnitud se cierran solas porque ya se evaluó.
        var autoPh = s.AutoControlled?.Contains(HydroPilotWeb.Models.AnomalyContract.SensorTypePh) == true;
        var autoEc = s.AutoControlled?.Contains(HydroPilotWeb.Models.AnomalyContract.SensorTypeCe) == true;

        // R4: CE por encima del rango → se gasta de más en nutrientes (supuesto en duda: la CE sube en
        // proporción a los ml de concentrado por litro).
        if (!autoEc && solution.Ec is { State: SolutionState.Lower, Value: { } ec, Band.Target: { } ecTarget } && ecTarget > 0)
        {
            var excess = ec / ecTarget - 1m;
            yield return new OptimizationFinding(
                OptimizationContractType.NutrientOverspend, KeyEcHigh,
                "Bajar CE al objetivo",
                $"CE {Num(ec)} con objetivo {Num(ecTarget)}: ≈ {Pct(excess * 100m)} más de nutrientes.",
                OptimizationContractPriority.Medium, OptimizationContractDirection.Lower,
                WeeklyOverspend(s, excess), OptimizationImpactKind.Saving, IsEstimate: true, due,
                [
                    new("CE", "number", ec, "mS/cm"),
                    new("Objetivo", "number", ecTarget, "mS/cm"),
                    new("Exceso", "pct", Math.Round(excess * 100m, 1)),
                ],
                ec, ecTarget);
        }

        // R5: pH fuera de rango o CE por debajo → plantas en riesgo.
        var sensorFinding = false;
        if (!autoPh && solution.Ph is { State: SolutionState.Raise or SolutionState.Lower, Value: { } ph } phStatus)
        {
            sensorFinding = true;
            var raise = phStatus.State == SolutionState.Raise;
            yield return RiskFinding(raise ? KeyPhLow : KeyPhHigh, raise ? "Subir pH" : "Bajar pH",
                raise ? OptimizationContractDirection.Raise : OptimizationContractDirection.Lower,
                "pH", ph, null, phStatus.Band, atRisk, due);
        }

        if (!autoEc && solution.Ec is { State: SolutionState.Raise, Value: { } ecLow } ecStatus)
        {
            sensorFinding = true;
            yield return RiskFinding(KeyEcLow, "Subir CE", OptimizationContractDirection.Raise,
                "CE", ecLow, "mS/cm", ecStatus.Band, atRisk, due);
        }

        if (!sensorFinding && solution.OpenEpisodes > 0)
        {
            yield return new OptimizationFinding(
                OptimizationContractType.SolutionRisk, KeyAnomaly,
                "Revisar anomalía abierta",
                $"{solution.OpenEpisodes} episodio(s) de pH/CE abiertos en Anomalías.",
                solution.HasCriticalEpisode ? OptimizationContractPriority.High : OptimizationContractPriority.Medium,
                OptimizationContractDirection.Verify,
                atRisk, OptimizationImpactKind.Risk, IsEstimate: false, due,
                [new("Episodios", "number", solution.OpenEpisodes)]);
        }

        static bool IsEvaluable(SolutionParameterStatus p) =>
            p.State is SolutionState.InRange or SolutionState.Raise or SolutionState.Lower;
    }

    private static OptimizationFinding RiskFinding(
        string key, string title, string direction, string label, decimal value, string? unit,
        SolutionBand band, decimal? atRisk, DateOnly due) =>
        new(OptimizationContractType.SolutionRisk, key,
            title,
            $"{label} {Num(value)} fuera del rango {Num(band.Min)}–{Num(band.Max)}.",
            OptimizationContractPriority.High, direction,
            atRisk, OptimizationImpactKind.Risk, IsEstimate: false, due,
            [
                new(label, "number", value, unit),
                new("Objetivo", "number", band.Target, unit),
                new("Rango", "text", Text: $"{Num(band.Min)}–{Num(band.Max)}"),
            ],
            value, band.Target);

    /// <summary>
    /// Nutrientes de más por semana ≈ exceso × litros de nutrientes de la semana × precio por litro. Litros
    /// = plantas activas × agua por planta y día de la fase × 7 × ml por litro (NutrientConsumptionRules).
    /// </summary>
    private static decimal? WeeklyOverspend(LotSituation s, decimal excess)
    {
        if (s.NutrientPricePerLiter is not { } price || s.Timeline is not { } t)
            return null;

        var (water, mlPerLiter) = t.Phase switch
        {
            CyclePhase.Desarrollo => (NutrientConsumptionRules.WaterBeforeBabyLeaf, NutrientConsumptionRules.NutrientMlPerLiterUntilBabyLeafEnd),
            CyclePhase.VentanaBabyLeaf => (NutrientConsumptionRules.WaterDuringBabyLeaf, NutrientConsumptionRules.NutrientMlPerLiterUntilBabyLeafEnd),
            _ => (NutrientConsumptionRules.WaterConventional, NutrientConsumptionRules.NutrientMlPerLiterConventional),
        };
        var weeklyLiters = s.Plants.Active * water * 7m * mlPerLiter / 1000m;
        return Math.Round(excess * weeklyLiters * price, 0);
    }

    // ------------------------------------------------------------------
    // R6: metas del ciclo (cards de Lotes)
    // ------------------------------------------------------------------

    private static IEnumerable<OptimizationFinding> Goals(
        LotSituation s, decimal? babyLeafValue, decimal? conventionalValue, HashSet<string> evaluated)
    {
        evaluated.Add(Fingerprint(OptimizationContractType.CycleGoals, KeyLoss));
        var inBabyLeaf = s.Timeline?.Phase is CyclePhase.Desarrollo or CyclePhase.VentanaBabyLeaf;

        if (s.Timeline is { } t)
        {
            evaluated.Add(Fingerprint(OptimizationContractType.CycleGoals, KeyGoal));
            if (inBabyLeaf && s.Goals is { BabyLeafStatus: BabyLeafGoalStatus.PorDebajo, GoalPlants: { } goalPlants })
            {
                var missing = Math.Max(0, goalPlants - s.Goals.PotentialCount);
                yield return new OptimizationFinding(
                    OptimizationContractType.CycleGoals, KeyGoal,
                    "Meta Baby Leaf en riesgo",
                    $"Faltan {missing} plantas para la meta de {Pct(s.BabyLeafGoalPercent ?? 0m)}.",
                    OptimizationContractPriority.Medium, OptimizationContractDirection.Verify,
                    Times(missing, babyLeafValue), OptimizationImpactKind.Risk, IsEstimate: false, t.BabyLeafCloses.Date,
                    [
                        new("Faltan", "plants", missing),
                        new("Meta", "pct", s.BabyLeafGoalPercent),
                        new("Potencial", "pct", s.Goals.PotentialPercent),
                    ]);
            }
        }

        if (s.Plants.Risk > 0 && s.MaxLossPercent is { } max && s.Goals.LossWithRiskPercent > max)
        {
            yield return new OptimizationFinding(
                OptimizationContractType.CycleGoals, KeyLoss,
                "Salvar plantas en riesgo",
                $"{s.Plants.Risk} plantas en riesgo: si se pierden, las pérdidas superan el máximo de {Pct(max)}.",
                OptimizationContractPriority.Medium, OptimizationContractDirection.Verify,
                Times(s.Plants.Risk, inBabyLeaf ? babyLeafValue : conventionalValue), OptimizationImpactKind.Risk,
                IsEstimate: false, null,
                [
                    new("En riesgo", "plants", s.Plants.Risk),
                    new("Pérdidas con riesgo", "pct", s.Goals.LossWithRiskPercent),
                    new("Máximo", "pct", max),
                ]);
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Valor del rendimiento potencial de Lotes: (aptas + candidatas) como Baby Leaf y (en desarrollo +
    /// convencionales) como convencional. Null si falta el valor de un destino que tiene plantas.
    /// </summary>
    public static decimal? PotentialValue(LotPlantBreakdown plants, decimal? babyLeafValue, decimal? conventionalValue)
    {
        var babyLeaf = plants.Apta + plants.Candidate;
        var conventional = plants.Developing + plants.Conventional;
        if ((babyLeaf > 0 && babyLeafValue is null) || (conventional > 0 && conventionalValue is null))
            return null;
        return Math.Round(babyLeaf * (babyLeafValue ?? 0m) + conventional * (conventionalValue ?? 0m), 0);
    }

    private static decimal? Times(int plants, decimal? plantValue) =>
        plantValue is { } value ? Math.Round(plants * value, 0) : null;

    private static void AddIfAny(List<OptimizationFinding> findings, OptimizationFinding? finding)
    {
        if (finding is not null)
            findings.Add(finding);
    }

    private static string Day(DateOnly date) => date.ToString("dd/MM", Culture);

    private static string Num(decimal? value) => value?.ToString("0.0#", Culture) ?? "—";

    private static string Pct(decimal value) => value.ToString("0.#", Culture) + " %";

    private static string Money(decimal value) => "$" + value.ToString("N0", Culture);
}
