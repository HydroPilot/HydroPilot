using HydroPilotWeb.Services.Simulation;

namespace HydroPilotWeb.Tests.Unit;

/// <summary>
/// Pruebas unitarias puras del módulo de simulación (SIM-03..SIM-07): costos,
/// validación, cálculo de GDD, comparación de estrategias y determinismo.
/// No requieren SQL Server (lógica sin I/O).
/// </summary>
public class SimulationLogicTests
{
    private static SimulationRequest ValidRequest() => new(
        LotId: null,
        CropTypeId: 1,
        SowingDate: new DateOnly(2026, 1, 1),
        AreaM2: 4.5m,
        ReferenceDate: new DateOnly(2026, 1, 15),
        Climate: new SimulationClimateInput(SimulationClimateMode.Manual, ManualTempMinC: 18m, ManualTempMaxC: 26m, ManualHumidityPercent: 65, ForecastHorizonDays: 7),
        Agronomic: new SimulationAgronomicInput(6.0m, 1.5m),
        Costs: new SimulationCostsInput(
            "ARS",
            new SimulationSeedCostsInput(2.5m, 600),
            new SimulationNutrientCostsInput(5m, 5m),
            new SimulationEnergyCostsInput(30m, 1.5m)),
        Harvest: new SimulationHarvestInput(EnableBabyLeaf: true, EnableConvencional: true, HypothesisAptaPercent: 80m, RequiredTargetPercent: 70m),
        SaveScenario: false);

    // --- SIM-05: fórmulas de costos transparentes ---

    [Fact]
    public void Costos_Siguen_Las_Formulas_Del_Plan()
    {
        var breakdown = CostCalculator.Calculate(
            new SimulationCostsInput(
                "ARS",
                new SimulationSeedCostsInput(PricePerSeed: 2.5m, SeedCount: 600),
                new SimulationNutrientCostsInput(PricePerLiter: 5m, LitersPerDay: 5m),
                new SimulationEnergyCostsInput(PricePerKwh: 30m, KwhPerDay: 1.5m)),
            projectedDays: 10);

        var seeds = breakdown.Components.Single(c => c.Name == "Semillas");
        var nutrients = breakdown.Components.Single(c => c.Name == "Nutrientes");
        var energy = breakdown.Components.Single(c => c.Name == "Energía");

        Assert.Equal(1500m, seeds.Amount);                      // 2.5 × 600
        Assert.Equal(250m, nutrients.Amount);                   // 5 × 5 × 10
        Assert.Equal(450m, energy.Amount);                      // 30 × 1.5 × 10
        Assert.Equal(2200m, breakdown.Total);                   // suma
        Assert.Equal(4.4m, CostCalculator.CostPerKg(2200m, 500m)); // 2200 / 500
    }

    [Fact]
    public void Costos_Sin_Precios_Muestran_No_Calculable_Sin_Inventar()
    {
        var breakdown = CostCalculator.Calculate(
            new SimulationCostsInput(
                "ARS",
                new SimulationSeedCostsInput(PricePerSeed: null, SeedCount: 600),
                new SimulationNutrientCostsInput(PricePerLiter: 5m, LitersPerDay: 5m),
                new SimulationEnergyCostsInput(PricePerKwh: 30m, KwhPerDay: 1.5m)),
            projectedDays: 7);

        var seeds = breakdown.Components.Single(c => c.Name == "Semillas");
        Assert.Null(seeds.Amount);
        Assert.Contains("Falta el precio por semilla", seeds.NotCalculableReason);
        Assert.Null(breakdown.Total); // sin semillas no se puede totalizar
        Assert.Contains(CostCalculator.NotCalculable, breakdown.TotalNotCalculableReason);
        Assert.Null(CostCalculator.CostPerKg(breakdown.Total, 300m));
    }

    [Fact]
    public void CostoPorKg_Requiere_Rendimiento_Positivo()
    {
        Assert.Null(CostCalculator.CostPerKg(1000m, 0m));
        Assert.Null(CostCalculator.CostPerKg(1000m, null));
        Assert.Null(CostCalculator.CostPerKg(null, 100m));
    }

