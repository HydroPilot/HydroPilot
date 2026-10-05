using System.Security.Claims;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Dashboard;
using HydroPilotWeb.Services.Lotes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HydroPilotWeb.Tests.Integration;

/// <summary>
/// Vista de situación del lote (LotOverviewService) sobre SQL Server: composición de
/// plantas, metas del ciclo, solución nutritiva, ciclos cerrados y edición de
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
        Parameters(),
        Options.Create(new DashboardOptions()),
        NullLogger<LotOverviewService>.Instance);

    private SystemParametersService Parameters() => new(
        _fixture.Factory,
        new SettingsService(_fixture.Factory),
        NullLogger<SystemParametersService>.Instance);

    private static ClaimsPrincipal UserWithRole(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, $"test-{role}"), new Claim(ClaimTypes.Role, role)], "test"));

    /// <summary>Lote con 6 plantas: 2 aptas, 1 candidata, 1 en desarrollo, 1 cosechada apta y 1 descartada.</summary>
    private async Task<Lot> CreateLotWithPlantsAsync(decimal? babyLeafGoal = 50m, decimal? maxLoss = 20m)
    {
        await using var context = _fixture.Factory.CreateDbContext();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-10), rows: 2, columns: 5);
        lot.BabyLeafGoalPercent = babyLeafGoal;
        lot.MaxLossPercent = maxLoss;
        lot.CurrentPh = 7.2m; // fuera de la banda del cultivo (5,5-6,5), sin lecturas de sensor

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
    public async Task Overview_Compone_Plantas_Metas_Y_Solucion_Del_Lote()
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

        // Meta Baby Leaf 50%: 1 cosechada + 2 aptas = 3 de 6 → cumplida. Pérdidas 1 de 6 (16,7%) ≤ 20%.
        Assert.Equal(BabyLeafGoalStatus.Cumplida, overview.Goals.BabyLeafStatus);
        Assert.Equal(50m, overview.Goals.AchievedPercent);
        Assert.Equal(LossGoalStatus.Dentro, overview.Goals.LossStatus);

        // Sin lecturas de sensor: el pH es el valor manual del lote, contra la banda del cultivo.
        var ph = Assert.Single(overview.Solution, s => s.SensorType == AnomalyContract.SensorTypePh);
        Assert.True(ph.IsManualValue);
        Assert.Equal(7.2m, ph.Value);
        Assert.False(ph.InBand);

        // Sin lecturas de temperatura el lote está en desarrollo (GDD 0).
        Assert.NotNull(overview.Timeline);
        Assert.Equal(CyclePhase.Desarrollo, overview.Timeline.Phase);
    }

    [Fact]
    public async Task Productor_Guarda_Objetivos_Redondeados()
    {
        var lot = await CreateLotWithPlantsAsync(babyLeafGoal: null, maxLoss: null);

        var result = await Service().UpdateObjectivesAsync(
            lot.Id, new LotObjectives(70.126m, 80m, 10m), UserWithRole("Productor"));

        Assert.True(result.Succeeded);
        await using var context = _fixture.Factory.CreateDbContext();
        var saved = await context.Lots.AsNoTracking().FirstAsync(l => l.Id == lot.Id);
        Assert.Equal(70.13m, saved.BabyLeafHarvestTargetPercent);
        Assert.Equal(80m, saved.BabyLeafGoalPercent);
        Assert.Equal(10m, saved.MaxLossPercent);
    }

    [Fact]
    public async Task Operador_No_Puede_Editar_Objetivos()
    {
        var lot = await CreateLotWithPlantsAsync(babyLeafGoal: 50m, maxLoss: 20m);

        var result = await Service().UpdateObjectivesAsync(
            lot.Id, new LotObjectives(90m, 90m, 1m), UserWithRole("Operador"));

        Assert.False(result.Succeeded);
        Assert.Single(result.Errors);
        await using var context = _fixture.Factory.CreateDbContext();
        var saved = await context.Lots.AsNoTracking().FirstAsync(l => l.Id == lot.Id);
        Assert.Equal(50m, saved.BabyLeafGoalPercent);
        Assert.Equal(20m, saved.MaxLossPercent);
    }

    [Fact]
    public async Task Objetivos_Fuera_De_Rango_No_Se_Guardan()
    {
        var lot = await CreateLotWithPlantsAsync(babyLeafGoal: 50m, maxLoss: 20m);

        var result = await Service().UpdateObjectivesAsync(
            lot.Id, new LotObjectives(70m, 150m, 20m), UserWithRole("Administrador"));

        Assert.False(result.Succeeded);
        await using var context = _fixture.Factory.CreateDbContext();
        var saved = await context.Lots.AsNoTracking().FirstAsync(l => l.Id == lot.Id);
        Assert.Equal(50m, saved.BabyLeafGoalPercent);
    }

    [Fact]
    public async Task Lote_Cerrado_Muestra_El_Resultado_Del_Ciclo_Sin_Linea_De_Tiempo()
    {
        var lot = await CreateLotWithPlantsAsync();
        await using (var context = _fixture.Factory.CreateDbContext())
        {
            await ForecastTestData.CloseLotAsync(context, lot.Id, yieldKg: 12.6m, harvestDate: lot.SowingDate.AddDays(26));
        }

        var overview = await Service().GetOverviewAsync(lot.Id);

        Assert.NotNull(overview);
        Assert.True(overview.IsClosed);
        Assert.Null(overview.Timeline);
        Assert.NotNull(overview.ClosedCycle);
        Assert.Equal(26, overview.ClosedCycle.CycleDays);
        Assert.Equal(12.6m, overview.ClosedCycle.ActualYieldKg);
        Assert.Equal(2.8m, overview.ClosedCycle.YieldPerM2); // 12,6 kg / 4,5 m²
    }

    [Fact]
    public async Task Rendimiento_Por_Destino_Usa_Pesos_Del_Sistema_Y_Precio_Del_Lote()
    {
        var lot = await CreateLotWithPlantsAsync();
        var service = Service();
        var saved = await Parameters().UpdateYieldParametersAsync(
            new YieldReferenceInput(40m, 250m, 2500m, 1500m), UserWithRole("Productor"));
        Assert.True(saved.Succeeded);

        // Precio propio del lote: le gana al del cultivo y al global (precedencia de Optimización).
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

            // Activas: 2 aptas, 1 candidata, 1 en desarrollo, 0 convencionales.
            Assert.NotNull(overview);
            var yield = overview.Yield;
            Assert.Equal(new YieldReference(40m, 250m), yield.Reference);
            Assert.Equal(new DestinationYield(2, 0.08m, 240m), yield.Current.BabyLeaf);       // 2 × 40 g × $3000/kg
            Assert.Equal(new DestinationYield(0, 0m, 0m), yield.Current.Conventional);
            Assert.Equal(new DestinationYield(3, 0.12m, 360m), yield.Potential.BabyLeaf);     // + 1 candidata
            Assert.Equal(new DestinationYield(1, 0.25m, 500m), yield.Potential.Conventional); // 1 en desarrollo × 250 g × $2000/kg
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

        // El precio global vale para el lote (y para Optimización: el demo del cultivo quedó cerrado).
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

        // Sin precio cargado queda sin definir: el demo no cuenta para Lotes, pero sigue vigente para Optimización.
        Assert.Null((await parameters.GetYieldParametersAsync()).ConventionalPricePerKg);
        var overview = await Service().GetOverviewAsync(lot.Id);
        Assert.Null(overview!.Yield.Prices.ConventionalPerKg);
        Assert.Null(overview.Yield.Potential.Conventional.Revenue);

        await using var context = _fixture.Factory.CreateDbContext();
        var demo = await context.CostPriceCatalogs.AsNoTracking().FirstAsync(c => c.Id == demoId);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(100), demo.ValidUntil);
    }
}
