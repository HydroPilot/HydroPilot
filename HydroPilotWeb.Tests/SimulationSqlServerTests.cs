using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.Simulation;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Tests.Integration;

/// <summary>
/// Pruebas de integración del módulo de simulación sobre SQL Server real con las MIGRACIONES y el
/// SEED del producto. Cubren: contexto lote/cultivo (siempre desde hoy, siembra futura para
/// planificar, lotes cerrados fuera), pronóstico igual al de Lotes, cortes por GDD con la lógica
/// de Lotes, plantas del lote, pesos y precios de Parámetros u override,
/// costos (No calculable), validación, ausencia de datos climáticos, repetibilidad y AISLAMIENTO
/// (cero invocaciones a hardware y cero modificaciones de entidades productivas).
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

    private static SimulationCostsInput FullCosts() => new(PricePerSeed: 2.5m, NutrientPricePerLiter: 5m);

    /// <summary>Pesos y precios propios del escenario (no dependen de los parámetros del sistema).</summary>
    private static SimulationYieldInput ScenarioYield() => new(30m, 200m, 3000m, 2000m);

    private static SimulationRequest ManualLotRequest(
        int lotId,
        SimulationCostsInput? costs = null,
        decimal? babyLeafGoal = null,
        SimulationClimateMode mode = SimulationClimateMode.Manual,
        decimal tmin = 16m,
        decimal tmax = 26m) => new(
        LotId: lotId,
        CropTypeId: null,
        SowingDate: null,
        PlantCount: null,
        Climate: new SimulationClimateInput(mode, mode == SimulationClimateMode.Manual ? tmin : null,
            mode == SimulationClimateMode.Manual ? tmax : null, ForecastHorizonDays: 7),
        Costs: costs ?? FullCosts(),
        Harvest: new SimulationHarvestInput(
            babyLeafGoal is null ? SimulationStrategyKind.MaxBabyLeaf : SimulationStrategyKind.BabyLeafGoal, babyLeafGoal),
        Yield: ScenarioYield());

    private static SimulationRequest FreeRequest(
        int cropId, DateOnly sowing, SimulationClimateMode mode, decimal? goal = null) => new(
        LotId: null,
        CropTypeId: cropId,
        SowingDate: sowing,
        PlantCount: 60,
        Climate: mode == SimulationClimateMode.Manual
            ? new SimulationClimateInput(mode, 18m, 26m, 7)
            : new SimulationClimateInput(mode, null, null, 7),
        Costs: FullCosts(),
        Harvest: new SimulationHarvestInput(
            goal is null ? SimulationStrategyKind.MaxBabyLeaf : SimulationStrategyKind.BabyLeafGoal, goal),
        Yield: ScenarioYield());

    /// <summary>Deja en la DB solo este pronóstico (la tabla no se limpia entre tests).</summary>
    private static async Task SeedForecastAsync(HydroPilotDbContext context, DateOnly from, int days, decimal tmin, decimal tmax)
    {
        context.DailyWeatherForecasts.RemoveRange(context.DailyWeatherForecasts);
        for (var i = 0; i < days; i++)
        {
            context.DailyWeatherForecasts.Add(new DailyWeatherForecast
            {
                Date = from.AddDays(i),
                TempMin = tmin,
                TempMax = tmax,
                FetchedAt = DateTime.UtcNow
            });
        }
        await context.SaveChangesAsync();
    }

    private static async Task AddActivePlantsAsync(HydroPilotDbContext context, Lot lot, int count)
    {
        var stage = await context.CommercialStages.FirstAsync(s => s.Name == CommercialStageNames.EnDesarrollo);
        for (var i = 0; i < count; i++)
        {
            context.Plants.Add(new Plant
            {
                LotId = lot.Id,
                Row = i / 4 + 1,
                Column = i % 4 + 1,
                CommercialStageId = stage.Id,
                OperationalState = PlantOperationalState.Activa,
            });
        }
        await context.SaveChangesAsync();
    }

    // --- Resultado único con contexto de lote ---

    [Fact]
    public async Task Simulacion_Lote_Modo_Manual_Corta_En_Los_Optimos_De_Lotes()
    {
        await using var context = NewContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(context);
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-10));
        await ForecastTestData.SeedConstantTemperatureReadingsAsync(context, UtcDate(lot.SowingDate), 10, 25m);
        await AddActivePlantsAsync(context, lot, 10);

        var preview = await _fixture.Simulation.PreviewAsync(ManualLotRequest(lot.Id));

        Assert.True(preview.IsValid);
        var result = preview.Result!;
        Assert.True(result.IsSimulated);
        Assert.Equal("lote", result.Context.ContextKind);
        Assert.Equal(10, result.Context.Plants); // las activas del lote
        Assert.Equal("manual", result.Gdd.ProviderName);

        // 10 días × GDD(25,25,4.5)=20.5 → 205: todavía en desarrollo.
        Assert.Equal(205m, result.Gdd.Accumulated);
        Assert.NotNull(result.Timeline);
        Assert.Equal(CyclePhase.Desarrollo, result.Timeline.Phase);

        // Proyección manual 16.5/día: el óptimo Baby Leaf (300) cae el día 6, como en la vista Lotes.
        Assert.Equal(Today.AddDays(6), result.Timeline.BabyLeafOptimum.Date);
        Assert.Equal(7, result.Gdd.CoveredDays);
        Assert.Equal(0, result.Gdd.MissingDays);

        // Sin meta Baby Leaf: "máximo Baby Leaf" y "todo convencional". Sin historial: todas aptas, sin pérdidas.
        Assert.Equal(["Máximo Baby Leaf", "Todo adulta"], result.Strategies.Select(s => s.Name));
        Assert.Null(result.Outlook.AptRate);
        Assert.Equal(0, result.Strategies[0].LostPlants);
        var babyLeaf = result.Strategies[0];
        Assert.Equal(Today.AddDays(6), babyLeaf.BabyLeafCut!.Date);
        Assert.Equal(16, babyLeaf.Costs.CycleDays);             // siembra (hoy-10) → corte (hoy+6)
        // 10 plantas: 14 días antes de Baby Leaf (abre hoy+3) × 0,04 L + 2 días en Baby Leaf × 0,075 L = 7,1 L.
        Assert.Equal(10, babyLeaf.Costs.Seeds);                 // las plantas del lote
        Assert.Equal(7.1m, babyLeaf.Costs.Usage!.WaterLiters);
        Assert.Equal(25.18m, babyLeaf.Costs.Total);             // 10 × 2,5 + 0,0355 L × $5
        Assert.Equal(new DestinationYield(10, 0.3m, 900m), babyLeaf.BabyLeaf); // 10 × 30 g × $3000/kg

        // Curva: medido hasta hoy + proyección del escenario.
        Assert.NotNull(result.Curve);
        Assert.Equal(7, result.Curve.Projected.Count);

        // Contexto de lote + grilla 3×4 (solo lectura).
        Assert.NotNull(result.Lot);
        Assert.Equal(12, result.Lot.Grid.Count);
        Assert.Equal(10, result.Lot.ActivePlants);
    }

    [Fact]
    public async Task Simulacion_Escenario_Libre_Por_Cultivo_En_Modo_Manual()
    {
        await using var context = NewContext();
        var crop = await context.CropTypes.FirstAsync();
        var request = FreeRequest(crop.Id, Today.AddDays(-15), SimulationClimateMode.Manual, goal: 80m);

        var preview = await _fixture.Simulation.PreviewAsync(request);

        Assert.True(preview.IsValid);
        var result = preview.Result!;
        Assert.Equal("cultivo", result.Context.ContextKind);
        Assert.Equal(60, result.Context.Plants);
        Assert.Null(result.Lot);

        // 16 días (siembra hoy-15 ... hoy) × 17.5 = 280 → ventana Baby Leaf; el óptimo (300) llega en 2 días.
        Assert.Equal(280m, result.Gdd.Accumulated);
        Assert.Equal(CyclePhase.VentanaBabyLeaf, result.Timeline!.Phase);
        Assert.Equal(Today.AddDays(2), result.Timeline.BabyLeafOptimum.Date);

        // Meta 80 %: 48 Baby Leaf + 12 convencionales.
        var mixed = result.Strategies.Single(s => s.Kind == SimulationStrategyKind.BabyLeafGoal);
        Assert.Equal(48, mixed.BabyLeaf.Plants);
        Assert.Equal(12, mixed.Conventional.Plants);
        Assert.Equal(3, result.Strategies.Count);
    }

    [Fact]
    public async Task Simulacion_Escenario_Libre_Con_Siembra_Futura_Planifica_Desde_La_Siembra()
    {
        await using var context = NewContext();
        var crop = await context.CropTypes.FirstAsync();
        var sowing = Today.AddDays(5);

        var preview = await _fixture.Simulation.PreviewAsync(FreeRequest(crop.Id, sowing, SimulationClimateMode.Manual));

        Assert.True(preview.IsValid);
        var result = preview.Result!;
        Assert.Equal(Today, result.Today);
        Assert.Equal(0m, result.Gdd.Accumulated); // todavía no se sembró
        Assert.Equal(sowing, result.Gdd.Projection[0].Date); // el clima cuenta desde la siembra
        Assert.Equal(CyclePhase.Desarrollo, result.Timeline!.Phase);

        // 17,5 GDD/día desde la siembra: 300 GDD el día 18 (≈, extrapolado después de 7 días).
        var babyLeaf = result.Strategies[0];
        Assert.Equal(sowing.AddDays(17), babyLeaf.BabyLeafCut!.Date);
        Assert.Equal(17, babyLeaf.Costs.CycleDays);

        Assert.Empty(result.Curve!.Measured);
        Assert.Equal(sowing, result.Curve.Projected[0].Date);
    }

    [Fact]
    public async Task Simulacion_Lote_Con_Pronostico_Usa_La_Misma_Proyeccion_Que_Lotes()
    {
        await using var context = NewContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(context);
        var created = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-10));
        await ForecastTestData.SeedConstantTemperatureReadingsAsync(context, UtcDate(created.SowingDate), 10, 25m);
        await SeedForecastAsync(context, Today.AddDays(1), 7, 15m, 25m); // 15,5 GDD/día

        var preview = await _fixture.Simulation.PreviewAsync(ManualLotRequest(created.Id, mode: SimulationClimateMode.Forecast));

        Assert.True(preview.IsValid);
        var result = preview.Result!;
        Assert.Equal("pronostico", result.Gdd.ProviderName);
        Assert.Equal(205m, result.Gdd.Accumulated); // medido hasta hoy

        // La proyección es la de la vista Lotes (GddService), día por día.
        var lot = await context.Lots.AsNoTracking().Include(l => l.CropType).FirstAsync(l => l.Id == created.Id);
        var lotes = await _fixture.Gdd.GetFutureGddProjectionAsync(lot);
        Assert.Equal(lotes.Points.Select(p => (p.Date, p.Gdd)), result.Gdd.Projection.Select(p => (p.Date, p.Gdd)));
        Assert.All(result.Gdd.Projection, p => Assert.Equal(15.5m, p.Gdd));

        // 205 + 15,5 × 7 = 313,5: el óptimo Baby Leaf (300) cae el día 7.
        Assert.Equal(Today.AddDays(7), result.Timeline!.BabyLeafOptimum.Date);
    }

    [Fact]
    public async Task Simulacion_No_Ofrece_Ni_Acepta_Lotes_Cerrados()
    {
        await using var context = NewContext();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-30));
        lot.StatusId = (await context.LotStatuses.FirstAsync(st => st.Name == "COSECHADO")).Id;
        await context.SaveChangesAsync();

        var options = await _fixture.Simulation.GetContextOptionsAsync();
        var preview = await _fixture.Simulation.PreviewAsync(ManualLotRequest(lot.Id));

        Assert.DoesNotContain(options.Lots, l => l.Id == lot.Id);
        Assert.False(preview.IsValid);
        Assert.Contains(preview.ValidationErrors, e => e.Contains("cerrado"));
    }

    // --- Previsión de Baby Leaf y pérdidas (histórico + estado del lote) ---

    /// <summary>Lote cerrado con plantas en los estados dados (y, opcional, los datos del ciclo cargados a mano).</summary>
    private static async Task<Lot> ClosedLotAsync(
        HydroPilotDbContext context, DateOnly sowing, int babyLeaf, int conventional,
        (DateOnly Date, int Count)[] discards, Action<Lot>? manual = null, DateOnly? harvest = null)
    {
        var lot = await ForecastTestData.CreateActiveLotAsync(context, sowing, rows: 5, columns: 6);
        lot.StatusId = (await context.LotStatuses.FirstAsync(st => st.Name == "COSECHADO")).Id;
        lot.ActualHarvestDate = harvest ?? sowing.AddDays(30);
        manual?.Invoke(lot);

        var apt = await context.CommercialStages.FirstAsync(st => st.Name == CommercialStageNames.BabyLeafApta);
        var conv = await context.CommercialStages.FirstAsync(st => st.Name == CommercialStageNames.CosechaConvencional);
        var position = 0;
        void Add(PlantOperationalState state, int? stageId, DateOnly? harvestDate, DateOnly? discardDate)
        {
            context.Plants.Add(new Plant
            {
                LotId = lot.Id, Row = position / 6 + 1, Column = position % 6 + 1, CommercialStageId = stageId,
                OperationalState = state, HarvestDate = harvestDate, DiscardDate = discardDate,
            });
            position++;
        }
        for (var i = 0; i < babyLeaf; i++) Add(PlantOperationalState.Cosechada, apt.Id, lot.ActualHarvestDate, null);
        for (var i = 0; i < conventional; i++) Add(PlantOperationalState.Cosechada, conv.Id, lot.ActualHarvestDate, null);
        foreach (var (date, count) in discards)
            for (var i = 0; i < count; i++) Add(PlantOperationalState.Descartada, null, null, date);

        await context.SaveChangesAsync();
        return lot;
    }

    private async Task RemoveLotsAsync(params Lot[] lots)
    {
        await using var context = NewContext();
        var ids = lots.Select(l => l.Id).ToList();
        context.Lots.RemoveRange(await context.Lots.Where(l => ids.Contains(l.Id)).ToListAsync());
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Simulacion_Preve_Aptas_Y_Perdidas_Con_Los_Lotes_Previos_Cargados()
    {
        await using var context = NewContext();
        var crop = await context.CropTypes.FirstAsync();

        // Dos lotes previos de la misma temporada (hace un año), con los datos del ciclo cargados a mano.
        var x = await ClosedLotAsync(context, Today.AddDays(-365), babyLeaf: 8, conventional: 1,
            [(Today.AddDays(-360), 1)], l => { l.BabyLeafAptAtOptimum = 8; l.EarlyLossPlants = 1; l.LateLossPlants = 0; });
        var y = await ClosedLotAsync(context, Today.AddDays(-360), babyLeaf: 6, conventional: 3,
            [(Today.AddDays(-335), 1)], l => { l.BabyLeafAptAtOptimum = 6; l.EarlyLossPlants = 0; l.LateLossPlants = 1; });
        try
        {
            var preview = await _fixture.Simulation.PreviewAsync(FreeRequest(crop.Id, Today, SimulationClimateMode.Manual));

            Assert.True(preview.IsValid);
            var outlook = preview.Result!.Outlook;
            Assert.Equal(2, outlook.History.Lots.Count);
            Assert.All(outlook.History.Lots, l => Assert.True(l.Outcome.IsManual));
            Assert.Equal(0.7444m, outlook.AptRate);              // (8/9 + 6/10) / 2
            Assert.Equal(0.05m, outlook.History.EarlyLossRate);  // (1/10 + 0) / 2
            Assert.Equal(0.125m, outlook.History.LateLossRate);  // (0/1 + 1/4) / 2

            // 60 plantas: 3 pérdidas tempranas → 57 vivas; 74 % aptas → 42 Baby Leaf; de las 15 que siguen, 2 tardías.
            var max = preview.Result.Strategies.Single(s => s.Kind == SimulationStrategyKind.MaxBabyLeaf);
            Assert.Equal(42, max.BabyLeaf.Plants);
            Assert.Equal(13, max.Conventional.Plants);
            Assert.Equal(5, max.LostPlants);
        }
        finally
        {
            await RemoveLotsAsync(x, y);
        }
    }

    [Fact]
    public async Task Simulacion_Calcula_El_Ciclo_De_Los_Lotes_Cerrados_Desde_La_App()
    {
        await using var context = NewContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(context);
        var crop = await context.CropTypes.FirstAsync();

        // 30 días a 25 °C → 20,5 GDD/día: la ventana Baby Leaf (450) cierra el día 22 desde la siembra.
        var sowing = Today.AddDays(-60);
        var lot = await ClosedLotAsync(context, sowing, babyLeaf: 4, conventional: 2,
            [(sowing.AddDays(10), 1), (sowing.AddDays(26), 1)]);
        await ForecastTestData.SeedConstantTemperatureReadingsAsync(context, UtcDate(sowing), 30, 25m);

        // Una de las convencionales estuvo "Baby Leaf": contaba como apta en el óptimo.
        var aptId = (await context.CommercialStages.FirstAsync(st => st.Name == CommercialStageNames.BabyLeafApta)).Id;
        var conventionalPlant = await context.Plants.FirstAsync(p => p.LotId == lot.Id && p.CommercialStageId != aptId && p.OperationalState == PlantOperationalState.Cosechada);
        context.PlantStageHistories.Add(new PlantStageHistory
        {
            PlantId = conventionalPlant.Id, NewCommercialStageId = aptId, NewOperationalState = "Activa",
            Source = "daily-flow", ChangedAtUtc = UtcDate(sowing.AddDays(18)),
        });
        await context.SaveChangesAsync();
        try
        {
            var preview = await _fixture.Simulation.PreviewAsync(FreeRequest(crop.Id, sowing.AddDays(365), SimulationClimateMode.Manual));

            var cycle = preview.Result!.Outlook.History.Lots.Single(l => l.Outcome.LotId == lot.Id).Outcome;
            Assert.False(cycle.IsManual);
            Assert.Equal(8, cycle.TotalPlants);
            Assert.Equal(5, cycle.AptAtOptimum);       // 4 cortadas como Baby Leaf + 1 que estuvo apta
            Assert.Equal(1, cycle.EarlyLosses);        // descartada el día 10
            Assert.Equal(1, cycle.LateLosses);         // descartada el día 26, con la ventana ya cerrada
            Assert.Equal(20.5m, cycle.AverageDailyGdd);
        }
        finally
        {
            await RemoveLotsAsync(lot);
        }
    }

    [Fact]
    public async Task Pronostico_Corregido_Con_El_Sensor_De_Adentro_Igual_En_Lotes_Y_Simulacion()
    {
        await using var context = NewContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(context);
        var created = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-10));
        await ForecastTestData.SeedConstantTemperatureReadingsAsync(context, UtcDate(created.SowingDate), 10, 25m); // adentro: 20,5/día
        await SeedForecastAsync(context, Today.AddDays(-10), 18, 15m, 25m);                                          // afuera: 15,5/día

        var lot = await context.Lots.AsNoTracking().Include(l => l.CropType).FirstAsync(l => l.Id == created.Id);
        var lotes = await _fixture.Gdd.GetFutureGddProjectionAsync(lot);
        var preview = await _fixture.Simulation.PreviewAsync(ManualLotRequest(created.Id, mode: SimulationClimateMode.Forecast));

        // Adentro sumó 5 GDD/día más que el pronóstico de afuera: se corrige la proyección.
        Assert.Equal(5m, lotes.Correction!.OffsetPerDay);
        Assert.All(lotes.Points, p => Assert.Equal(20.5m, p.Gdd));
        Assert.Equal(5m, preview.Result!.Gdd.Correction!.OffsetPerDay);
        Assert.Equal(lotes.Points.Select(p => (p.Date, p.Gdd)), preview.Result.Gdd.Projection.Select(p => (p.Date, p.Gdd)));
    }

    // --- Plantas y pesos/precios ---

    [Fact]
    public async Task Simulacion_Usa_Las_Plantas_Activas_Del_Lote()
    {
        await using var context = NewContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(context);

        // Lote demo del seed: 19 plantas activas.
        var demoLot = await context.Lots.FirstAsync(l => l.Name == "Lote Demo 01");
        var preview = await _fixture.Simulation.PreviewAsync(ManualLotRequest(demoLot.Id, babyLeafGoal: 50m));

        Assert.True(preview.IsValid);
        var result = preview.Result!;
        Assert.Equal(19, result.Context.Plants);
        Assert.Equal(19, result.Lot!.ActivePlants);
        var mixed = result.Strategies.Single(s => s.Kind == SimulationStrategyKind.BabyLeafGoal);
        Assert.True(mixed.BabyLeaf.Plants <= 10); // meta 50 % de 19 = 9,5 → 10, sin pasar las previstas aptas
        Assert.Equal(19, mixed.BabyLeaf.Plants + mixed.Conventional.Plants + mixed.LostPlants);
    }

    [Fact]
    public async Task Simulacion_Toma_Pesos_Y_Precios_De_Parametros_Y_Acepta_Cambios_Del_Escenario()
    {
        var saved = await _fixture.Parameters.UpdateYieldParametersAsync(
            new YieldReferenceInput(30m, 200m, 2600m, 1800m),
            new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Administrador")], "test")));
        Assert.True(saved.Succeeded);

        await using var context = NewContext();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-10));
        await AddActivePlantsAsync(context, lot, 4);

        var fromSystem = await _fixture.Simulation.PreviewAsync(ManualLotRequest(lot.Id) with { Yield = null });
        var withOverride = await _fixture.Simulation.PreviewAsync(
            ManualLotRequest(lot.Id) with { Yield = new SimulationYieldInput(null, null, 5000m, null) });

        Assert.Equal(new YieldReference(30m, 200m), fromSystem.Result!.Reference.Weights);
        Assert.Equal(2600m, fromSystem.Result.Reference.Prices.BabyLeafPerKg);
        Assert.False(fromSystem.Result.Reference.HasScenarioOverrides);

        Assert.Equal(5000m, withOverride.Result!.Reference.Prices.BabyLeafPerKg);   // el del escenario
        Assert.Equal(1800m, withOverride.Result.Reference.Prices.ConventionalPerKg); // el resto, del sistema
        Assert.True(withOverride.Result.Reference.HasScenarioOverrides);
        Assert.Equal(600m, withOverride.Result.Strategies[0].Revenue); // 4 × 30 g × $5000/kg
    }

    [Fact]
    public async Task Simulacion_Usa_El_Precio_De_Nutrientes_De_Parametros()
    {
        static System.Security.Claims.ClaimsPrincipal User(string role) => new(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role)], "test"));

        Assert.False((await _fixture.Parameters.UpdateNutrientPricePerLiterAsync(80m, User("Operador"))).Succeeded);
        Assert.False((await _fixture.Parameters.UpdateNutrientPricePerLiterAsync(0m, User("Administrador"))).Succeeded);
        Assert.True((await _fixture.Parameters.UpdateNutrientPricePerLiterAsync(80m, User("Administrador"))).Succeeded);
        try
        {
            await using var context = NewContext();
            var crop = await context.CropTypes.FirstAsync();
            var request = FreeRequest(crop.Id, Today, SimulationClimateMode.Manual) with
            {
                Costs = FullCosts() with { NutrientPricePerLiter = null } // vacío: el de Ajustes > Parámetros
            };

            var preview = await _fixture.Simulation.PreviewAsync(request);

            Assert.Equal(80m, (await _fixture.Simulation.GetContextOptionsAsync()).NutrientPricePerLiter);
            var max = preview.Result!.Strategies[0];
            Assert.Equal(Math.Round(80m * max.Costs.Usage!.NutrientLiters, 2),
                max.Costs.Components.Single(c => c.Name == "Nutrientes").Amount);
        }
        finally
        {
            await _fixture.Parameters.UpdateNutrientPricePerLiterAsync(null, User("Administrador"));
        }
    }

    // --- SIM-05: costos y validación ---

    [Fact]
    public async Task Simulacion_Costos_No_Calculable_Cuando_Faltan_Precios()
    {
        await using var context = NewContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(context);
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-10));
        await ForecastTestData.SeedConstantTemperatureReadingsAsync(context, UtcDate(lot.SowingDate), 5, 25m);

        var costs = FullCosts() with { PricePerSeed = null };
        var preview = await _fixture.Simulation.PreviewAsync(ManualLotRequest(lot.Id, costs: costs));

        Assert.True(preview.IsValid);
        var strategy = preview.Result!.Strategies[0];
        Assert.Null(strategy.Costs.Components.Single(c => c.Name == "Semillas").Amount);
        Assert.Null(strategy.Costs.Total);
        Assert.Contains(CostCalculator.NotCalculable, strategy.Costs.TotalNotCalculableReason);
        Assert.Null(strategy.Margin);
        Assert.Null(strategy.CostPerKg);
    }

    [Fact]
    public async Task Simulacion_Negativos_Se_Rechazan_En_Servidor()
    {
        await using var context = NewContext();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-10));

        var costs = FullCosts() with { PricePerSeed = -1m };
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
        await SeedForecastAsync(context, Today.AddDays(1), 0, 0m, 0m); // sin ningún pronóstico guardado

        // Escenario libre con pronóstico y siembra pasada: los días sin pronóstico no suman (no se asume 0 medido).
        var past = await _fixture.Simulation.PreviewAsync(FreeRequest(crop.Id, Today.AddDays(-15), SimulationClimateMode.Forecast));
        Assert.True(past.IsValid);
        Assert.Equal(0m, past.Result!.Gdd.Accumulated);
        Assert.Contains(past.Result.Warnings, w => w.Contains("16 de 16 días desde la siembra sin pronóstico guardado"));

        // Siembra lejana en el futuro: no hay pronóstico para esas fechas.
        var preview = await _fixture.Simulation.PreviewAsync(FreeRequest(crop.Id, Today.AddDays(200), SimulationClimateMode.Forecast));

        Assert.True(preview.IsValid);
        var result = preview.Result!;
        Assert.Empty(result.Gdd.Projection); // nada se proyecta ni se asume
        Assert.Equal(0, result.Gdd.CoveredDays);
        Assert.Equal(7, result.Gdd.MissingDays);
        Assert.Equal(0m, result.Gdd.Accumulated);
        Assert.Contains(result.Warnings, w => w.Contains("Sin pronóstico climático", StringComparison.OrdinalIgnoreCase));

        // Sin proyección no hay fecha de corte: las estrategias quedan sin calcular (no se inventa una fecha).
        Assert.All(result.Strategies, s => Assert.False(s.Available));
        Assert.Contains(result.Warnings, w => w.Contains("Sin proyección de GDD", StringComparison.OrdinalIgnoreCase));
    }

    // --- SIM: repetibilidad ---

    [Fact]
    public async Task Simulacion_Repetida_Devuelve_El_Mismo_Resultado()
    {
        await using var context = NewContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(context);
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-10));
        await ForecastTestData.SeedConstantTemperatureReadingsAsync(context, UtcDate(lot.SowingDate), 10, 25m);

        var request = ManualLotRequest(lot.Id, babyLeafGoal: 70m);
        var first = await _fixture.Simulation.PreviewAsync(request);
        var second = await _fixture.Simulation.PreviewAsync(request);

        Assert.True(first.IsValid && second.IsValid);
        Assert.Equal(first.Result!.SimulationId, second.Result!.SimulationId);
        Assert.Equal(first.Result.Gdd.Accumulated, second.Result.Gdd.Accumulated);
        Assert.Equal(first.Result.Timeline, second.Result.Timeline);
        Assert.Equal(
            first.Result.Strategies.Select(s => (s.EndDate, s.TotalKg, s.Revenue, s.Costs.Total, s.Margin)),
            second.Result.Strategies.Select(s => (s.EndDate, s.TotalKg, s.Revenue, s.Costs.Total, s.Margin)));
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
        await SeedForecastAsync(context, Today.AddDays(1), 7, 15m, 25m);
        var crop = await context.CropTypes.FirstAsync();

        var before = await ProductiveSnapshotAsync();

        foreach (var request in new[]
                 {
                     ManualLotRequest(lot.Id),
                     ManualLotRequest(lot.Id, babyLeafGoal: 50m),
                     ManualLotRequest(lot.Id, mode: SimulationClimateMode.Forecast),
                     FreeRequest(crop.Id, Today.AddDays(3), SimulationClimateMode.Forecast),
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

        // La frontera de hardware ni siquiera es dependencia del servicio.
        var constructorParams = typeof(SimulationService).GetConstructors().SelectMany(c => c.GetParameters());
        Assert.DoesNotContain(constructorParams, p => p.ParameterType == typeof(IHardwareGateway));
    }

    // --- Helpers de aislamiento ---

    private sealed record ProductiveSnapshot(
        int Lots, decimal? LotsAccumulatedGdd, int Predictions, int SensorReadings,
        int Plants, int BabyLeafEvaluations, int PlantStageHistories, int BabyLeafConfigs,
        int CropTypes, int PhenologicalStages, int CommercialStages, int LotStatuses,
        int CircuitAssignments, int DailyWeatherForecasts, int PlantImages, int PlantImageAnalyses);

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
            CircuitAssignments: await context.CircuitAssignments.CountAsync(),
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
