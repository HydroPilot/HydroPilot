using HydroPilotWeb.Models.Optimization;
using HydroPilotWeb.Services.Anomalies;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.Optimization;
using HydroPilotWeb.Services.Simulation;

namespace HydroPilotWeb.Tests.Unit;

/// <summary>
/// Reglas de Optimización (R1–R6) y de la solución nutritiva de Anomalías. Lógica pura: ventanas por
/// GDD de Lotes, estrategias de Simulación y pH/CE con lecturas firmes.
/// </summary>
public class OptimizationRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);

    // 20 GDD por día: 15 días medidos = 300 GDD; la proyección sigue a 20 por día.
    private static CycleTimeline Timeline(decimal accumulated)
    {
        var days = (int)Math.Ceiling(accumulated / 20m);
        var sowing = Today.AddDays(-Math.Max(0, days - 1));
        var history = Enumerable.Range(0, days)
            .Select(i => new DailyGddPoint(sowing.AddDays(i), i == days - 1 ? accumulated - 20m * (days - 1) : 20m, GddPointSource.Observed))
            .ToList();
        var projection = Enumerable.Range(1, 7)
            .Select(i => new DailyGddPoint(Today.AddDays(i), 20m, GddPointSource.Forecast))
            .ToList();
        return CycleTimelineLogic.Build(sowing, Today, accumulated, history, projection, 250m, 450m, 750m);
    }

    private static LotPlantBreakdown Plants(int apta = 0, int candidate = 0, int developing = 0, int risk = 0, int discarded = 0)
    {
        var active = apta + candidate + developing + risk;
        return new LotPlantBreakdown(active + discarded, active, apta, candidate, developing, 0, risk, 0, 0, discarded, 0);
    }

    private static LotSituation Situation(
        CycleTimeline? timeline,
        LotPlantBreakdown plants,
        decimal? goal = null,
        decimal? maxLoss = null,
        LotSolutionStatus? solution = null,
        StrategyComparison? strategy = null,
        decimal? nutrientPrice = 25_000m,
        OptimizationThresholds? thresholds = null)
    {
        var goals = LotGoalsRules.Evaluate(new LotGoalsRules.Input(
            plants.TotalPlants, plants.HarvestedAsBabyLeaf, plants.Apta, plants.Candidate,
            plants.Discarded, plants.Risk, goal, maxLoss));
        return new LotSituation(
            Today, timeline, plants, goals, goal, maxLoss,
            new YieldReference(30m, 250m),
            new YieldPrices(50_000m, 11_800m, "ARS"),
            nutrientPrice, solution, strategy,
            thresholds ?? new OptimizationThresholds(10_000m, 10m));
    }

    private static SolutionParameterStatus Parameter(string sensor, decimal? value, SolutionState state, decimal min, decimal max, decimal target) =>
        new(sensor, sensor == "pH" ? "pH" : "mS/cm", value, DateTime.UtcNow, new SolutionBand(min, max, target), state, 2);

    private static LotSolutionStatus Solution(
        SolutionState phState = SolutionState.InRange, decimal ph = 6.0m,
        SolutionState ecState = SolutionState.InRange, decimal ec = 1.5m,
        int episodes = 0, bool critical = false) =>
        new(1, "Lote", "Crecimiento vegetativo",
            Parameter("pH", ph, phState, 5.5m, 6.5m, 6.0m),
            Parameter("CE", ec, ecState, 1.2m, 1.8m, 1.5m),
            episodes, critical);

    private static OptimizationFinding Single(OptimizationEvaluation evaluation, string type) =>
        Assert.Single(evaluation.Findings, f => f.Type == type);

    // ------------------------------------------------------------------
    // R1 / R2: cortes
    // ------------------------------------------------------------------

    [Fact]
    public void R1_Optimo_Alcanzado_Con_Aptas_Corta_Baby_Leaf_Ahora()
    {
        var finding = Single(OptimizationRules.Evaluate(Situation(Timeline(320m), Plants(apta: 7, candidate: 3))),
            OptimizationContractType.BabyLeafCut);

        Assert.Equal("Cortar Baby Leaf ahora", finding.Title);
        Assert.Equal(OptimizationContractPriority.High, finding.Priority);
        Assert.Equal(10_500m, finding.Impact);                 // 7 aptas × 30 g × $50.000/kg
        Assert.Equal(Today.AddDays(7), finding.DueDate);       // la ventana cierra a 450 GDD (130 GDD más)
        Assert.Equal(OptimizationImpactKind.Capture, finding.ImpactKind);
    }

    [Fact]
    public void R1_Optimo_En_Tres_Dias_O_Menos_Prepara_El_Corte_Con_Aptas_Y_Candidatas()
    {
        var finding = Single(OptimizationRules.Evaluate(Situation(Timeline(260m), Plants(apta: 2, candidate: 4, developing: 4))),
            OptimizationContractType.BabyLeafCut);

        Assert.Equal("Preparar corte Baby Leaf", finding.Title);
        Assert.Equal(OptimizationContractPriority.Medium, finding.Priority);
        Assert.Equal(9_000m, finding.Impact);                  // (2 + 4) × 30 g × $50.000/kg
    }

    [Fact]
    public void R1_Sin_Aptas_Ni_Optimo_Cerca_No_Recomienda()
    {
        var evaluation = OptimizationRules.Evaluate(Situation(Timeline(120m), Plants(developing: 10)));

        Assert.DoesNotContain(evaluation.Findings, f => f.Type == OptimizationContractType.BabyLeafCut);
        Assert.True(evaluation.WasEvaluated("BABY_LEAF_CUT:bl-cut"));
    }

    [Fact]
    public void R2_Optimo_Convencional_Corta_Las_Activas_Que_No_Estan_En_Riesgo()
    {
        var finding = Single(OptimizationRules.Evaluate(Situation(Timeline(680m), Plants(developing: 8, risk: 2))),
            OptimizationContractType.ConventionalCut);

        Assert.Equal("Cortar convencional ahora", finding.Title);
        Assert.Equal(23_600m, finding.Impact);                 // 8 × 250 g × $11.800/kg
    }

    [Fact]
    public void R2_Pasado_De_Cosecha_Es_Prioridad_Alta_Con_Un_Dia_Para_Cortar()
    {
        var finding = Single(OptimizationRules.Evaluate(Situation(Timeline(780m), Plants(developing: 5))),
            OptimizationContractType.ConventionalCut);

        Assert.Equal("Pasado de cosecha: cortar ya", finding.Title);
        Assert.Equal(OptimizationContractPriority.High, finding.Priority);
        Assert.Equal(Today.AddDays(1), finding.DueDate);
    }

    [Fact]
    public void Sin_Linea_De_Tiempo_Los_Cortes_No_Se_Evaluan()
    {
        var evaluation = OptimizationRules.Evaluate(Situation(null, Plants(apta: 5)));

        Assert.False(evaluation.WasEvaluated("BABY_LEAF_CUT:bl-cut"));
        Assert.False(evaluation.WasEvaluated("CONVENTIONAL_CUT:conv-cut"));
    }

    // ------------------------------------------------------------------
    // R3: estrategia
    // ------------------------------------------------------------------

    private static StrategyComparison Strategies(decimal plan, decimal allConventional) => new(
        new StrategyOption(SimulationStrategyKind.MaxBabyLeaf, "Máximo Baby Leaf", plan),
        [
            new StrategyOption(SimulationStrategyKind.MaxBabyLeaf, "Máximo Baby Leaf", plan),
            new StrategyOption(SimulationStrategyKind.AllConventional, "Todo convencional", allConventional),
        ],
        HistoryLots: 3);

    [Fact]
    public void R3_Recomienda_Otra_Estrategia_Si_Supera_Los_Dos_Minimos()
    {
        var finding = Single(OptimizationRules.Evaluate(
                Situation(Timeline(200m), Plants(developing: 20), strategy: Strategies(plan: 50_000m, allConventional: 70_000m))),
            OptimizationContractType.HarvestStrategy);

        Assert.Equal("Cambiar a Todo convencional", finding.Title);
        Assert.Equal(20_000m, finding.Impact);
        Assert.Equal("strategy:AllConventional", finding.Key);
        Assert.Equal(40m, finding.Figures.Single(f => f.Label == "Diferencia").Value);
    }

    [Theory]
    [InlineData(50_000, 58_000)]   // $8.000: no llega al mínimo de $10.000
    [InlineData(200_000, 215_000)] // 7,5 %: no llega al mínimo de 10 %
    public void R3_No_Recomienda_Si_No_Supera_Algun_Minimo(int plan, int alternative)
    {
        var evaluation = OptimizationRules.Evaluate(
            Situation(Timeline(200m), Plants(developing: 20), strategy: Strategies(plan, alternative)));

        Assert.DoesNotContain(evaluation.Findings, f => f.Type == OptimizationContractType.HarvestStrategy);
        Assert.True(evaluation.WasEvaluated("HARVEST_STRATEGY:strategy:AllConventional"));
    }

    [Fact]
    public void R3_Con_Plan_En_Perdida_Solo_Mira_El_Minimo_En_Pesos()
    {
        var finding = Single(OptimizationRules.Evaluate(
                Situation(Timeline(200m), Plants(developing: 20), strategy: Strategies(plan: -5_000m, allConventional: 12_000m))),
            OptimizationContractType.HarvestStrategy);

        Assert.Equal(17_000m, finding.Impact);
        Assert.Null(finding.Figures.Single(f => f.Label == "Diferencia").Value);
    }

    [Fact]
    public void R3_Sin_Margen_No_Se_Evalua_Y_Pasada_La_Ventana_Baby_Leaf_No_Hay_Decision()
    {
        var withoutPrices = OptimizationRules.Evaluate(Situation(Timeline(200m), Plants(developing: 20),
            strategy: new StrategyComparison(new StrategyOption(SimulationStrategyKind.MaxBabyLeaf, "Máximo Baby Leaf", null), [], 0)));
        Assert.False(withoutPrices.WasEvaluated("HARVEST_STRATEGY:strategy:AllConventional"));

        var conventionalPhase = OptimizationRules.Evaluate(Situation(Timeline(500m), Plants(developing: 20)));
        Assert.True(conventionalPhase.WasEvaluated("HARVEST_STRATEGY:strategy:AllConventional"));
        Assert.DoesNotContain(conventionalPhase.Findings, f => f.Type == OptimizationContractType.HarvestStrategy);
    }

    // ------------------------------------------------------------------
    // R4 / R5: solución
    // ------------------------------------------------------------------

    [Fact]
    public void R4_CE_Alta_Estima_El_Sobrecosto_Semanal_De_Nutrientes()
    {
        var finding = Single(OptimizationRules.Evaluate(
                Situation(Timeline(200m), Plants(developing: 20), solution: Solution(ecState: SolutionState.Lower, ec: 2.1m))),
            OptimizationContractType.NutrientOverspend);

        // Exceso 2,1 / 1,5 − 1 = 40 %. Desarrollo: 20 plantas × 0,15 L × 7 días × 5 ml/L = 0,105 L de nutrientes.
        // 0,4 × 0,105 L × $25.000 = $1.050 por semana.
        Assert.Equal(1_050m, finding.Impact);
        Assert.True(finding.IsEstimate);
        Assert.Equal(OptimizationImpactKind.Saving, finding.ImpactKind);
        Assert.DoesNotContain(OptimizationRules.Evaluate(
                Situation(Timeline(200m), Plants(developing: 20), solution: Solution(ecState: SolutionState.Lower, ec: 2.1m))).Findings,
            f => f.Type == OptimizationContractType.SolutionRisk);
    }

    [Fact]
    public void R5_PH_Fuera_De_Rango_Pone_En_Riesgo_El_Valor_Potencial_Del_Lote()
    {
        var finding = Single(OptimizationRules.Evaluate(
                Situation(Timeline(320m), Plants(apta: 4, developing: 6), solution: Solution(phState: SolutionState.Lower, ph: 7.1m))),
            OptimizationContractType.SolutionRisk);

        Assert.Equal("Bajar pH", finding.Title);
        Assert.Equal("ph-high", finding.Key);
        Assert.Equal(23_700m, finding.Impact); // 4 × 30 g × $50.000 + 6 × 250 g × $11.800
        Assert.Equal(OptimizationImpactKind.Risk, finding.ImpactKind);
    }

    [Fact]
    public void R5_Anomalia_Abierta_Sin_Desvio_Firme_Es_Una_Sola_Card()
    {
        var finding = Single(OptimizationRules.Evaluate(
                Situation(Timeline(200m), Plants(developing: 10), solution: Solution(episodes: 2, critical: true))),
            OptimizationContractType.SolutionRisk);

        Assert.Equal("anomaly", finding.Key);
        Assert.Equal(OptimizationContractPriority.High, finding.Priority);
    }

    [Fact]
    public void R4_R5_No_Se_Recomiendan_Si_El_Control_Automatico_Ya_Lo_Corrige()
    {
        var solution = Solution(phState: SolutionState.Lower, ph: 7.1m, ecState: SolutionState.Lower, ec: 2.1m);

        var auto = OptimizationRules.Evaluate(Situation(Timeline(320m), Plants(apta: 4, developing: 6), solution: solution)
            with { AutoControlled = new HashSet<string> { "pH", "CE" } });
        Assert.DoesNotContain(auto.Findings, f => f.Type is OptimizationContractType.SolutionRisk or OptimizationContractType.NutrientOverspend);
        // Se evaluó: las recomendaciones abiertas de antes se cierran solas.
        Assert.True(auto.WasEvaluated("SOLUTION_RISK:ph-high"));

        // pH pausado (fuera de lo automático): vuelve a recomendarse a mano; la CE la sigue corrigiendo el motor.
        var phPaused = OptimizationRules.Evaluate(Situation(Timeline(320m), Plants(apta: 4, developing: 6), solution: solution)
            with { AutoControlled = new HashSet<string> { "CE" } });
        Assert.Contains(phPaused.Findings, f => f.Type == OptimizationContractType.SolutionRisk && f.Key == "ph-high");
        Assert.DoesNotContain(phPaused.Findings, f => f.Type == OptimizationContractType.NutrientOverspend);
    }

    [Fact]
    public void Lecturas_Sin_Confirmar_O_Viejas_No_Evaluan_La_Solucion()
    {
        var evaluation = OptimizationRules.Evaluate(Situation(Timeline(200m), Plants(developing: 10),
            solution: Solution(phState: SolutionState.Stale, ecState: SolutionState.Unconfirmed, ec: 2.4m)));

        Assert.DoesNotContain(evaluation.Findings, f => f.Type is OptimizationContractType.SolutionRisk or OptimizationContractType.NutrientOverspend);
        Assert.False(evaluation.WasEvaluated("SOLUTION_RISK:ph-high"));
        Assert.False(evaluation.WasEvaluated("NUTRIENT_OVERSPEND:ec-high"));
        Assert.True(evaluation.WasEvaluated("SOLUTION_RISK:anomaly"));
    }

    // ------------------------------------------------------------------
    // R6: metas
    // ------------------------------------------------------------------

    [Fact]
    public void R6_Meta_Baby_Leaf_Por_Debajo_Del_Potencial()
    {
        var finding = Single(OptimizationRules.Evaluate(
                Situation(Timeline(300m), Plants(apta: 2, candidate: 2, developing: 6), goal: 80m)),
            OptimizationContractType.CycleGoals);

        Assert.Equal("goal", finding.Key);
        Assert.Equal(6_000m, finding.Impact); // faltan 8 − 4 = 4 plantas × $1.500
    }

    [Fact]
    public void R6_Plantas_En_Riesgo_Que_Superarian_El_Maximo_De_Perdidas()
    {
        var finding = Single(OptimizationRules.Evaluate(
                Situation(Timeline(500m), Plants(developing: 7, risk: 2, discarded: 1), maxLoss: 10m)),
            OptimizationContractType.CycleGoals);

        Assert.Equal("loss", finding.Key);
        Assert.Equal(5_900m, finding.Impact); // 2 en riesgo × 250 g × $11.800 (fase convencional)
    }

    // ------------------------------------------------------------------
    // Solución nutritiva (Anomalías)
    // ------------------------------------------------------------------

    private static SolutionReading Reading(decimal value, int minutesAgo) =>
        new("pH", "VALID", value, DateTime.UtcNow.AddMinutes(-minutesAgo));

    private static readonly SolutionBand PhBand = new(5.5m, 6.5m, 6.0m);

    [Fact]
    public void Solucion_Dos_Lecturas_Frescas_Que_Coinciden_Son_Firmes()
    {
        var status = SolutionRules.Evaluate("pH", "pH", [Reading(7.0m, 2), Reading(6.9m, 12)], null, PhBand, DateTime.UtcNow, 30);
        Assert.Equal(SolutionState.Lower, status.State);
        Assert.True(status.IsFirmDeviation);
    }

    [Fact]
    public void Solucion_Una_Lectura_O_Contradictorias_Piden_Confirmar()
    {
        Assert.Equal(SolutionState.Unconfirmed,
            SolutionRules.Evaluate("pH", "pH", [Reading(7.0m, 2)], null, PhBand, DateTime.UtcNow, 30).State);
        Assert.Equal(SolutionState.Unconfirmed,
            SolutionRules.Evaluate("pH", "pH", [Reading(7.0m, 2), Reading(6.0m, 12)], null, PhBand, DateTime.UtcNow, 30).State);
    }

    [Fact]
    public void Solucion_Lectura_Vieja_Y_Valor_Manual()
    {
        Assert.Equal(SolutionState.Stale,
            SolutionRules.Evaluate("pH", "pH", [Reading(7.0m, 90), Reading(7.1m, 100)], null, PhBand, DateTime.UtcNow, 30).State);

        var manual = SolutionRules.Evaluate("pH", "pH", [], 7.2m, PhBand, DateTime.UtcNow, 30);
        Assert.Equal(SolutionState.Manual, manual.State);
        Assert.Equal(7.2m, manual.Value);
    }
}