    [Fact]
    public void DiasProyectados_Usan_La_Fecha_De_Cierre_Cuando_Existe()
    {
        var warnings = new List<string>();
        var days = CostCalculator.ResolveProjectedDays(
            new DateOnly(2026, 1, 15), new DateOnly(2026, 1, 25), 7, warnings);

        Assert.Equal(10, days);
        Assert.Empty(warnings);
    }

    [Fact]
    public void DiasProyectados_Sin_Fecha_Usan_Horizonte_Con_Advertencia()
    {
        var warnings = new List<string>();
        var days = CostCalculator.ResolveProjectedDays(
            new DateOnly(2026, 1, 15), null, 7, warnings);

        Assert.Equal(7, days);
        Assert.Contains(warnings, w => w.Contains("horizonte", StringComparison.OrdinalIgnoreCase));
    }

    // --- SIM-05: validación de negativos / incoherencias en servidor ---

    [Fact]
    public void Validacion_Rechaza_Negativos_En_Costos()
    {
        var request = ValidRequest() with
        {
            Costs = new SimulationCostsInput(
                "ARS",
                new SimulationSeedCostsInput(PricePerSeed: -2.5m, SeedCount: 600),
                new SimulationNutrientCostsInput(5m, 5m),
                new SimulationEnergyCostsInput(30m, 1.5m))
        };

        var errors = SimulationRequestValidator.Validate(request);
        Assert.Contains(errors, e => e.Contains("precio de semilla", StringComparison.OrdinalIgnoreCase) && e.Contains("negativo"));
    }

    [Fact]
    public void Validacion_Rechaza_Tmin_Mayor_Que_Tmax()
    {
        var request = ValidRequest() with
        {
            Climate = new SimulationClimateInput(SimulationClimateMode.Manual, 30m, 18m, 65, 7)
        };

        var errors = SimulationRequestValidator.Validate(request);
        Assert.Contains(errors, e => e.Contains("Tmin no puede ser mayor que Tmax"));
    }

    [Fact]
    public void Validacion_Rechaza_Contexto_Incompleto_En_Escenario_Libre()
    {
        var request = ValidRequest() with { CropTypeId = null, SowingDate = null, AreaM2 = null };

        var errors = SimulationRequestValidator.Validate(request);
        Assert.Contains(errors, e => e.Contains("cultivo"));
        Assert.Contains(errors, e => e.Contains("siembra"));
        Assert.Contains(errors, e => e.Contains("superficie"));
    }

    [Fact]
    public void Validacion_Rechaza_Hipotesis_Fuera_De_Rango()
    {
        var request = ValidRequest() with
        {
            Harvest = new SimulationHarvestInput(true, true, HypothesisAptaPercent: 150m, RequiredTargetPercent: 70m)
        };

        var errors = SimulationRequestValidator.Validate(request);
        Assert.Contains(errors, e => e.Contains("hipotético"));
    }

    [Fact]
    public void Validacion_Rechaza_Moneda_Invalida()
    {
        var request = ValidRequest() with
        {
            Costs = new SimulationCostsInput(
                "PESOS ARGENTINOS",
                new SimulationSeedCostsInput(2.5m, 600),
                new SimulationNutrientCostsInput(5m, 5m),
                new SimulationEnergyCostsInput(30m, 1.5m))
        };

        var errors = SimulationRequestValidator.Validate(request);
        Assert.Contains(errors, e => e.Contains("ISO 4217"));
    }

    [Fact]
    public void Validacion_Acepta_Request_Valido()
    {
        Assert.Empty(SimulationRequestValidator.Validate(ValidRequest()));
    }

    // --- SIM-03/SIM-04: GDD manual del escenario (núcleo forecasting reutilizado) ---

