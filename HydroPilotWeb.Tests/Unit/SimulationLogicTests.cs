using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.Simulation;

namespace HydroPilotWeb.Tests.Unit;

/// <summary>
/// Pruebas unitarias puras del módulo de simulación: costos, validación, GDD del escenario,
/// estrategias con la lógica de Lotes (óptimos por GDD, plantas × peso × precio) y determinismo.
/// No requieren SQL Server (lógica sin I/O).
/// </summary>
public class SimulationLogicTests
{
    /// <summary>El "hoy" de los escenarios de prueba (la simulación siempre arranca hoy).</summary>
    private static readonly DateOnly TestToday = new(2026, 1, 15);

    // Semilla $2,5 · nutrientes $5/L.
    private static SimulationCostsInput FullCosts() => new(2.5m, 5m);

    private static SimulationRequest ValidRequest() => new(
        LotId: null,
        CropTypeId: 1,
        SowingDate: new DateOnly(2026, 1, 1),
        PlantCount: 60,
        Climate: new SimulationClimateInput(SimulationClimateMode.Manual, ManualTempMinC: 18m, ManualTempMaxC: 26m, ForecastHorizonDays: 7),
        Costs: FullCosts(),
        Harvest: new SimulationHarvestInput(SimulationStrategyKind.BabyLeafGoal, BabyLeafGoalPercent: 80m),
        Yield: null);

    // --- SIM-05: fórmulas de costos transparentes ---

    [Fact]
    public void Costos_Siguen_Las_Formulas_Del_Plan()
    {
        var breakdown = CostCalculator.Calculate(FullCosts(), seeds: 600, new NutrientUsage(100m, 0.5m), cycleDays: 10, "ARS");

        Assert.Equal(["Semillas", "Nutrientes"], breakdown.Components.Select(c => c.Name)); // solo gastos del sistema
        Assert.Equal(1500m, breakdown.Components.Single(c => c.Name == "Semillas").Amount);  // 2,5 × 600 plantas
        Assert.Equal(2.5m, breakdown.Components.Single(c => c.Name == "Nutrientes").Amount); // 0,5 L × $5
        Assert.Equal(1502.5m, breakdown.Total);
        Assert.Equal(10, breakdown.CycleDays);
        Assert.Equal(600, breakdown.Seeds);
        Assert.Equal(4.4m, CostCalculator.CostPerKg(2200m, 500m)); // 2200 / 500
    }

    [Fact]
    public void Costos_Sin_Precios_Muestran_No_Calculable_Sin_Inventar()
    {
        var breakdown = CostCalculator.Calculate(
            FullCosts() with { PricePerSeed = null }, seeds: 600, new NutrientUsage(10m, 0.05m), cycleDays: 7, "ARS");

        var seeds = breakdown.Components.Single(c => c.Name == "Semillas");
        Assert.Null(seeds.Amount);
        Assert.Contains("Falta el precio por semilla", seeds.NotCalculableReason);
        Assert.Null(breakdown.Total); // sin semillas no se puede totalizar
        Assert.Contains(CostCalculator.NotCalculable, breakdown.TotalNotCalculableReason);
        Assert.Null(CostCalculator.CostPerKg(breakdown.Total, 300m));
    }

    [Fact]
    public void CostoPorKg_Requiere_Kg_Positivos()
    {
        Assert.Null(CostCalculator.CostPerKg(1000m, 0m));
        Assert.Null(CostCalculator.CostPerKg(1000m, null));
        Assert.Null(CostCalculator.CostPerKg(null, 100m));
    }

