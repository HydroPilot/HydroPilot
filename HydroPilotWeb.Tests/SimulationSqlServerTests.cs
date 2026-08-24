using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Simulation;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Tests.Integration;

/// <summary>
/// Pruebas de integración del módulo de simulación (SIM-01..SIM-10) sobre SQL
/// Server real con las MIGRACIONES y el SEED del producto. Cubren: contexto lote/
/// cultivo, AsOfDate sin fuga de lecturas futuras, costos (No calculable),
/// validación de negativos, ausencia de datos climáticos, repetibilidad,
/// regla híbrida con hipótesis explícita, AISLAMIENTO (cero invocaciones a
/// hardware y cero modificaciones de entidades productivas) y grilla opcional.
/// </summary>
[Collection("simulation-sql")]
public class SimulationSqlServerTests
{
    private readonly SimulationSqlFixture _fixture;

    public SimulationSqlServerTests(SimulationSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static DateTime UtcDate(DateOnly d) => d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private HydroPilotDbContext NewContext() => _fixture.Factory.CreateDbContext();

    private static SimulationCostsInput FullCosts() => new(
        "ARS",
        new SimulationSeedCostsInput(PricePerSeed: 2.5m, SeedCount: 600),
        new SimulationNutrientCostsInput(PricePerLiter: 5m, LitersPerDay: 5m),
        new SimulationEnergyCostsInput(PricePerKwh: 30m, KwhPerDay: 1.5m));

    private static SimulationRequest ManualLotRequest(
        int lotId,
        SimulationCostsInput? costs = null,
        SimulationHarvestInput? harvest = null,
        DateOnly? referenceDate = null,
        decimal tmin = 16m,
        decimal tmax = 26m) => new(
        LotId: lotId,
        CropTypeId: null,
        SowingDate: null,
        AreaM2: null,
        ReferenceDate: referenceDate ?? Today,
        Climate: new SimulationClimateInput(SimulationClimateMode.Manual, tmin, tmax, 65, ForecastHorizonDays: 7),
        Agronomic: new SimulationAgronomicInput(6.0m, 1.5m),
        Costs: costs ?? FullCosts(),
        Harvest: harvest ?? new SimulationHarvestInput(EnableBabyLeaf: true, EnableConvencional: true, HypothesisAptaPercent: 80m, RequiredTargetPercent: 70m),
        SaveScenario: false);

    // --- SIM-02/SIM-04: resultado único con contexto de lote ---

    [Fact]
    public async Task Simulacion_Lote_Modo_Manual_Devuelve_Resultado_Completo()
    {
        await using var context = NewContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(context);
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-10));
        await ForecastTestData.SeedConstantTemperatureReadingsAsync(context, UtcDate(lot.SowingDate), 10, 25m);

        var preview = await _fixture.Simulation.PreviewAsync(ManualLotRequest(lot.Id));

        Assert.True(preview.IsValid);
        var result = preview.Result!;
        Assert.True(result.IsSimulated);
        Assert.Equal("lote", result.Context.ContextKind);
        Assert.Equal("manual", result.Gdd.ProviderName);

        // 10 días × GDD(25,25,4.5)=20.5 → 205
        Assert.Equal(205m, result.Gdd.Accumulated);
        Assert.Equal(300m, result.Gdd.GddTarget);

        // Proyección manual 7 días × GDD(26,16,4.5)=16.5; cruce a 300 el día 6.
        Assert.Equal(Today.AddDays(6), result.Gdd.EstimatedHarvestDate);
        Assert.Equal(6, result.Gdd.DaysRemaining);
        Assert.Equal(7, result.Gdd.CoveredDays);
        Assert.Equal(0, result.Gdd.MissingDays);

        // Rendimiento desde el núcleo de forecasting (sin ciclos cerrados → baseline).
        Assert.True(result.Yield.Base > 0m);
        Assert.True(result.Yield.Conservative <= result.Yield.Base && result.Yield.Base <= result.Yield.Optimistic);

