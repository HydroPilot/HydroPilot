using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Tests de integración sobre SQL Server local (SETUP.md). Cubren las reglas
/// nuevas con base y migraciones reales: unicidad de posición, agregados,
/// predominancia Mixto, flujo diario (sin tocar históricas), readiness híbrido
/// y ciclo de vida cosecha/descarte.
/// </summary>
[Collection("sql-server")]
public class LotesSqlServerTests
{
    private readonly LotesSqlFixture _fixture;

    public LotesSqlServerTests(LotesSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(int cropId, int greenhouseId)> GetBaseAsync(HydroPilotDbContext context)
    {
        var crop = await context.CropTypes.FirstAsync();
        var greenhouse = await context.Greenhouses.FirstAsync();
        return (crop.Id, greenhouse.Id);
    }

    private async Task<Lot> CreateLotAsync(HydroPilotDbContext context, int cropId, int greenhouseId,
        int rows = 3, int columns = 4, decimal? targetPercent = 70m)
    {
        // LotStatus tiene índice único por nombre: se reutiliza entre tests.
        var status = await context.LotStatuses.FirstOrDefaultAsync(s => s.Name == "ACTIVO");
        if (status is null)
        {
            status = new LotStatus { Name = "ACTIVO" };
            context.LotStatuses.Add(status);
            await context.SaveChangesAsync();
        }

        var lot = new Lot
        {
            GreenhouseId = greenhouseId,
            CropTypeId = cropId,
            StatusId = status.Id,
            Name = $"Lote Test {Guid.NewGuid():N}"[..20],
            SowingDate = LotesSqlFixture.SowingDate,
            PlantedAreaM2 = 2m,
            GridRows = rows,
            GridColumns = columns,
            CurrentPh = 6.0m,
            CurrentEc = 1.5m,
            BabyLeafHarvestTargetPercent = targetPercent
        };
        context.Lots.Add(lot);
        await context.SaveChangesAsync();
        return lot;
    }

    private static async Task<Plant> AddPlantAsync(HydroPilotDbContext context, Lot lot, int row, int col,
        string commercialStageName, PlantOperationalState op = PlantOperationalState.Activa,
        decimal? score = null, bool mandatoryMet = true)
    {
        var stage = await context.CommercialStages.FirstAsync(s => s.Name == commercialStageName);
        var pheno = await context.PhenologicalStages.OrderBy(s => s.Order).LastAsync();

        var plant = new Plant
        {
            LotId = lot.Id,
            Row = row,
            Column = col,
            PhenologicalStageId = pheno.Id,
            CommercialStageId = stage.Id,
            OperationalState = op,
            DiscardReason = op == PlantOperationalState.Descartada ? "test descarte" : null,
            DiscardDate = op == PlantOperationalState.Descartada ? DateOnly.FromDateTime(DateTime.UtcNow) : null,
            HarvestDate = op == PlantOperationalState.Cosechada ? DateOnly.FromDateTime(DateTime.UtcNow) : null
        };
        context.Plants.Add(plant);
        await context.SaveChangesAsync();

        if (score is not null)
        {
            var config = await context.BabyLeafConfigs.FirstAsync();
            context.BabyLeafEvaluations.Add(new BabyLeafEvaluation
            {
                PlantId = plant.Id,
                BabyLeafConfigId = config.Id,
                EvaluatedAtUtc = DateTime.UtcNow,
                BabyLeafScore = score.Value,
                Result = commercialStageName,
                Confidence = 0.9m,
                GddAtEvaluation = 320m,
                MandatoryCriteriaMet = mandatoryMet
            });
            await context.SaveChangesAsync();
        }

        return plant;
    }

    [Fact]
    public async Task Unique_Position_Constraint_Rejects_Duplicate_Plant()
    {
        await using var context = _fixture.NewContext();
        var (cropId, ghId) = await GetBaseAsync(context);
        var lot = await CreateLotAsync(context, cropId, ghId);

        await AddPlantAsync(context, lot, 1, 1, "En desarrollo");

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            var stage = await context.CommercialStages.FirstAsync(s => s.Name == "En desarrollo");
            context.Plants.Add(new Plant { LotId = lot.Id, Row = 1, Column = 1, CommercialStageId = stage.Id });
            await context.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task LotAggregate_Counts_Active_Empty_And_Special_Cells()
    {
        await using var context = _fixture.NewContext();
        var (cropId, ghId) = await GetBaseAsync(context);
        var lot = await CreateLotAsync(context, cropId, ghId, rows: 3, columns: 4);

        await AddPlantAsync(context, lot, 1, 1, "En desarrollo");
        await AddPlantAsync(context, lot, 1, 2, "Baby Leaf apta", score: 88m);
        await AddPlantAsync(context, lot, 2, 1, "Riesgo / fuera de ventana", PlantOperationalState.Descartada);
        await AddPlantAsync(context, lot, 3, 3, "Baby Leaf apta", PlantOperationalState.Cosechada, score: 90m);

        var aggregate = BuildAggregate(); // GddService real: el lote no tiene snapshot AccumulatedGdd

        var state = await aggregate.GetLotStateAsync(lot.Id);

        Assert.NotNull(state);
        Assert.Equal(3, state.GridRows);
        Assert.Equal(4, state.GridColumns);
        Assert.Equal(12, state.TotalConfiguredPositions);
        Assert.Equal(4, state.TotalPlants);
        Assert.Equal(2, state.ActivePlants);
        Assert.Equal(1, state.HarvestedPlants);
        Assert.Equal(1, state.DiscardedPlants);
        Assert.Equal(8, state.EmptyPositions);

        var cells = state.Cells;
        Assert.Equal(12, cells.Count);
        Assert.Equal("empty", cells.Single(c => c.Row == 2 && c.Column == 3).CellKind);
        Assert.Equal("discarded", cells.Single(c => c.Row == 2 && c.Column == 1).CellKind);

        // Conteos: solo activas.
        var byStage = state.CommercialCounts.ToDictionary(c => c.StageName);
        Assert.Equal(1, byStage["En desarrollo"].Count);
        Assert.Equal(1, byStage["Baby Leaf apta"].Count);
        Assert.False(byStage.ContainsKey("Riesgo / fuera de ventana")); // la descartada no cuenta
        // 1 vs 1 (En desarrollo vs Baby Leaf apta) → empate → Mixto.
        Assert.True(state.IsCommercialStageMixed);
        Assert.Equal(Services.Lotes.CommercialStageNames.Mixto, state.CommercialStageName);
    }

    [Fact]
    public async Task Predominancia_Mixto_Cuando_Hay_Empate()
    {
        await using var context = _fixture.NewContext();
        var (cropId, ghId) = await GetBaseAsync(context);
        var lot = await CreateLotAsync(context, cropId, ghId);

        await AddPlantAsync(context, lot, 1, 1, "En desarrollo");
        await AddPlantAsync(context, lot, 1, 2, "En desarrollo");
        await AddPlantAsync(context, lot, 2, 1, "Baby Leaf apta", score: 88m);
        await AddPlantAsync(context, lot, 2, 2, "Baby Leaf apta", score: 90m);
        // Descartada (no activa) no rompe el empate.
        await AddPlantAsync(context, lot, 3, 1, "Riesgo / fuera de ventana", PlantOperationalState.Descartada);

        var aggregate = BuildAggregate();
        var state = await aggregate.GetLotStateAsync(lot.Id);

        Assert.True(state.IsCommercialStageMixed);
        Assert.Equal(Services.Lotes.CommercialStageNames.Mixto, state.CommercialStageName);
    }

    [Fact]
    public async Task DailyFlow_Evalua_Activas_Por_Score_Y_Registra_Historial()
    {
        await using var context = _fixture.NewContext();
        var (cropId, ghId) = await GetBaseAsync(context);
        var lot = await CreateLotAsync(context, cropId, ghId);

        // GDD ~329 en ventana Baby Leaf (250-450).
        await LotesSqlFixture.SeedTemperatureReadingsAsync(
            context, ghId, LotesSqlFixture.SowingDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        var apta = await AddPlantAsync(context, lot, 1, 1, "En desarrollo", score: 90m, mandatoryMet: true);
        var candidata = await AddPlantAsync(context, lot, 1, 2, "En desarrollo", score: 70m, mandatoryMet: true);
        var enDesarrollo = await AddPlantAsync(context, lot, 2, 1, "En desarrollo", score: 40m, mandatoryMet: true);

        var flow = BuildFlow();
        var result = await flow.RunDailyAsync(lot.Id);

        Assert.NotNull(result);
        Assert.Equal(3, result.PlantsEvaluated);
        Assert.True(result.GddAccumulated >= 250m && result.GddAccumulated < 450m);

        await using var check = _fixture.NewContext();
        var byId = await check.Plants.ToDictionaryAsync(p => p.Id);
        Assert.Equal("Baby Leaf apta", (await check.CommercialStages.FindAsync(byId[apta.Id].CommercialStageId))!.Name);
        Assert.Equal("Candidata Baby Leaf", (await check.CommercialStages.FindAsync(byId[candidata.Id].CommercialStageId))!.Name);
        Assert.Equal("En desarrollo", (await check.CommercialStages.FindAsync(byId[enDesarrollo.Id].CommercialStageId))!.Name);

        // Historial registrado por el flujo diario.
        var history = await check.PlantStageHistories.CountAsync(h => h.Source == "daily-flow");
        Assert.True(history >= 2, $"Se esperaban transiciones, hubo {history}");

        // Snapshot de la predominancia persistido en el lote.
        var lotAfter = await check.Lots.Include(l => l.PredominantCommercialStage).FirstAsync(l => l.Id == lot.Id);
        // 1 apta / 1 candidata / 1 en desarrollo → triple empate → Mixto.
        Assert.True(lotAfter.IsCommercialStageMixed);
        Assert.Null(lotAfter.PredominantCommercialStage);

        // Readiness híbrido: 1 apta / 3 activas = 33,3% < 70% → no listo.
        Assert.False(result.HarvestReadiness!.IsReady);
    }

    [Fact]
    public async Task DailyFlow_No_Cambia_Plantas_Cosechadas_Ni_Descartadas()
    {
        await using var context = _fixture.NewContext();
        var (cropId, ghId) = await GetBaseAsync(context);
        var lot = await CreateLotAsync(context, cropId, ghId);

        await LotesSqlFixture.SeedTemperatureReadingsAsync(
            context, ghId, LotesSqlFixture.SowingDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        // Cosechada congelada como "Baby Leaf apta" pero con score que la haría
        // "En desarrollo" si estuviera activa: el flujo NO debe tocar su estado.
        var harvested = await AddPlantAsync(context, lot, 1, 1, "Baby Leaf apta",
            PlantOperationalState.Cosechada, score: 30m);
        var discarded = await AddPlantAsync(context, lot, 1, 2, "Riesgo / fuera de ventana",
            PlantOperationalState.Descartada, score: 90m);

        var flow = BuildFlow();
        var result = await flow.RunDailyAsync(lot.Id);

        Assert.NotNull(result);
        Assert.Equal(0, result.PlantsEvaluated); // ninguna activa

        await using var check = _fixture.NewContext();
        var h = await check.Plants.FirstAsync(p => p.Id == harvested.Id);
        var d = await check.Plants.FirstAsync(p => p.Id == discarded.Id);

        Assert.Equal(PlantOperationalState.Cosechada, h.OperationalState);
        Assert.Equal(PlantOperationalState.Descartada, d.OperationalState);

        var hStage = (await check.CommercialStages.FindAsync(h.CommercialStageId))!.Name;
        var dStage = (await check.CommercialStages.FindAsync(d.CommercialStageId))!.Name;
        Assert.Equal("Baby Leaf apta", hStage);
        Assert.Equal("Riesgo / fuera de ventana", dStage);

        Assert.Empty(await check.PlantStageHistories
            .Where(x => (x.PlantId == harvested.Id || x.PlantId == discarded.Id) && x.Source == "daily-flow")
            .ToListAsync());
    }

    [Fact]
    public async Task Readiness_Es_Pendiente_Sin_Porcentaje_Objetivo()
    {
        await using var context = _fixture.NewContext();
        var (cropId, ghId) = await GetBaseAsync(context);
        var lot = await CreateLotAsync(context, cropId, ghId, targetPercent: null);

        await AddPlantAsync(context, lot, 1, 1, "Baby Leaf apta", score: 90m, mandatoryMet: true);

        // Sin lecturas → GDD 0: igual se evalúa la regla con datos disponibles.
        var aggregate = BuildAggregate();
        var state = await aggregate.GetLotStateAsync(lot.Id);

        Assert.NotNull(state.HarvestReadiness);
        Assert.Null(state.HarvestReadiness.IsReady); // decisión pendiente
        Assert.True(state.HarvestReadiness.Warnings.Any(w => w.Contains("pendiente", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Harvest_Y_Discard_Congelan_Estado_Y_Registran_Historial()
    {
        await using var context = _fixture.NewContext();
        var (cropId, ghId) = await GetBaseAsync(context);
        var lot = await CreateLotAsync(context, cropId, ghId);

        var plant = await AddPlantAsync(context, lot, 1, 1, "Baby Leaf apta", score: 90m);

        var lifecycle = new Services.Lotes.PlantLifecycleService(new TestDbContextFactory(_fixture));
        var harvested = await lifecycle.HarvestPlantAsync(plant.Id);

        Assert.Equal(PlantOperationalState.Cosechada, harvested!.OperationalState);
        Assert.NotNull(harvested.HarvestDate);

        // Segunda operación sobre cosechada: no cambia (histórico congelado).
        var again = await lifecycle.HarvestPlantAsync(plant.Id);
        Assert.Equal(PlantOperationalState.Cosechada, again!.OperationalState);
        Assert.Single(await _fixture.NewContext().PlantStageHistories.Where(h => h.PlantId == plant.Id && h.Source == "harvest").ToListAsync());

        // Descartar una planta distinta.
        var plant2 = await AddPlantAsync(context, lot, 2, 1, "En desarrollo");
        var discarded = await lifecycle.DiscardPlantAsync(plant2.Id, "test bolting");
        Assert.Equal(PlantOperationalState.Descartada, discarded!.OperationalState);
        Assert.Equal("test bolting", discarded.DiscardReason);
        Assert.NotNull(discarded.DiscardDate);
    }

    private Services.Lotes.LotAggregateService BuildAggregate()
    {
        var factory = new TestDbContextFactory(_fixture);
        var settings = new SettingsService(factory);
        var weather = new WeatherService(
            new HttpClient(),
            factory,
            new ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WeatherService>.Instance);
        return new Services.Lotes.LotAggregateService(factory, new GddService(factory, weather, settings));
    }

    private Services.Lotes.LotDailyFlowService BuildFlow()
    {
        var factory = new TestDbContextFactory(_fixture);
        var settings = new SettingsService(factory);
        var weather = new WeatherService(
            new HttpClient(),
            factory,
            new ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WeatherService>.Instance);
        var gdd = new GddService(factory, weather, settings);
        var aggregate = new Services.Lotes.LotAggregateService(factory, gdd);
        return new Services.Lotes.LotDailyFlowService(
            factory,
            gdd,
            new PlantEvaluationService(factory),
            aggregate,
            new Services.Lotes.NoPlantRiskProvider());
    }

    private sealed class TestDbContextFactory(LotesSqlFixture fixture)
        : IDbContextFactory<HydroPilotDbContext>
    {
        public HydroPilotDbContext CreateDbContext() => fixture.NewContext();

        public Task<HydroPilotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(fixture.NewContext());
    }
}