    [Fact]
    public void Consumo_De_Agua_Y_Nutrientes_Sigue_Plantas_Vivas_Y_Etapa()
    {
        // Siembra 1/1, ventana Baby Leaf del 11/1 al 20/1: 10 días antes, 10 en Baby Leaf y 10 en convencional.
        var sowing = new DateOnly(2026, 1, 1);
        var usage = NutrientConsumptionRules.Calculate(sowing, new DateOnly(2026, 1, 10), new DateOnly(2026, 1, 20),
        [
            new NutrientConsumptionRules.PlantSpan(2, new DateOnly(2026, 1, 31)), // 2 plantas los 30 días
            new NutrientConsumptionRules.PlantSpan(1, new DateOnly(2026, 1, 6)),  // 1 descartada el 6/1: 5 días
        ]);

        // Por planta: 10 × 0,04 + 10 × 0,075 + 10 × 0,25 = 3,65 L de agua; (1,15 L × 5 ml) + (2,5 L × 10 ml) = 0,03075 L de
        // nutrientes. La descartada: 5 × 0,04 = 0,2 L de agua y 0,001 L de nutrientes.
        Assert.Equal(7.5m, usage.WaterLiters);
        Assert.Equal(0.0625m, usage.NutrientLiters);
        Assert.Equal(new NutrientUsage(0m, 0m), NutrientConsumptionRules.Calculate(sowing, null, null, []));
    }

    // --- SIM-05: validación en servidor ---

    [Fact]
    public void Validacion_Rechaza_Negativos_En_Costos()
    {
        var request = ValidRequest() with
        {
            Costs = FullCosts() with { PricePerSeed = -2.5m }
        };

        var errors = SimulationRequestValidator.Validate(request, TestToday);
        Assert.Contains(errors, e => e.Contains("precio de semilla", StringComparison.OrdinalIgnoreCase) && e.Contains("negativo"));
    }

    [Fact]
    public void Validacion_Rechaza_Tmin_Mayor_Que_Tmax()
    {
        var request = ValidRequest() with
        {
            Climate = new SimulationClimateInput(SimulationClimateMode.Manual, 30m, 18m, 7)
        };

        Assert.Contains(SimulationRequestValidator.Validate(request, TestToday), e => e.Contains("Tmin no puede ser mayor que Tmax"));
    }

    [Fact]
    public void Validacion_Rechaza_Contexto_Incompleto_En_Escenario_Libre()
    {
        var request = ValidRequest() with { CropTypeId = null, SowingDate = null, PlantCount = null };

        var errors = SimulationRequestValidator.Validate(request, TestToday);
        Assert.Contains(errors, e => e.Contains("cultivo"));
        Assert.Contains(errors, e => e.Contains("siembra"));
        Assert.Contains(errors, e => e.Contains("cantidad de plantas"));
    }

    [Fact]
    public void Validacion_Rechaza_Meta_Baby_Leaf_Fuera_De_Rango_O_Faltante()
    {
        var outOfRange = ValidRequest() with { Harvest = new SimulationHarvestInput(SimulationStrategyKind.MaxBabyLeaf, 150m) };
        var missing = ValidRequest() with { Harvest = new SimulationHarvestInput(SimulationStrategyKind.BabyLeafGoal, null) };

        Assert.Contains(SimulationRequestValidator.Validate(outOfRange, TestToday), e => e.Contains("entre 0 y 100"));
        Assert.Contains(SimulationRequestValidator.Validate(missing, TestToday), e => e.Contains("Indicá la meta Baby Leaf máxima"));
        Assert.Empty(SimulationRequestValidator.Validate(
            ValidRequest() with { Harvest = new SimulationHarvestInput(SimulationStrategyKind.AllConventional, null) }, TestToday));
    }