        // Contexto de lote + grilla (SIM-08/SIM-10): 3×4 posiciones, sin plantas.
        Assert.NotNull(result.Lot);
        Assert.Equal(12, result.Lot.Grid.Count);
        Assert.All(result.Lot.Grid, cell => Assert.Equal("empty", cell.CellKind));
        Assert.Equal(0, result.Lot.ActivePlants);

        // Regla híbrida: GDD 205 < ventana (250–450) → no listo, con razón visible.
        Assert.NotNull(result.Harvest);
        Assert.False(result.Harvest.GddInWindow);
        Assert.Equal(Today.AddDays(3), result.Harvest.BabyLeafEntryDate); // cruce a 250 el día 3

        // Costos con días proyectados = 6 (referencia → cosecha).
        Assert.NotNull(result.Costs.Total);
        Assert.Equal(6, result.Costs.ProjectedDays);
    }

    [Fact]
    public async Task Simulacion_Escenario_Libre_Por_Cultivo_En_Modo_Manual()
    {
        await using var context = NewContext();
        var crop = await context.CropTypes.FirstAsync();
        var sowing = Today.AddDays(-15);
        var request = new SimulationRequest(
            LotId: null,
            CropTypeId: crop.Id,
            SowingDate: sowing,
            AreaM2: 4.5m,
            ReferenceDate: Today,
            Climate: new SimulationClimateInput(SimulationClimateMode.Manual, 18m, 26m, 65, 7),
            Agronomic: null,
            Costs: FullCosts(),
            Harvest: new SimulationHarvestInput(true, true, 85m, 70m),
            SaveScenario: false);

        var preview = await _fixture.Simulation.PreviewAsync(request);

        Assert.True(preview.IsValid);
        var result = preview.Result!;
        Assert.Equal("cultivo", result.Context.ContextKind);
        Assert.Null(result.Lot);

        // 16 días (siembra Today-15 ... Today inclusive) × GDD(26,18,4.5)=17.5 → 280
        Assert.Equal(280m, result.Gdd.Accumulated);
        Assert.Equal(300m, result.Gdd.GddTarget);

        // Saldo 20 con proyección 16.5/día → cruce el día 2.
        Assert.Equal(Today.AddDays(2), result.Gdd.EstimatedHarvestDate);

        // Rendimiento baseline del cultivo: 3.0 kg/m² × 4.5 m² = 13.5 base; avance 280/300 → confianza 92.
        Assert.Equal(13.5m, result.Yield.Base);
        Assert.Equal(92, result.Yield.ConfidencePercent);
        Assert.Equal(0, result.Yield.HistoryCycles);

        // Escenario libre sin plantas activas: la regla híbrida queda pendiente (no se inventa).
        Assert.NotNull(result.Harvest);
        Assert.Null(result.Harvest.IsReady);
        Assert.Contains(result.Harvest.Warnings, w => w.Contains("sin plantas activas", StringComparison.OrdinalIgnoreCase));
    }

    // --- SIM-04: AsOfDate sin fuga de lecturas futuras ---

    [Fact]
    public async Task Simulacion_Respeta_AsOfDate_Sin_Consumir_Lecturas_Futuras()
    {
        await using var context = NewContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(context);
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-10));

        // 10 días de lecturas (20°C → GDD 15.5/día) desde la siembra (días Today-10..Today-1).
        await ForecastTestData.SeedConstantTemperatureReadingsAsync(context, UtcDate(lot.SowingDate), 10, 20m);

        // Referencia 5 días atrás: solo las lecturas ≤ ese día (6 de 10) alimentan el
        // cálculo; las 4 restantes EXISTEN (son pasado real) pero quedan fuera de AsOfDate.
        var reference = Today.AddDays(-5);
        var preview = await _fixture.Simulation.PreviewAsync(ManualLotRequest(lot.Id, referenceDate: reference));

        Assert.True(preview.IsValid);
        var result = preview.Result!;

        var expected = await _fixture.Gdd.GetAccumulatedGddAsync(lot, reference);
        Assert.Equal(Math.Round(expected, 2), result.Gdd.Accumulated);
        Assert.Equal(6 * 15.5m, result.Gdd.Accumulated); // 93: sin fuga de las 4 lecturas posteriores a AsOf
        Assert.Contains(result.Warnings, w => w.Contains("Consulta simulada", StringComparison.OrdinalIgnoreCase));

        // Con referencia hoy: se usan las 10 lecturas (155), nunca valores futuros.
        var todayPreview = await _fixture.Simulation.PreviewAsync(ManualLotRequest(lot.Id));
        Assert.True(todayPreview.IsValid);
        Assert.Equal(10 * 15.5m, todayPreview.Result!.Gdd.Accumulated);
    }

    // --- SIM-05: costos y validación ---

    [Fact]
    public async Task Simulacion_Costos_No_Calculable_Cuando_Faltan_Precios()
    {
        await using var context = NewContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(context);
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-10));
        await ForecastTestData.SeedConstantTemperatureReadingsAsync(context, UtcDate(lot.SowingDate), 5, 25m);

        var costs = new SimulationCostsInput(
            "ARS",
            new SimulationSeedCostsInput(PricePerSeed: null, SeedCount: 600),
            new SimulationNutrientCostsInput(5m, 5m),
            new SimulationEnergyCostsInput(30m, 1.5m));

        var preview = await _fixture.Simulation.PreviewAsync(ManualLotRequest(lot.Id, costs: costs));

        Assert.True(preview.IsValid);
        var result = preview.Result!;

        var seeds = result.Costs.Components.Single(c => c.Name == "Semillas");
        Assert.Null(seeds.Amount);
        Assert.Null(result.Costs.Total);
        Assert.Contains(CostCalculator.NotCalculable, result.Costs.TotalNotCalculableReason);
        Assert.Null(result.Costs.CostPerKg);
    }

    [Fact]
    public async Task Simulacion_Negativos_Se_Rechazan_En_Servidor()
    {
        await using var context = NewContext();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-10));

        var costs = new SimulationCostsInput(
            "ARS",
            new SimulationSeedCostsInput(PricePerSeed: -1m, SeedCount: 600),
            new SimulationNutrientCostsInput(5m, 5m),
            new SimulationEnergyCostsInput(30m, 1.5m));

        var preview = await _fixture.Simulation.PreviewAsync(ManualLotRequest(lot.Id, costs: costs));

        Assert.False(preview.IsValid);
        Assert.Null(preview.Result);
        Assert.Contains(preview.ValidationErrors, e => e.Contains("negativo", StringComparison.OrdinalIgnoreCase));
    }

    // --- SIM-03: sin datos climáticos degrada de forma visible ---

    [Fact]
    public async Task Simulacion_Sin_Datos_Climaticos_Degrada_Con_Advertencias()
    {
        await using var context = NewContext();
        var crop = await context.CropTypes.FirstAsync();
        var request = new SimulationRequest(
            LotId: null,
            CropTypeId: crop.Id,
            SowingDate: Today.AddDays(-15),
            AreaM2: 4.5m,
            ReferenceDate: Today,
            Climate: new SimulationClimateInput(SimulationClimateMode.Historic, null, null, null, 7),
            Agronomic: null,
            Costs: FullCosts(),
            Harvest: new SimulationHarvestInput(true, true, null, null),
            SaveScenario: false);

        var preview = await _fixture.Simulation.PreviewAsync(request);

        Assert.True(preview.IsValid);
        var result = preview.Result!;
        Assert.Empty(result.Gdd.Projection); // sin datos climáticos: nada se proyecta ni se asume
        Assert.Equal(0, result.Gdd.CoveredDays);
        Assert.Equal(7, result.Gdd.MissingDays);
        Assert.Contains(result.Warnings, w => w.Contains("histórico sin lote", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Warnings, w => w.Contains("Sin proyección GDD", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(result.Gdd.EstimatedHarvestDate); // respaldo por días de ciclo del cultivo, etiquetado
    }

    // --- SIM-08/09: regla híbrida con hipótesis explícita ---

    [Fact]
    public async Task Simulacion_Regla_Hibrida_Usa_Hipotesis_Explicita_Sin_Tocar_El_Lote()
    {
        await using var context = NewContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(context);
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-12), babyLeafTargetPercent: 70m);
        await ForecastTestData.SeedConstantTemperatureReadingsAsync(context, UtcDate(lot.SowingDate), 11, 30m); // GDD 25.5/día → 280.5

        // 10 plantas activas "En desarrollo" (sin evaluaciones aptas reales).
        var stage = await context.CommercialStages.FirstAsync(s => s.Name == "En desarrollo");
        var pheno = await context.PhenologicalStages.FirstAsync();
        for (var i = 1; i <= 10; i++)
        {
            context.Plants.Add(new Plant
            {
                LotId = lot.Id,
                Row = i,
                Column = 1,
                CommercialStageId = stage.Id,
                PhenologicalStageId = pheno.Id,
                OperationalState = PlantOperationalState.Activa,
                CreatedAtUtc = DateTime.UtcNow
            });
        }
        await context.SaveChangesAsync();

        // Hipótesis del usuario: 85% de aptas esperadas; umbral 70%.
        var request = ManualLotRequest(
            lot.Id,
            harvest: new SimulationHarvestInput(true, true, HypothesisAptaPercent: 85m, RequiredTargetPercent: 70m));
        var preview = await _fixture.Simulation.PreviewAsync(request);

        Assert.True(preview.IsValid);
        var result = preview.Result!;

        Assert.Equal(280.5m, result.Gdd.Accumulated); // dentro de la ventana 250–450
        Assert.NotNull(result.Harvest);
        Assert.True(result.Harvest.GddInWindow);
        Assert.True(result.Harvest.AptaPercentIsHypothesis);
        Assert.Equal(85m, result.Harvest.AptaPercentUsed);
        Assert.Equal(70m, result.Harvest.RequiredPercent);
        Assert.True(result.Harvest.IsReady); // 85% (hipótesis) ≥ 70% → listo
        Assert.NotNull(result.Harvest.DecisionReason);
        Assert.Contains("hipótesis", result.Harvest.DecisionReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10, result.Lot!.ActivePlants); // el lote real no cambió

        // El lote real sigue sin snapshot de GDD (la simulación no escribe).
        var after = await context.Lots.AsNoTracking().FirstAsync(l => l.Id == lot.Id);
        Assert.Null(after.AccumulatedGdd);
    }

    [Fact]
    public async Task Simulacion_Regla_Hibrida_Queda_Pendiente_Sin_Umbral()
    {
        await using var context = NewContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(context);
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-12), babyLeafTargetPercent: null);
        await ForecastTestData.SeedConstantTemperatureReadingsAsync(context, UtcDate(lot.SowingDate), 11, 30m);

        var request = ManualLotRequest(
            lot.Id,
            harvest: new SimulationHarvestInput(true, false, HypothesisAptaPercent: null, RequiredTargetPercent: null));
        var preview = await _fixture.Simulation.PreviewAsync(request);

        Assert.True(preview.IsValid);
        var result = preview.Result!;
        Assert.NotNull(result.Harvest);
        Assert.Null(result.Harvest.IsReady); // decisión pendiente visible, nunca una constante oculta
        Assert.Contains(result.Harvest.Warnings, w => w.Contains("pendiente", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Simulacion_Regla_Hibrida_Usa_Porcentaje_Apto_Real_De_Evaluaciones()
    {
        await using var context = NewContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(context);

        // Lote demo del seed: 7 plantas "Baby Leaf apta" activas sobre 19 activas (36.8%),
        // con evaluaciones persistidas; sin hipótesis el escenario usa el dato real.
        var demoLot = await context.Lots.FirstAsync(l => l.Name == "Lote Demo 01");
        var request = ManualLotRequest(
            demoLot.Id,
            harvest: new SimulationHarvestInput(true, false, HypothesisAptaPercent: null, RequiredTargetPercent: 70m));

        var preview = await _fixture.Simulation.PreviewAsync(request);

        Assert.True(preview.IsValid);
        var result = preview.Result!;
        Assert.NotNull(result.Harvest);
        Assert.Equal(7, result.Harvest.RealAptaCount);
        Assert.Equal(19, result.Harvest.TotalActivePlants);
        Assert.Equal(36.8m, result.Harvest.AptaPercentUsed);
        Assert.False(result.Harvest.AptaPercentIsHypothesis); // dato real, no hipótesis
        Assert.Equal(19, result.Lot!.ActivePlants);
        Assert.False(result.Harvest.IsReady); // 36.8% < umbral 70%
    }

    // --- SIM: repetibilidad ---

    [Fact]
    public async Task Simulacion_Repetida_Devuelve_El_Mismo_Resultado()
    {
        await using var context = NewContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(context);
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-10));
        await ForecastTestData.SeedConstantTemperatureReadingsAsync(context, UtcDate(lot.SowingDate), 10, 25m);

        var request = ManualLotRequest(lot.Id);
        var first = await _fixture.Simulation.PreviewAsync(request);
        var second = await _fixture.Simulation.PreviewAsync(request);

        Assert.True(first.IsValid && second.IsValid);
        Assert.Equal(first.Result!.SimulationId, second.Result!.SimulationId);
        Assert.Equal(first.Result.Gdd.EstimatedHarvestDate, second.Result.Gdd.EstimatedHarvestDate);
        Assert.Equal(first.Result.Gdd.Accumulated, second.Result.Gdd.Accumulated);
        Assert.Equal(first.Result.Yield, second.Result.Yield);
        Assert.Equal(first.Result.Costs.Total, second.Result.Costs.Total);
    }

    // --- SIM-07: AISLAMIENTO ---

    [Fact]
    public async Task Simulacion_No_Modifica_Entidades_Productivas()
    {
        await using var context = NewContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(context);
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-10));
        await ForecastTestData.SeedConstantTemperatureReadingsAsync(context, UtcDate(lot.SowingDate), 10, 25m);

        // Modo pronóstico con rango ya cubierto en DB: sin fetch ni escritura.
        var from = Today.AddDays(1);
        for (var i = 0; i < 7; i++)
        {
            context.DailyWeatherForecasts.Add(new DailyWeatherForecast
            {
                Date = from.AddDays(i),
                TempMin = 15m,
                TempMax = 25m,
                FetchedAt = DateTime.UtcNow
            });
        }
        await context.SaveChangesAsync();

        var before = await ProductiveSnapshotAsync();

        foreach (var request in new[]
                 {
                     ManualLotRequest(lot.Id), // manual
                     ManualLotRequest(lot.Id, referenceDate: Today.AddDays(-2)), // histórico no (modo manual) + AsOfDate
                     new SimulationRequest(lot.Id, null, null, null, Today,
                         new SimulationClimateInput(SimulationClimateMode.Forecast, null, null, null, 7),
                         new SimulationAgronomicInput(6.0m, 1.5m), FullCosts(),
                         new SimulationHarvestInput(true, true, 80m, 70m), SaveScenario: false),
                     new SimulationRequest(lot.Id, null, null, null, Today,
                         new SimulationClimateInput(SimulationClimateMode.Historic, null, null, null, 7),
                         new SimulationAgronomicInput(6.0m, 1.5m), FullCosts(),
                         new SimulationHarvestInput(true, true, 80m, 70m), SaveScenario: false)
                 })
        {
            var preview = await _fixture.Simulation.PreviewAsync(request);
            Assert.True(preview.IsValid);
        }

        var after = await ProductiveSnapshotAsync();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Simulacion_Cero_Invocaciones_A_Hardware()
    {
        var gateway = new CountingHardwareGateway();

        await using var context = NewContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(context);
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-10));
        await ForecastTestData.SeedConstantTemperatureReadingsAsync(context, UtcDate(lot.SowingDate), 6, 25m);

        var preview = await _fixture.Simulation.PreviewAsync(ManualLotRequest(lot.Id));
        Assert.True(preview.IsValid);
        Assert.Equal(0, gateway.Calls); // cero invocaciones con el adaptador falso presente

        // La frontera de hardware ni siquiera es dependencia del servicio: no hay
        // forma de que la simulación invoque un gateway que nunca recibe.
        var constructorParams = typeof(SimulationService).GetConstructors().SelectMany(c => c.GetParameters());
        Assert.DoesNotContain(constructorParams, p => p.ParameterType == typeof(IHardwareGateway));
    }

    [Fact]
    public async Task Simulacion_Con_SaveScenario_No_Persiste_Y_Avisa()
    {
        await using var context = NewContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(context);
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-10));
        await ForecastTestData.SeedConstantTemperatureReadingsAsync(context, UtcDate(lot.SowingDate), 5, 25m);

        var request = ManualLotRequest(lot.Id) with { SaveScenario = true };
        var preview = await _fixture.Simulation.PreviewAsync(request);

        Assert.True(preview.IsValid);
        Assert.Contains(preview.Result!.Warnings, w => w.Contains("no está disponible", StringComparison.OrdinalIgnoreCase));

        var predictionCount = await context.Predictions.CountAsync();
        Assert.Equal(0, predictionCount); // nada se guardó
    }

    // --- Helpers de aislamiento ---

    private sealed record ProductiveSnapshot(
        int Lots, decimal? LotsAccumulatedGdd, int Predictions, int SensorReadings,
        int Plants, int BabyLeafEvaluations, int PlantStageHistories, int BabyLeafConfigs,
        int CropTypes, int PhenologicalStages, int CommercialStages, int LotStatuses,
        int NodeLotAssignments, int DailyWeatherForecasts, int PlantImages, int PlantImageAnalyses);

    private async Task<ProductiveSnapshot> ProductiveSnapshotAsync()
    {
        await using var context = NewContext();
        return new ProductiveSnapshot(
            Lots: await context.Lots.CountAsync(),
            LotsAccumulatedGdd: await context.Lots.SumAsync(l => (decimal?)l.AccumulatedGdd),
            Predictions: await context.Predictions.CountAsync(),
            SensorReadings: await context.SensorReadings.CountAsync(),
            Plants: await context.Plants.CountAsync(),
            BabyLeafEvaluations: await context.BabyLeafEvaluations.CountAsync(),
            PlantStageHistories: await context.PlantStageHistories.CountAsync(),
            BabyLeafConfigs: await context.BabyLeafConfigs.CountAsync(),
            CropTypes: await context.CropTypes.CountAsync(),
            PhenologicalStages: await context.PhenologicalStages.CountAsync(),
            CommercialStages: await context.CommercialStages.CountAsync(),
            LotStatuses: await context.LotStatuses.CountAsync(),
            NodeLotAssignments: await context.NodeLotAssignments.CountAsync(),
            DailyWeatherForecasts: await context.DailyWeatherForecasts.CountAsync(),
            PlantImages: await context.PlantImages.CountAsync(),
            PlantImageAnalyses: await context.PlantImageAnalyses.CountAsync());
    }
}

/// <summary>Adaptador falso de hardware que cuenta invocaciones (SIM-07).</summary>
public sealed class CountingHardwareGateway : IHardwareGateway
{
    public int Calls { get; private set; }

    public Task SendCommandAsync(string target, string command, CancellationToken ct = default)
    {
        Calls++;
        return Task.CompletedTask;
    }
}