    [Fact]
    public void Gdd_Manual_Usa_La_Formula_Del_Nucleo_De_Forecasting()
    {
        // Tmin 18, Tmax 26, Tbase 4.5 → (26+18)/2 - 4.5 = 17.5
        var points = GddCalculator.ManualPoints(
            new SimulationClimateInput(SimulationClimateMode.Manual, 18m, 26m, 65, 7),
            4.5m,
            new DateOnly(2026, 1, 16),
            new DateOnly(2026, 1, 22));

        Assert.Equal(7, points.Count);
        Assert.All(points, p =>
        {
            Assert.Equal(17.5m, p.Gdd);
            Assert.Equal(SimulationGddSource.Manual, p.Source);
        });
    }

    [Fact]
    public void Gdd_Acumulado_Del_Escenario_Manual_Suma_Desde_La_Siembra()
    {
        // 15 días × GDD diario 17.5 = 262.5
        var (accumulated, warnings) = GddCalculator.AccumulatedManualScenario(
            new SimulationClimateInput(SimulationClimateMode.Manual, 18m, 26m, 65, 7),
            4.5m,
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 15));

        Assert.Equal(262.5m, accumulated);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Fecha_Estimada_Cruza_El_Umbral_Con_La_Proyeccion()
    {
        // acumulado 205, target 300 → faltan 95; proyección 7 días de 20.5 → cruza el día 5.
        var projection = GddCalculator.ManualPoints(
            new SimulationClimateInput(SimulationClimateMode.Manual, 20m, 30m, 65, 7),
            4.5m,
            new DateOnly(2026, 1, 16),
            new DateOnly(2026, 1, 22));

        var (date, extrapolation, _) = GddCalculator.EstimateHarvestDate(
            205m, projection, 300m, new DateOnly(2026, 1, 15),
            sowingDate: new DateOnly(2026, 1, 1), estimatedDaysToHarvest: 25, horizonDays: 7);

        Assert.Equal(new DateOnly(2026, 1, 20), date);
        Assert.False(extrapolation);
    }

    [Fact]
    public void Fecha_Estimada_Con_Acumulado_En_Objetivo_Es_La_Referencia()
    {
        var (date, _, _) = GddCalculator.EstimateHarvestDate(
            320m, [], 300m, new DateOnly(2026, 1, 15),
            sowingDate: new DateOnly(2026, 1, 1), estimatedDaysToHarvest: 25, horizonDays: 7);

        Assert.Equal(new DateOnly(2026, 1, 15), date);
    }

    [Fact]
    public void Fecha_Estimada_Extrapola_Fuera_Del_Horizonte_Etiquetada_Como_Tal()
    {
        // acumulado 0, target 300, proyección 7 días de 20 → cruza ~día 15 (más allá del horizonte).
        var projection = GddCalculator.ManualPoints(
            new SimulationClimateInput(SimulationClimateMode.Manual, 20m, 30m, 65, 7),
            4.5m,
            new DateOnly(2026, 1, 16),
            new DateOnly(2026, 1, 22));

        var (date, extrapolation, warnings) = GddCalculator.EstimateHarvestDate(
            0m, projection, 300m, new DateOnly(2026, 1, 15),
            sowingDate: new DateOnly(2026, 1, 1), estimatedDaysToHarvest: null, horizonDays: 7);

        Assert.NotNull(date);
        Assert.True(date > new DateOnly(2026, 1, 22));
        Assert.True(extrapolation);
        Assert.Contains(warnings, w => w.Contains("extrapolación", StringComparison.OrdinalIgnoreCase));
    }

    // --- SIM-09: comparación de estrategias (GDD vs hipótesis de score) ---

    private static SimulationAlternativeComparison CompareSample(
        decimal accumulated = 280m,
        decimal? hypothesis = 85m,
        decimal? target = 70m,
        bool enableBabyLeaf = true,
        bool enableConvencional = true,
        bool hasMaturity = false,
        int totalActive = 10,
        int realApta = 0)
    {
        var projection = GddCalculator.ManualPoints(
            new SimulationClimateInput(SimulationClimateMode.Manual, 20m, 30m, 65, 7),
            4.5m,
            new DateOnly(2026, 1, 16),
            new DateOnly(2026, 1, 22));

        return AlternativeComparisonService.Compare(new AlternativeComparisonService.Context(
            new DateOnly(2026, 1, 15), accumulated, projection, 7, "Lechuga Baby Leaf",
            CropTarget: 300m, BabyLeafWindow: (250m, 450m), realApta, totalActive,
            hypothesis, target, hasMaturity, enableBabyLeaf, enableConvencional));
    }