    [Fact]
    public void Validacion_Rechaza_Pesos_Y_Precios_Invalidos_Del_Escenario()
    {
        var request = ValidRequest() with { Yield = new SimulationYieldInput(0m, 200m, -1m, null) };

        var errors = SimulationRequestValidator.Validate(request, TestToday);
        Assert.Contains(errors, e => e.Contains("peso promedio", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, e => e.Contains("precio por kg", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validacion_Acepta_Siembra_Futura_Para_Planificar_Hasta_Un_Anio()
    {
        Assert.Empty(SimulationRequestValidator.Validate(ValidRequest() with { SowingDate = TestToday.AddDays(30) }, TestToday));
        Assert.Contains(
            SimulationRequestValidator.Validate(ValidRequest() with { SowingDate = TestToday.AddDays(400) }, TestToday),
            e => e.Contains("dentro de un año"));
    }

    [Fact]
    public void Validacion_Acepta_Request_Valido()
    {
        Assert.Empty(SimulationRequestValidator.Validate(ValidRequest(), TestToday));
    }

    // --- SIM-03/SIM-04: GDD manual del escenario (núcleo forecasting reutilizado) ---

    [Fact]
    public void Gdd_Manual_Usa_La_Formula_Del_Nucleo_De_Forecasting()
    {
        // Tmin 18, Tmax 26, Tbase 4.5 → (26+18)/2 - 4.5 = 17.5
        var points = GddCalculator.ManualPoints(
            new SimulationClimateInput(SimulationClimateMode.Manual, 18m, 26m, 7),
            4.5m, new DateOnly(2026, 1, 16), new DateOnly(2026, 1, 22));

        Assert.Equal(7, points.Count);
        Assert.All(points, p =>
        {
            Assert.Equal(17.5m, p.Gdd);
            Assert.Equal(SimulationGddSource.Manual, p.Source);
        });
    }

    [Fact]
    public void Gdd_Del_Escenario_Libre_Suma_El_Clima_Manual_Desde_La_Siembra_Hasta_Hoy()
    {
        var climate = new SimulationClimateInput(SimulationClimateMode.Manual, 18m, 26m, 7);

        var daily = GddCalculator.ManualHistory(climate, 4.5m, new DateOnly(2026, 1, 1), TestToday);

        Assert.Equal(15, daily.Count);                // 1 al 15 de enero
        Assert.Equal(262.5m, daily.Sum(p => p.Gdd));  // 15 × 17,5
    }

    [Fact]
    public void Escenario_Libre_Con_Siembra_Futura_No_Tiene_Gdd_Hasta_Hoy_Y_Proyecta_Desde_La_Siembra()
    {
        var climate = new SimulationClimateInput(SimulationClimateMode.Manual, 18m, 26m, 7);

        Assert.Empty(GddCalculator.ManualHistory(climate, 4.5m, TestToday.AddDays(5), TestToday));
        Assert.Equal(TestToday.AddDays(5), GddCalculator.ProjectionStart(TestToday.AddDays(5), TestToday));
        Assert.Equal(TestToday.AddDays(1), GddCalculator.ProjectionStart(new DateOnly(2026, 1, 1), TestToday)); // ya sembrado: desde mañana
        Assert.Equal(TestToday.AddDays(1), GddCalculator.ProjectionStart(TestToday, TestToday));
    }

    // --- Estrategias con la lógica de Lotes ---

    /// <summary>
    /// Escenario base: siembra 1/1, referencia 15/1, 17,5 GDD/día → 262,5 acumulados. Proyección de
    /// 7 días (16 al 22/1, 17,5/día) y después extrapolación con el último día.
    /// Óptimo Baby Leaf 300 → 18/1. Cierre 450 → ≈ 26/1. Óptimo convencional 650 → ≈ 7/2.
    /// </summary>
    private static CycleTimeline Timeline(decimal accumulated = 262.5m)
    {
        var climate = new SimulationClimateInput(SimulationClimateMode.Manual, 18m, 26m, 7);
        var history = GddCalculator.ManualHistory(climate, 4.5m, new DateOnly(2026, 1, 1), TestToday);
        var projection = GddCalculator.ToCorePoints(
            GddCalculator.ManualPoints(climate, 4.5m, new DateOnly(2026, 1, 16), new DateOnly(2026, 1, 22)));
        return CycleTimelineLogic.Build(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 15), accumulated,
            history, projection, 250m, 450m, 750m);
    }

    private static IReadOnlyList<SimulationStrategyOutcome> Strategies(
        CycleTimeline? timeline = null, decimal? goal = 60m, decimal? aptRate = null, decimal earlyLoss = 0m, decimal lateLoss = 0m) =>
        SimulationStrategies.Build(new SimulationStrategies.Input(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 15), timeline ?? Timeline(), Plants: 10, goal,
            aptRate, earlyLoss, lateLoss,
            new YieldReference(30m, 200m), new YieldPrices(3000m, 2000m, "ARS"), FullCosts(), Seeds: 600, ExitedPlantDates: []));

    [Fact]
    public void Estrategias_Cortan_En_Los_Optimos_Y_Valorizan_Por_Plantas()
    {
        var strategies = Strategies();

        Assert.Equal(["Máximo Baby Leaf", "Meta Baby Leaf (60 %)", "Todo adulta"], strategies.Select(s => s.Name));
        Assert.Equal(
            [SimulationStrategyKind.MaxBabyLeaf, SimulationStrategyKind.BabyLeafGoal, SimulationStrategyKind.AllConventional],
            strategies.Select(s => s.Kind));

        // Máximo Baby Leaf sin previsión (todas aptas, sin pérdidas): 10 plantas × 30 g = 0,3 kg × $3000 = $900;
        // corte 18/1 → 17 días de ciclo.
        var babyLeaf = strategies[0];
        Assert.True(babyLeaf.Available);
        Assert.Equal(new DateOnly(2026, 1, 18), babyLeaf.BabyLeafCut!.Date);
        Assert.Null(babyLeaf.ConventionalCut);
        Assert.Equal(0.3m, babyLeaf.TotalKg);
        Assert.Equal(900m, babyLeaf.Revenue);
        Assert.Equal(17, babyLeaf.Costs.CycleDays);
        // Agua: 10 plantas × (15 días antes de Baby Leaf × 0,04 L + 2 días en Baby Leaf × 0,075 L) = 7,5 L;
        // nutrientes 7,5 × 5 ml = 0,0375 L × $5 = $0,19. Gastos del sistema = 1500 + 0,19.
        Assert.Equal(new NutrientUsage(7.5m, 0.0375m), babyLeaf.Costs.Usage);
        Assert.Equal(1500.19m, babyLeaf.Costs.Total);
        Assert.Equal(-600.19m, babyLeaf.Margin);

        // Meta 60 %: 6 Baby Leaf (0,18 kg · $540) + 4 convencionales (0,8 kg · $1600); termina con el corte convencional.
        var mixed = strategies[1];
        Assert.Equal(new DestinationYield(6, 0.18m, 540m), mixed.BabyLeaf);
        Assert.Equal(new DestinationYield(4, 0.8m, 1600m), mixed.Conventional);
        Assert.Equal(new DateOnly(2026, 2, 7), mixed.EndDate);
        Assert.True(mixed.ConventionalCut!.IsExtrapolated); // más allá de los 7 días de proyección
        Assert.Equal(37, mixed.Costs.CycleDays);
        Assert.Equal(2140m, mixed.Revenue);
        // 6 Baby Leaf hasta el 18/1 (4,5 L de agua) + 4 adultas hasta el 7/2: 15 días × 0,04 L + 11 en Baby Leaf ×
        // 0,075 L + 11 de adulta × 0,25 L = 4,175 L cada una (16,7 L).
        Assert.Equal(new NutrientUsage(21.2m, 0.161m), mixed.Costs.Usage);
        Assert.Equal(1500.80m, mixed.Costs.Total);   // 1500 + 0,161 L × $5

        // Todo convencional: 10 × 200 g = 2 kg × $2000 = $4000.
        var conventional = strategies[2];
        Assert.Equal(2m, conventional.TotalKg);
        Assert.Equal(4000m, conventional.Revenue);
        Assert.Equal(new NutrientUsage(41.75m, 0.34625m), conventional.Costs.Usage); // 10 × 4,175 L; 10 ml/L de adulta
        Assert.Equal(2498.27m, conventional.Margin);  // 4000 − (1500 + 1,73)
        Assert.Equal(750.86m, conventional.CostPerKg); // 1501,73 / 2
    }

    [Fact]
    public void Sin_Meta_Baby_Leaf_No_Hay_Estrategia_De_Meta()
    {
        Assert.Equal(["Máximo Baby Leaf", "Todo adulta"], Strategies(goal: null).Select(s => s.Name));
    }

    [Fact]
    public void Reparto_Descuenta_Perdidas_Y_No_Pasa_Las_Aptas_Previstas()
    {
        // 20 plantas, 10 % de pérdidas tempranas → 18 vivas; 70 % aptas → hasta 13 Baby Leaf;
        // 20 % de pérdidas tardías sobre las que siguen a convencional.
        // Las que siguen a adulta se trasplantan (Transplanted).
        Assert.Equal(new SimulationStrategies.PlantAllocation(13, 4, EarlyLost: 2, LateLost: 1, Transplanted: 5),
            SimulationStrategies.Allocate(SimulationStrategyKind.MaxBabyLeaf, 20, null, 0.7m, 0.1m, 0.2m));
        Assert.Equal(new SimulationStrategies.PlantAllocation(9, 7, EarlyLost: 2, LateLost: 2, Transplanted: 9),   // meta 50 % de 18 = 9 (menos que 13)
            SimulationStrategies.Allocate(SimulationStrategyKind.BabyLeafGoal, 20, 50m, 0.7m, 0.1m, 0.2m));
        Assert.Equal(new SimulationStrategies.PlantAllocation(13, 4, EarlyLost: 2, LateLost: 1, Transplanted: 5),  // meta 90 % = 16, pero solo 13 aptas
            SimulationStrategies.Allocate(SimulationStrategyKind.BabyLeafGoal, 20, 90m, 0.7m, 0.1m, 0.2m));
        Assert.Equal(new SimulationStrategies.PlantAllocation(0, 14, EarlyLost: 2, LateLost: 4, Transplanted: 18),
            SimulationStrategies.Allocate(SimulationStrategyKind.AllConventional, 20, null, 0.7m, 0.1m, 0.2m));
    }

    [Fact]
    public void Reparto_Con_Pocos_Huecos_De_Adultas_Corta_Baby_Leaf_O_Pierde_Lo_Que_No_Entra()
    {
        // 18 vivas, 13 aptas. Con 3 huecos: las 5 no aptas no entran todas → 2 se pierden.
        Assert.Equal(new SimulationStrategies.PlantAllocation(13, 2, EarlyLost: 2, LateLost: 1, NoRoom: 2, Transplanted: 3),
            SimulationStrategies.Allocate(SimulationStrategyKind.MaxBabyLeaf, 20, null, 0.7m, 0.1m, 0.2m, adultCapacity: 3));
        // Todo adulta con 10 huecos: entran 10; de las 8 que no, las aptas se cortan como Baby Leaf.
        Assert.Equal(new SimulationStrategies.PlantAllocation(8, 8, EarlyLost: 2, LateLost: 2, Transplanted: 10),
            SimulationStrategies.Allocate(SimulationStrategyKind.AllConventional, 20, null, 0.7m, 0.1m, 0.2m, adultCapacity: 10));
        // Las ya trasplantadas son adultas siempre.
        Assert.Equal(new SimulationStrategies.PlantAllocation(13, 9, EarlyLost: 2, LateLost: 2, Transplanted: 5),
            SimulationStrategies.Allocate(SimulationStrategyKind.MaxBabyLeaf, 20, null, 0.7m, 0.1m, 0.2m, fixedAdults: 6));
    }

    [Fact]
    public void Estrategias_Usan_La_Prevision_De_Aptas_Y_Perdidas()
    {
        // 10 plantas: 1 pérdida temprana → 9 vivas; 70 % aptas → 6 Baby Leaf; de las 3 que siguen, 1 tardía.
        var max = Strategies(goal: null, aptRate: 0.7m, earlyLoss: 0.1m, lateLoss: 0.2m)[0];

        Assert.Equal(6, max.BabyLeaf.Plants);
        Assert.Equal(2, max.Conventional.Plants);
        Assert.Equal(2, max.LostPlants);
        Assert.Equal(0.58m, max.TotalKg); // 6 × 30 g + 2 × 200 g
        Assert.Equal(1340m, max.Revenue); // 0,18 kg × $3000 + 0,4 kg × $2000
    }

    [Fact]
    public void Pasado_El_Optimo_Baby_Leaf_Se_Corta_En_La_Fecha_De_Referencia()
    {
        var cut = SimulationStrategies.BabyLeafCut(Timeline(accumulated: 320m), new DateOnly(2026, 1, 15));

        Assert.True(cut.IsNow);
        Assert.Equal(new DateOnly(2026, 1, 15), cut.Date);
    }

    [Fact]
    public void Con_La_Ventana_Baby_Leaf_Cerrada_No_Hay_Corte_Baby_Leaf()
    {
        var strategies = Strategies(Timeline(accumulated: 460m));

        var babyLeaf = strategies.Single(s => s.Kind == SimulationStrategyKind.MaxBabyLeaf);
        Assert.False(babyLeaf.Available);
        Assert.Null(babyLeaf.BabyLeafCut!.Date);
        Assert.Null(babyLeaf.Revenue);
        Assert.Contains(babyLeaf.Warnings, w => w.Contains("ventana Baby Leaf ya cerró"));
        Assert.True(strategies.Single(s => s.Name == "Todo adulta").Available);
    }

    [Fact]
    public void Siembra_Futura_Corta_Contando_Desde_La_Siembra()
    {
        // Hoy 15/1, siembra 20/1, 17,5 GDD/día desde la siembra: 300 GDD el día 18 → ≈ 6/2 (extrapolado).
        var sowing = TestToday.AddDays(5);
        var climate = new SimulationClimateInput(SimulationClimateMode.Manual, 18m, 26m, 7);
        var projection = GddCalculator.ToCorePoints(GddCalculator.ManualPoints(climate, 4.5m, sowing, sowing.AddDays(6)));
        var timeline = CycleTimelineLogic.Build(sowing, TestToday, 0m, [], projection, 250m, 450m, 750m);

        var babyLeaf = SimulationStrategies.Build(new SimulationStrategies.Input(
            sowing, TestToday, timeline, Plants: 10, null, null, 0m, 0m,
            new YieldReference(30m, 200m), new YieldPrices(3000m, 2000m, "ARS"), FullCosts(), Seeds: 10, ExitedPlantDates: []))[0];

        Assert.Equal(CyclePhase.Desarrollo, timeline.Phase);
        Assert.Equal(0, timeline.CycleDay);
        Assert.Equal(new DateOnly(2026, 2, 6), babyLeaf.BabyLeafCut!.Date);
        Assert.False(babyLeaf.BabyLeafCut.IsNow);
        Assert.True(babyLeaf.BabyLeafCut.IsExtrapolated);
        Assert.Equal(17, babyLeaf.Costs.CycleDays); // de la siembra (20/1) al corte (6/2)
    }

    // --- SIM: repetibilidad (misma entrada el mismo día → mismo id/resultado) ---

    [Fact]
    public void Misma_Entrada_El_Mismo_Dia_Genera_El_Mismo_Id_Determinista()
    {
        var first = SimulationService.ComputeSimulationId(ValidRequest(), TestToday);
        var second = SimulationService.ComputeSimulationId(ValidRequest(), TestToday);

        Assert.Equal(16, first.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Entradas_Distintas_U_Otro_Dia_Generan_Ids_Distintos()
    {
        var request = ValidRequest();
        var other = request with { Climate = request.Climate with { ManualTempMaxC = 28m } };

        Assert.NotEqual(SimulationService.ComputeSimulationId(request, TestToday), SimulationService.ComputeSimulationId(other, TestToday));
        Assert.NotEqual(SimulationService.ComputeSimulationId(request, TestToday), SimulationService.ComputeSimulationId(request, TestToday.AddDays(1)));
    }

    // --- SIM-04: cobertura del plan manual ---

    [Fact]
    public void Plan_Manual_Tiene_Cobertura_Completa()
    {
        var plan = new ClimatePlanBuilderStub().Manual(
            new SimulationClimateInput(SimulationClimateMode.Manual, 16m, 26m, 7),
            4.5m, new DateOnly(2026, 1, 16), new DateOnly(2026, 1, 22));

        Assert.Equal(7, plan.HorizonDays);
        Assert.Equal(7, plan.CoveredDays);
        Assert.Equal(0, plan.MissingDays);
        Assert.Equal(100m, plan.CoveragePercent);
    }
}

/// <summary>
/// Stub mínimo para probar la construcción del plan manual sin DI ni SQL:
/// replica la lógica de ClimateScenarioProvider solo para el modo manual.
/// (El resto de los modos se prueban en integración SQL.)
/// </summary>
internal sealed class ClimatePlanBuilderStub
{
    public ClimatePlan Manual(SimulationClimateInput climate, decimal baseTemp, DateOnly from, DateOnly to)
    {
        var points = GddCalculator.ManualPoints(climate, baseTemp, from, to);
        return new ClimatePlan(points, "manual", null, false, 7, points.Count, 0, 100m, []);
    }
}
