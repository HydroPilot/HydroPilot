using System.Security.Claims;
using HydroPilotWeb.Controllers;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Dashboard;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HydroPilotWeb.Tests.Integration;

/// <summary>
/// Vista de situación del lote (LotOverviewService) sobre SQL Server: composición de
/// plantas, metas del ciclo, ciclos cerrados y edición de
/// objetivos restringida a Administrador/Productor. Cada prueba crea su propio lote.
/// </summary>
[Collection("forecast-sql")]
public class LotOverviewSqlServerTests
{
    private readonly ForecastSqlFixture _fixture;

    public LotOverviewSqlServerTests(ForecastSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private LotOverviewService Service() => new(
        _fixture.Factory,
        _fixture.Aggregate,
        _fixture.Forecast,
        _fixture.Gdd,
        Parameters(),
        NullLogger<LotOverviewService>.Instance);

    private SystemParametersService Parameters() => new(
        _fixture.Factory,
        new SettingsService(_fixture.Factory),
        NullLogger<SystemParametersService>.Instance);

    private static ClaimsPrincipal UserWithRole(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, $"test-{role}"), new Claim(ClaimTypes.Role, role)], "test"));

    /// <summary>Lote con 6 plantas: 2 aptas, 1 candidata, 1 en desarrollo, 1 cosechada apta y 1 descartada.</summary>
    private async Task<Lot> CreateLotWithPlantsAsync(decimal? babyLeafGoal = 50m, decimal? expectedActive = 80m)
    {
        await using var context = _fixture.Factory.CreateDbContext();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-10), rows: 2, columns: 5);
        lot.BabyLeafGoalPercent = babyLeafGoal;
        lot.ExpectedActivePercent = expectedActive;

        var stages = await context.CommercialStages.ToDictionaryAsync(s => s.Name, s => s.Id);
        void Add(int col, string stage, PlantOperationalState state) => context.Plants.Add(new Plant
        {
            LotId = lot.Id,
            Row = 1,
            Column = col,
            CommercialStageId = stages[stage],
            OperationalState = state,
            HarvestDate = state == PlantOperationalState.Cosechada ? DateOnly.FromDateTime(DateTime.UtcNow) : null,
            DiscardDate = state == PlantOperationalState.Descartada ? DateOnly.FromDateTime(DateTime.UtcNow) : null,
        });

        Add(1, CommercialStageNames.BabyLeafApta, PlantOperationalState.Activa);
        Add(2, CommercialStageNames.BabyLeafApta, PlantOperationalState.Activa);
        Add(3, CommercialStageNames.CandidataBabyLeaf, PlantOperationalState.Activa);
        Add(4, CommercialStageNames.EnDesarrollo, PlantOperationalState.Activa);
        Add(5, CommercialStageNames.BabyLeafApta, PlantOperationalState.Cosechada);
        context.Plants.Add(new Plant
        {
            LotId = lot.Id, Row = 2, Column = 1,
            CommercialStageId = stages[CommercialStageNames.RiesgoFueraDeVentana],
            OperationalState = PlantOperationalState.Descartada,
            DiscardDate = DateOnly.FromDateTime(DateTime.UtcNow),
            DiscardReason = "test",
        });

        await context.SaveChangesAsync();
        return lot;
    }

    [Fact]
    public async Task Overview_Compone_Plantas_Y_Metas_Del_Lote()
    {
        var lot = await CreateLotWithPlantsAsync();

        var overview = await Service().GetOverviewAsync(lot.Id);

        Assert.NotNull(overview);
        Assert.False(overview.IsClosed);
        Assert.Null(overview.ClosedCycle);

        var plants = overview.Plants;
        Assert.Equal(6, plants.TotalPlants);
        Assert.Equal(4, plants.Active);
        Assert.Equal(2, plants.Apta);
        Assert.Equal(1, plants.Candidate);
        Assert.Equal(1, plants.Developing);
        Assert.Equal(1, plants.HarvestedAsBabyLeaf);
        Assert.Equal(1, plants.Discarded);
        Assert.Equal(4, plants.EmptyPositions); // grilla 2x5 con 6 plantas

        // Meta Baby Leaf 50%: 1 cosechada + 2 aptas = 3 de 6. Activas 5 de 6 (83,3%) ≥ 80% esperado.
        Assert.Equal(50m, overview.Goals.AchievedPercent);
        Assert.Equal(83.3m, overview.Goals.ActivePercent);
        Assert.Equal(ActiveGoalStatus.Dentro, overview.Goals.ActiveStatus);

        // Sin lecturas de temperatura el lote está en desarrollo (GDD 0).
        Assert.NotNull(overview.Timeline);
        Assert.Equal(CyclePhase.Desarrollo, overview.Timeline.Phase);
    }

    [Fact]
    public async Task Productor_Guarda_Objetivos_Redondeados()
    {
        var lot = await CreateLotWithPlantsAsync(babyLeafGoal: null, expectedActive: null);

        var result = await Service().UpdateObjectivesAsync(
            lot.Id, new LotObjectives(70.126m, 80m, 90m), UserWithRole("Productor"));

        Assert.True(result.Succeeded);
        await using var context = _fixture.Factory.CreateDbContext();
        var saved = await context.Lots.AsNoTracking().FirstAsync(l => l.Id == lot.Id);
        Assert.Equal(70.13m, saved.BabyLeafHarvestTargetPercent);
        Assert.Equal(80m, saved.BabyLeafGoalPercent);
        Assert.Equal(90m, saved.ExpectedActivePercent);
    }

    [Fact]
    public async Task Operador_No_Puede_Editar_Objetivos()
    {
        var lot = await CreateLotWithPlantsAsync(babyLeafGoal: 50m, expectedActive: 80m);

        var result = await Service().UpdateObjectivesAsync(
            lot.Id, new LotObjectives(90m, 90m, 99m), UserWithRole("Operador"));

        Assert.False(result.Succeeded);
        Assert.Single(result.Errors);
        await using var context = _fixture.Factory.CreateDbContext();
        var saved = await context.Lots.AsNoTracking().FirstAsync(l => l.Id == lot.Id);
        Assert.Equal(50m, saved.BabyLeafGoalPercent);
        Assert.Equal(80m, saved.ExpectedActivePercent);
    }

    [Fact]
    public async Task Objetivos_Fuera_De_Rango_No_Se_Guardan()
    {
        var lot = await CreateLotWithPlantsAsync(babyLeafGoal: 50m, expectedActive: 80m);

        var result = await Service().UpdateObjectivesAsync(
            lot.Id, new LotObjectives(70m, 150m, 80m), UserWithRole("Administrador"));

        Assert.False(result.Succeeded);
        await using var context = _fixture.Factory.CreateDbContext();
        var saved = await context.Lots.AsNoTracking().FirstAsync(l => l.Id == lot.Id);
        Assert.Equal(50m, saved.BabyLeafGoalPercent);
    }

    [Fact]
    public async Task Lote_Cerrado_Muestra_La_Linea_De_Tiempo_Hasta_La_Cosecha()
    {
        var lot = await CreateLotWithPlantsAsync();
        var harvestDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2);
        await using (var context = _fixture.Factory.CreateDbContext())
        {
            await ForecastTestData.CloseLotAsync(context, lot.Id, yieldKg: 12.6m, harvestDate: harvestDate);
        }

        var overview = await Service().GetOverviewAsync(lot.Id);

        Assert.NotNull(overview);
        Assert.True(overview.IsClosed);
        Assert.NotNull(overview.ClosedCycle);
        Assert.Equal(8, overview.ClosedCycle.CycleDays);

        // Ventanas y curva hasta el día de la cosecha, sin proyección.
        Assert.NotNull(overview.Timeline);
        Assert.Equal(harvestDate, overview.Timeline.AsOfDate);
        Assert.NotNull(overview.Curve);
        Assert.Equal(harvestDate, overview.Curve.EndDate);
        Assert.Empty(overview.Curve.Projected);

        // Cerrado sin guardar la referencia: no hay pesos ni precios, así que no se inventan kg ni $.
        Assert.NotNull(overview.HarvestYield);
        Assert.Equal(new DestinationYield(1, null, null), overview.HarvestYield.Real.BabyLeaf);
    }

    /// <summary>Pesos 30 g / 200 g en el sistema y precios propios del lote (3000 / 2000 $/kg).</summary>
    private async Task<SystemParametersService> SetHarvestReferenceAsync(Lot lot)
    {
        var parameters = Parameters();
        Assert.True((await parameters.UpdateYieldParametersAsync(
            new YieldReferenceInput(30m, 200m, null, null), UserWithRole("Productor"))).Succeeded);
        await using var context = _fixture.Factory.CreateDbContext();
        foreach (var (destination, price) in new[] { ("baby_leaf", 3000m), ("conventional", 2000m) })
        {
            context.CostPriceCatalogs.Add(new HydroPilotWeb.Models.Optimization.CostPriceCatalog
            {
                LotId = lot.Id,
                Destination = destination,
                Item = "price_per_kg",
                Value = price,
                Currency = "ARS",
                ValidFrom = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-6),
                Source = "manual",
            });
        }
        await context.SaveChangesAsync();
        return parameters;
    }

    private async Task DeleteLotPricesAsync(Lot lot)
    {
        // Los precios por lote bloquean el borrado de lotes que hacen otros tests de la colección.
        await using var context = _fixture.Factory.CreateDbContext();
        await context.CostPriceCatalogs.Where(c => c.LotId == lot.Id).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Sin_Plantas_Activas_El_Lote_Se_Cierra_Solo_Y_Guarda_Pesos_Y_Precios()
    {
        var lot = await CreateLotWithPlantsAsync();
        var parameters = await SetHarvestReferenceAsync(lot);
        try
        {
            // Cosecha de las activas elegidas: 2 Baby Leaf + la candidata como Baby Leaf; la en desarrollo como adulta.
            var lifecycle = new PlantLifecycleService(_fixture.Factory, parameters);
            var result = await lifecycle.HarvestPlantsAsync(lot.Id, await ActivePlantIdsAsync(lot));
            Assert.Equal(new PlantBatchResult(3, 1, 0, LotStatusNames.Cosechado), result);

            // Los parámetros cambian después de la cosecha: el ciclo cerrado sigue con los suyos.
            Assert.True((await parameters.UpdateYieldParametersAsync(
                new YieldReferenceInput(99m, 999m, 1m, 1m), UserWithRole("Productor"))).Succeeded);

            var overview = await Service().GetOverviewAsync(lot.Id);

            Assert.NotNull(overview?.HarvestYield);
            Assert.True(overview.IsClosed);
            Assert.Equal(LotStatusNames.Cosechado, overview.State.StatusName);
            Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), overview.ClosedCycle?.HarvestDate);
            var real = overview.HarvestYield.Real;
            Assert.Equal(new DestinationYield(4, 0.12m, 360m), real.BabyLeaf);    // 1 previa + 2 Baby Leaf + 1 candidata × 30 g × $3000/kg
            Assert.Equal(new DestinationYield(1, 0.2m, 400m), real.Conventional);  // 1 × 200 g × $2000/kg
            Assert.Equal(760m, overview.HarvestYield.TotalRevenue);
        }
        finally
        {
            await DeleteLotPricesAsync(lot);
        }
    }

    [Fact]
    public async Task Cierre_Por_Api_Tambien_Guarda_Pesos_Y_Precios()
    {
        var lot = await CreateLotWithPlantsAsync();
        var parameters = await SetHarvestReferenceAsync(lot);
        var harvestDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        try
        {
            var controller = new ForecastingController(
                _fixture.Factory, _fixture.Forecast, parameters, NullLogger<ForecastingController>.Instance);
            await controller.RecordHarvest(lot.Id, new RecordHarvestRequest(1.5m, harvestDate));

            await using var check = _fixture.Factory.CreateDbContext();
            var saved = await check.Lots.AsNoTracking().FirstAsync(l => l.Id == lot.Id);
            Assert.Equal(harvestDate, saved.ActualHarvestDate);
            Assert.Equal(30m, saved.HarvestBabyLeafGramsPerPlant);
            Assert.Equal(200m, saved.HarvestConventionalGramsPerPlant);
            Assert.Equal(3000m, saved.HarvestBabyLeafPricePerKg);
            Assert.Equal(2000m, saved.HarvestConventionalPricePerKg);
            Assert.Equal("ARS", saved.HarvestPriceCurrency);
        }
        finally
        {
            await DeleteLotPricesAsync(lot);
        }
    }

    [Fact]
    public async Task Si_Se_Descartan_Todas_El_Lote_Queda_Descartado()
    {
        await using var context = _fixture.Factory.CreateDbContext();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-5), rows: 1, columns: 2);
        var plants = new[] { new Plant { LotId = lot.Id, Row = 1, Column = 1 }, new Plant { LotId = lot.Id, Row = 1, Column = 2 } };
        context.Plants.AddRange(plants);
        await context.SaveChangesAsync();

        var lifecycle = new PlantLifecycleService(_fixture.Factory, Parameters());
        await lifecycle.DiscardPlantAsync(plants[0].Id, "test");
        await using (var middle = _fixture.Factory.CreateDbContext())
        {
            // Todavía queda una activa: el lote sigue en curso.
            Assert.Equal(LotStatusNames.Activo, (await middle.Lots.Include(l => l.Status).FirstAsync(l => l.Id == lot.Id)).Status!.Name);
        }
        await lifecycle.DiscardPlantAsync(plants[1].Id, "test");

        await using var check = _fixture.Factory.CreateDbContext();
        var closed = await check.Lots.Include(l => l.Status).FirstAsync(l => l.Id == lot.Id);
        Assert.Equal(LotStatusNames.Descartado, closed.Status!.Name);
        Assert.Null(closed.ActualHarvestDate);
        Assert.Null(closed.HarvestBabyLeafPricePerKg);
    }

    private async Task<List<int>> ActivePlantIdsAsync(Lot lot)
    {
        await using var context = _fixture.Factory.CreateDbContext();
        return await context.Plants.Where(p => p.LotId == lot.Id && p.OperationalState == PlantOperationalState.Activa)
            .Select(p => p.Id).ToListAsync();
    }

    [Fact]
    public async Task Cosecha_De_Seleccion_Usa_El_Estado_De_Cada_Planta()
    {
        var lot = await CreateLotWithPlantsAsync();
        int riskId, foreignId;
        await using (var context = _fixture.Factory.CreateDbContext())
        {
            var risk = await context.CommercialStages.FirstAsync(s => s.Name == CommercialStageNames.RiesgoFueraDeVentana);
            var riskPlant = new Plant { LotId = lot.Id, Row = 2, Column = 2, CommercialStageId = risk.Id };
            context.Plants.Add(riskPlant);
            await context.SaveChangesAsync();
            riskId = riskPlant.Id;
            foreignId = await context.Plants.Where(p => p.LotId != lot.Id).Select(p => p.Id).FirstAsync();
        }

        // Activas: 2 Baby Leaf, 1 candidata, 1 en desarrollo y 1 en riesgo. Se eligen todas menos la candidata
        // (más una de otro lote, que se ignora).
        var ids = await ActivePlantIdsAsync(lot);
        await using (var context = _fixture.Factory.CreateDbContext())
        {
            var candidate = await context.Plants.FirstAsync(p => p.LotId == lot.Id && p.CommercialStage!.Name == CommercialStageNames.CandidataBabyLeaf);
            ids.Remove(candidate.Id);
        }
        ids.Add(foreignId);

        var result = await new PlantLifecycleService(_fixture.Factory, Parameters()).HarvestPlantsAsync(lot.Id, ids);

        Assert.Equal(new PlantBatchResult(2, 2, 0, null), result);   // 2 Baby Leaf; en desarrollo y riesgo como adultas
        await using var check = _fixture.Factory.CreateDbContext();
        var plants = await check.Plants.Include(p => p.CommercialStage).Where(p => p.LotId == lot.Id).ToListAsync();
        Assert.Equal(CommercialStageNames.CosechaConvencional, plants.Single(p => p.Id == riskId).CommercialStage!.Name);
        Assert.Equal(CommercialStageNames.CandidataBabyLeaf, Assert.Single(plants, p => p.OperationalState == PlantOperationalState.Activa).CommercialStage!.Name);
        Assert.Equal(4, await check.PlantStageHistories.CountAsync(h => h.Plant!.LotId == lot.Id && h.Source == "harvest-bulk"));
    }

    [Fact]
    public async Task Descarte_De_Seleccion_Con_El_Mismo_Motivo_Y_Cierra_El_Lote_Si_No_Quedan()
    {
        var lot = await CreateLotWithPlantsAsync();
        var lifecycle = new PlantLifecycleService(_fixture.Factory, Parameters());

        var result = await lifecycle.DiscardPlantsAsync(lot.Id, await ActivePlantIdsAsync(lot), "granizo");

        // Quedaban 4 activas y ya había una cosechada: el lote se cierra como cosechado.
        Assert.Equal(new PlantBatchResult(0, 0, 4, LotStatusNames.Cosechado), result);
        await using var check = _fixture.Factory.CreateDbContext();
        var discarded = await check.Plants.Where(p => p.LotId == lot.Id && p.OperationalState == PlantOperationalState.Descartada).ToListAsync();
        Assert.Equal(5, discarded.Count);                                    // 4 de ahora + 1 que ya estaba
        Assert.Equal(4, discarded.Count(p => p.DiscardReason == "granizo"));

        // Volver a pedirlo no hace nada: ya no hay activas.
        Assert.Equal(new PlantBatchResult(0, 0, 0, null), await lifecycle.DiscardPlantsAsync(lot.Id, discarded.Select(p => p.Id).ToList(), "otra"));
    }

    [Theory]
    [InlineData(CommercialStageNames.BabyLeafApta, HarvestDestination.BabyLeaf)]
    [InlineData(CommercialStageNames.CandidataBabyLeaf, HarvestDestination.BabyLeaf)]
    [InlineData(CommercialStageNames.EnDesarrollo, HarvestDestination.Convencional)]
    [InlineData(CommercialStageNames.CosechaConvencional, HarvestDestination.Convencional)]
    [InlineData(CommercialStageNames.RiesgoFueraDeVentana, HarvestDestination.Convencional)]
    [InlineData(null, HarvestDestination.Convencional)]
    public void Destino_De_Cosecha_Por_Estado(string? stage, HarvestDestination expected)
    {
        Assert.Equal(expected, PlantHarvestRules.DestinationFor(stage));
    }

    [Fact]
    public async Task Rendimiento_Por_Destino_Usa_Pesos_Del_Sistema_Y_Precio_Del_Lote()
    {
        var lot = await CreateLotWithPlantsAsync();
        var service = Service();
        var saved = await Parameters().UpdateYieldParametersAsync(
            new YieldReferenceInput(40m, 250m, 2500m, 1500m), UserWithRole("Productor"));
        Assert.True(saved.Succeeded);

        // Precio propio del lote: le gana al del cultivo y al global.
        await using (var context = _fixture.Factory.CreateDbContext())
        {
            foreach (var (destination, price) in new[] { ("baby_leaf", 3000m), ("conventional", 2000m) })
            {
                context.CostPriceCatalogs.Add(new HydroPilotWeb.Models.Optimization.CostPriceCatalog
                {
                    LotId = lot.Id,
                    Destination = destination,
                    Item = "price_per_kg",
                    Value = price,
                    Currency = "ARS",
                    ValidFrom = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1),
                    Source = "fixture",
                });
            }
            await context.SaveChangesAsync();
        }

        try
        {
            var overview = await service.GetOverviewAsync(lot.Id);

            // Activas: 2 aptas, 1 candidata, 1 en desarrollo, 0 convencionales; 1 cosechada como Baby Leaf.
            Assert.NotNull(overview);
            var yield = overview.Yield;
            Assert.Equal(new YieldReference(40m, 250m), yield.Reference);
            Assert.Equal(new DestinationYield(3, 0.12m, 360m), yield.Current.BabyLeaf);       // (1 cosechada + 2 aptas) × 40 g × $3000/kg
            Assert.Equal(new DestinationYield(0, 0m, 0m), yield.Current.Conventional);
            // Esperado: lo arma la regla con la situación del lote (y el histórico del invernadero, si hay).
            var expected = LotYieldRules.Estimate(
                new LotYieldRules.Input(Apta: 2, Candidate: 1, Developing: 1, Conventional: 0, Risk: 0, HarvestedBabyLeaf: 1,
                    AdultRoom: yield.Basis.AdultRoom),
                yield.Reference, yield.Prices, yield.Basis.History, yield.Basis.LotWeight);
            Assert.Equal(expected.Expected, yield.Expected);
        }
        finally
        {
            // Los precios por lote bloquean el borrado de lotes que hacen otros tests de la colección.
            await using var context = _fixture.Factory.CreateDbContext();
            await context.CostPriceCatalogs.Where(c => c.LotId == lot.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Operador_No_Puede_Editar_Pesos_Ni_Precios()
    {
        var parameters = Parameters();
        var before = await parameters.GetYieldParametersAsync();

        var result = await parameters.UpdateYieldParametersAsync(
            new YieldReferenceInput(1m, 1m, 1m, 1m), UserWithRole("Operador"));

        Assert.False(result.Succeeded);
        Assert.Equal(before, await parameters.GetYieldParametersAsync());
    }

    private async Task<int> AddCropDemoPriceAsync(int cropTypeId, string destination, decimal value)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using var context = _fixture.Factory.CreateDbContext();
        var demo = new HydroPilotWeb.Models.Optimization.CostPriceCatalog
        {
            CropTypeId = cropTypeId,
            Destination = destination,
            Item = "price_per_kg",
            Value = value,
            Currency = "ARS",
            ValidFrom = today.AddDays(-10),
            ValidUntil = today.AddDays(100),
            Source = "demo",
        };
        context.CostPriceCatalogs.Add(demo);
        await context.SaveChangesAsync();
        return demo.Id;
    }

    [Fact]
    public async Task Precio_Global_Cierra_El_Demo_Y_Se_Corrige_En_El_Dia()
    {
        var lot = await CreateLotWithPlantsAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var demoId = await AddCropDemoPriceAsync(lot.CropTypeId, "baby_leaf", 2600m);

        var parameters = Parameters();
        var admin = UserWithRole("Administrador");
        Assert.True((await parameters.UpdateYieldParametersAsync(new YieldReferenceInput(30m, 200m, 2900m, 1900m), admin)).Succeeded);
        Assert.True((await parameters.UpdateYieldParametersAsync(new YieldReferenceInput(30m, 200m, 3100m, 1900m), admin)).Succeeded);

        // El precio global vale para el lote (el demo del cultivo quedó cerrado).
        var overview = await Service().GetOverviewAsync(lot.Id);
        Assert.Equal(3100m, overview!.Yield.Prices.BabyLeafPerKg);
        Assert.Equal(1900m, overview.Yield.Prices.ConventionalPerKg);
        Assert.Equal(3100m, (await parameters.GetYieldParametersAsync()).BabyLeafPricePerKg);

        await using var context = _fixture.Factory.CreateDbContext();
        var demo = await context.CostPriceCatalogs.AsNoTracking().FirstAsync(c => c.Id == demoId);
        Assert.Equal(today.AddDays(-1), demo.ValidUntil);
        Assert.Equal(1, await context.CostPriceCatalogs.CountAsync(c =>
            c.CropTypeId == null && c.LotId == null && c.Destination == "baby_leaf"
            && c.Item == "price_per_kg" && c.Source == "manual" && c.ValidFrom == today));
    }

    [Fact]
    public async Task Vaciar_Precio_Lo_Deja_Sin_Definir_Sin_Tocar_El_Demo()
    {
        var lot = await CreateLotWithPlantsAsync();
        var parameters = Parameters();
        var admin = UserWithRole("Administrador");
        Assert.True((await parameters.UpdateYieldParametersAsync(new YieldReferenceInput(30m, 200m, 3000m, 2000m), admin)).Succeeded);
        var demoId = await AddCropDemoPriceAsync(lot.CropTypeId, "conventional", 1800m);

        Assert.True((await parameters.UpdateYieldParametersAsync(new YieldReferenceInput(30m, 200m, 3000m, null), admin)).Succeeded);

        // Sin precio cargado queda sin definir: el demo no cuenta para Lotes y se deja como estaba.
        Assert.Null((await parameters.GetYieldParametersAsync()).ConventionalPricePerKg);
        var overview = await Service().GetOverviewAsync(lot.Id);
        Assert.Null(overview!.Yield.Prices.ConventionalPerKg);
        Assert.Null(overview.Yield.Expected.Conventional.Revenue);

        await using var context = _fixture.Factory.CreateDbContext();
        var demo = await context.CostPriceCatalogs.AsNoTracking().FirstAsync(c => c.Id == demoId);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(100), demo.ValidUntil);
    }
}