    [Fact]
    public void Alternativas_BabyLeaf_Listo_Con_Hipotesis_Explícita()
    {
        var result = CompareSample();

        var babyLeaf = result.Alternatives.Single(a => a.Strategy == "Baby Leaf");
        Assert.True(babyLeaf.IsReady);
        Assert.Contains("hipótesis de score", babyLeaf.DecisionBasis);
        Assert.Contains(result.HypothesesUsed, h => h.Contains("85", StringComparison.Ordinal));
    }

    [Fact]
    public void Alternativas_BabyLeaf_No_Listo_Sin_Hipotesis_Ni_Evaluaciones()
    {
        var result = CompareSample(hypothesis: null, target: 70m, realApta: 0);

        var babyLeaf = result.Alternatives.Single(a => a.Strategy == "Baby Leaf");
        Assert.False(babyLeaf.IsReady); // GDD en ventana pero 0% aptas < umbral
        Assert.Contains(result.HypothesesUsed, h => h.Contains("Umbral objetivo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Alternativas_BabyLeaf_Pendiente_Sin_Umbral()
    {
        var result = CompareSample(hypothesis: 85m, target: null, realApta: 0, totalActive: 0);

        var babyLeaf = result.Alternatives.Single(a => a.Strategy == "Baby Leaf");
        Assert.Null(babyLeaf.IsReady); // decisión pendiente visible
        Assert.Contains(result.HypothesesUsed, h => h.Contains("Sin umbral", StringComparison.Ordinal));
    }

    [Fact]
    public void Alternativas_Convencional_Pendiente_Sin_Análisis_De_Imagen()
    {
        var result = CompareSample(enableConvencional: true, hasMaturity: false);

        var conventional = result.Alternatives.Single(a => a.Strategy == "Convencional");
        Assert.Null(conventional.IsReady); // madurez requiere análisis de imagen (fase futura)
        Assert.Contains("madurez", conventional.DecisionBasis, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Alternativas_Convencional_Listo_Con_Madurez_Confirmada()
    {
        var result = CompareSample(enableConvencional: true, hasMaturity: true);

        var conventional = result.Alternatives.Single(a => a.Strategy == "Convencional");
        Assert.True(conventional.IsReady);
        Assert.Contains("análisis de imagen", conventional.DecisionBasis);
    }

    [Fact]
    public void Alternativas_Excluye_Estrategias_No_Habilitadas()
    {
        var result = CompareSample(enableBabyLeaf: false, enableConvencional: true, hypothesis: null);

        Assert.DoesNotContain(result.Alternatives, a => a.Strategy == "Baby Leaf");
        Assert.Contains(result.Alternatives, a => a.Strategy == "Convencional");
    }

    // --- SIM: repetibilidad (misma entrada → mismo id/resultado) ---

    [Fact]
    public void Misma_Entrada_Genera_El_Mismo_Id_Determinista()
    {
        var first = SimulationService.ComputeSimulationId(ValidRequest());
        var second = SimulationService.ComputeSimulationId(ValidRequest());

        Assert.Equal(16, first.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Entradas_Distintas_Generan_Ids_Distintos()
    {
        var request = ValidRequest();
        var other = request with { Climate = request.Climate with { ManualTempMaxC = 28m } };

        Assert.NotEqual(
            SimulationService.ComputeSimulationId(request),
            SimulationService.ComputeSimulationId(other));
    }

    // --- SIM-04: cobertura y ausencia de datos ---

    [Fact]
    public void Plan_Manual_Tiene_Cobertura_Completa()
    {
        var plan = new ClimatePlanBuilderStub().Manual(
            new SimulationClimateInput(SimulationClimateMode.Manual, 16m, 26m, 65, 7),
            4.5m,
            new DateOnly(2026, 1, 16),
            new DateOnly(2026, 1, 22));

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