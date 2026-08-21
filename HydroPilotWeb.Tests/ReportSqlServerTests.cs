using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Reports;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Tests de integración del módulo Reports sobre SQL Server local (SETUP.md),
/// usando la misma colección serializada y fixture que LotesSqlServerTests.
///
/// Cubren: exclusión de inválidas de estadísticas (política compartida), conteos
/// por calidad, truncado de rango, paginación, ciclo de cultivo (misma métrica de
/// forecasting), forecast vs cosecha, predominancia Mixto consumida del dominio,
/// reporte de plantas con última evaluación y reporte de cambios (pendiente sin
/// historial; nombres resueltos con historial).
/// </summary>
[Collection("sql-server")]
public class ReportSqlServerTests
{
    private readonly LotesSqlFixture _fixture;

    public ReportSqlServerTests(LotesSqlFixture fixture)
    {
        _fixture = fixture;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private ReportQueryService BuildService()
    {
        var factory = new TestDbContextFactory(_fixture);
        var settings = new SettingsService(factory);
        var weather = new WeatherService(
            new HttpClient(),
            factory,
            new ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WeatherService>.Instance);
        var gdd = new GddService(factory, weather, settings);
        var yield = new YieldService(factory);
        var aggregate = new Services.Lotes.LotAggregateService(factory, gdd);
        return new ReportQueryService(factory, gdd, yield, aggregate);
    }

    private async Task<(int CropId, int GreenhouseId)> GetBaseAsync(HydroPilotDbContext context)
    {
        var crop = await context.CropTypes.FirstAsync();
        var greenhouse = await context.Greenhouses.FirstAsync();
        return (crop.Id, greenhouse.Id);
    }

    private async Task<Lot> CreateLotAsync(HydroPilotDbContext context, int cropId, int greenhouseId,
        int rows = 3, int columns = 4, decimal? accumulatedGdd = null)
    {
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
            Name = $"Lote Rep {Guid.NewGuid():N}"[..24],
            SowingDate = LotesSqlFixture.SowingDate,
            PlantedAreaM2 = 4m,
            GridRows = rows,
            GridColumns = columns,
            AccumulatedGdd = accumulatedGdd,
            BabyLeafHarvestTargetPercent = 70m
        };
        context.Lots.Add(lot);
        await context.SaveChangesAsync();
        return lot;
    }

    /// <summary>
    /// Inserta una lectura con el contrato de lectura v2 (NodeId obligatorio,
    /// ExternalReadingId único por nodo, timestamps UTC).
    /// </summary>
    private static async Task AddReadingAsync(HydroPilotDbContext context, int sensorId, int nodeId,
        DateTime observedAtUtc, decimal value, string quality, string seedTag, int index)
    {
        context.SensorReadings.Add(new SensorReading
        {
            SensorId = sensorId,
            NodeId = nodeId,
            Value = value,
            Timestamp = observedAtUtc,
            ObservedAtUtc = observedAtUtc,
            ReceivedAtUtc = DateTime.UtcNow,
            ExternalReadingId = $"{seedTag}-{index}",
            Quality = quality,
            IngestionResult = TelemetryContract.ResultAceptada,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private static async Task<Plant> AddPlantAsync(HydroPilotDbContext context, Lot lot, int row, int col,
        string commercialStageName, PlantOperationalState op = PlantOperationalState.Activa, decimal? score = null)
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
                MandatoryCriteriaMet = true
            });
            await context.SaveChangesAsync();
        }

        return plant;
    }

    // ------------------------------------------------------------------
    // REP-02/03: telemetría
    // ------------------------------------------------------------------

    [Fact]
    public async Task Telemetria_Excluye_Invalidas_De_Estadisticas_Y_Cuenta_Por_Calidad()
    {
        await using var context = _fixture.NewContext();
        var sensor = await context.Sensors.Include(s => s.Node).FirstAsync();
        var seed = "rep-tele-" + Guid.NewGuid().ToString("N")[..8];

        var day1 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await AddReadingAsync(context, sensor.Id, sensor.NodeId, day1, 20m, TelemetryContract.QualityValid, seed, 0);
        await AddReadingAsync(context, sensor.Id, sensor.NodeId, day1.AddHours(1), 22m, TelemetryContract.QualityValid, seed, 1);
        await AddReadingAsync(context, sensor.Id, sensor.NodeId, day1.AddHours(2), 25m, TelemetryContract.QualitySuspect, seed, 2);
        await AddReadingAsync(context, sensor.Id, sensor.NodeId, day1.AddHours(3), 9999m, TelemetryContract.QualityInvalid, seed, 3);
        await AddReadingAsync(context, sensor.Id, sensor.NodeId, day1.AddHours(4), 30m, TelemetryContract.QualityStale, seed, 4);
        // Fuera del rango: no debe aparecer.
        await AddReadingAsync(context, sensor.Id, sensor.NodeId, day1.AddDays(-10), 10m, TelemetryContract.QualityValid, seed, 5);

        var service = BuildService();
        var result = await service.GetTelemetryReportAsync(
            new TelemetryReportRequest(
                FromUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ToUtc: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
            page: 1,
            pageSize: 20);

        Assert.Equal(5, result.TotalReadings);      // 5 dentro del rango
        Assert.Equal(4, result.UsableReadings);     // VALID+SUSPECT+STALE
        Assert.Equal(1, result.NonUsableReadings);  // INVALID excluded del promedio

        var stat = Assert.Single(result.Stats);
        Assert.Equal(20m, stat.Min);                // el 9999 inválido NO entra al min
        Assert.Equal(30m, stat.Max);
        Assert.Equal(24.25m, Math.Round(stat.Average!.Value, 2)); // (20+22+25+30)/4
        Assert.Equal(23.5m, stat.Median);           // mediana de [20,22,25,30]
        Assert.True(stat.MedianComputed);
        Assert.Equal(1, stat.SuspectCount);
        Assert.Equal(1, stat.InvalidCount);
        Assert.Equal(1, stat.DaysWithData);
        var invalidQuality = Assert.Single(stat.QualityCounts, q => q.Quality == TelemetryContract.QualityInvalid);
        Assert.Equal(1, invalidQuality.Count);

        // Detalle por defecto: solo utilizables.
        Assert.Equal(4, result.TotalRows);
        Assert.DoesNotContain(result.Rows, r => r.Quality == TelemetryContract.QualityInvalid);

        // Detalle con inválidas incluidas.
        var withInvalid = await service.GetTelemetryReportAsync(
            new TelemetryReportRequest(
                FromUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ToUtc: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                IncludeNonUsable: true),
            page: 1,
            pageSize: 20);
        Assert.Equal(5, withInvalid.TotalRows);
        Assert.Contains(withInvalid.Rows, r => r.Quality == TelemetryContract.QualityInvalid);
    }

    [Fact]
    public async Task Telemetria_Trunca_Rango_Largo_Y_Advierte()
    {
        await using var context = _fixture.NewContext();
        var service = BuildService();

        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = await service.GetTelemetryReportAsync(
            new TelemetryReportRequest(from, from.AddDays(200)),
            page: 1,
            pageSize: 20);

        Assert.Contains(result.Warnings, w => w.Contains("92", StringComparison.Ordinal));
        Assert.True(result.RangeDays <= ReportLimits.MaxRangeDays + 1);
    }

    [Fact]
    public async Task Telemetria_Rango_Sin_Datos_No_Muestra_Ceros()
    {
        await using var context = _fixture.NewContext();
        var service = BuildService();

        var result = await service.GetTelemetryReportAsync(
            new TelemetryReportRequest(
                FromUtc: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ToUtc: new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
            page: 1,
            pageSize: 20);

        Assert.Empty(result.Stats);
        Assert.Empty(result.Rows);
        Assert.Equal(0, result.TotalReadings);
    }

    [Fact]
    public async Task Telemetria_Pagina_El_Detalle()
    {
        await using var context = _fixture.NewContext();
        var sensor = await context.Sensors.Include(s => s.Node).FirstAsync();
        var seed = "rep-page-" + Guid.NewGuid().ToString("N")[..8];
        var day = new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
            await AddReadingAsync(context, sensor.Id, sensor.NodeId, day.AddMinutes(i), 20m + i,
                TelemetryContract.QualityValid, seed, i);

        var service = BuildService();
        var page1 = await service.GetTelemetryReportAsync(
            new TelemetryReportRequest(day, day.AddMinutes(10)), page: 1, pageSize: 2);
        Assert.Equal(5, page1.TotalRows);
        Assert.Equal(2, page1.Rows.Count);
        Assert.Equal(1, page1.Page);

        var page3 = await service.GetTelemetryReportAsync(
            new TelemetryReportRequest(day, day.AddMinutes(10)), page: 3, pageSize: 2);
        Assert.Single(page3.Rows);
        Assert.Equal(3, page3.Page);
    }

    // ------------------------------------------------------------------
    // REP-02: ciclo de cultivo
    // ------------------------------------------------------------------

    [Fact]
    public async Task Ciclo_Calcula_Errores_Con_Prediccion_Y_Cosecha_Real()
    {
        await using var context = _fixture.NewContext();
        var (cropId, ghId) = await GetBaseAsync(context);
        var lot = await CreateLotAsync(context, cropId, ghId, accumulatedGdd: 300m);

        var actualHarvest = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-5));
        lot.ActualHarvestDate = actualHarvest;
        lot.ActualYieldKg = 12m;
        await context.SaveChangesAsync();

        context.Predictions.Add(new Prediction
        {
            LotId = lot.Id,
            GeneratedAt = DateTime.UtcNow.AddDays(-30),
            EstimatedHarvestDate = actualHarvest.AddDays(3),
            AccumulatedGdd = 300m,
            EstimatedYield = 13.2m,
            ModelVersion = "gdd-v1"
        });
        await context.SaveChangesAsync();

        var service = BuildService();
        var result = await service.GetCycleReportAsync(new CycleReportRequest(lot.Id));

        var row = Assert.Single(result.Rows);
        Assert.True(row.HasPrediction);
        Assert.Equal(actualHarvest.AddDays(3), row.EstimatedHarvestDate);
        Assert.Equal(actualHarvest, row.ActualHarvestDate);
        Assert.Equal(13.2m, row.EstimatedYieldKg);
        Assert.Equal(12m, row.ActualYieldKg);
        Assert.Equal(3, row.DaysError);
        Assert.Equal(10.0m, row.YieldErrorPercent); // |13.2-12|/12*100
    }

    [Fact]
    public async Task Ciclo_Sin_Prediccion_Marca_Advertencia_Y_Sin_Cosecha_Queda_Pendiente()
    {
        await using var context = _fixture.NewContext();
        var (cropId, ghId) = await GetBaseAsync(context);
        var lot = await CreateLotAsync(context, cropId, ghId, accumulatedGdd: 300m);

        var service = BuildService();
        var result = await service.GetCycleReportAsync(new CycleReportRequest(lot.Id));

        var row = Assert.Single(result.Rows);
        Assert.False(row.HasPrediction);
        Assert.Null(row.ActualHarvestDate);
        Assert.Null(row.DaysError);         // sin cosecha real → pendiente, no error falso
        Assert.Null(row.YieldErrorPercent);
        Assert.Contains(row.Warnings, w => w.Contains("Sin predicción persistida", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // REP-02: forecast vs cosecha (misma métrica que forecasting)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Forecast_Vs_Cosecha_Usa_Prediccion_Reciente_Y_Metricas_Del_Controlador()
    {
        await using var context = _fixture.NewContext();

        // La colección comparte la base: el resumen del reporte es GLOBAL (como el
        // GetModelAccuracyAsync del controlador), así que este test parte de cero
        // eliminando lotes de tests de reportes anteriores (prefijo "Lote Rep ").
        var leftover = await context.Lots.Where(l => l.Name!.StartsWith("Lote Rep ")).ToListAsync();
        context.Lots.RemoveRange(leftover);
        await context.SaveChangesAsync();

        var (cropId, ghId) = await GetBaseAsync(context);

        // Lote A: predicción ANTES de la cosecha → días y MAPE.
        var lotA = await CreateLotAsync(context, cropId, ghId);
        var harvestA = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-6));
        lotA.ActualHarvestDate = harvestA;
        lotA.ActualYieldKg = 100m;
        await context.SaveChangesAsync();
        context.Predictions.Add(new Prediction
        {
            LotId = lotA.Id,
            GeneratedAt = DateTime.UtcNow.AddDays(-20),
            EstimatedHarvestDate = harvestA.AddDays(2),
            AccumulatedGdd = 300m,
            EstimatedYield = 110m,
            ModelVersion = "gdd-v1"
        });

        // Lote B: predicción DESPUÉS de la cosecha → sin error de días, MAPE sí.
        var lotB = await CreateLotAsync(context, cropId, ghId);
        var harvestB = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30));
        lotB.ActualHarvestDate = harvestB;
        lotB.ActualYieldKg = 50m;
        await context.SaveChangesAsync();
        context.Predictions.Add(new Prediction
        {
            LotId = lotB.Id,
            GeneratedAt = DateTime.UtcNow.AddDays(-5),
            EstimatedHarvestDate = harvestB.AddDays(1),
            AccumulatedGdd = 300m,
            EstimatedYield = 60m,
            ModelVersion = "gdd-v1"
        });
        await context.SaveChangesAsync();

        var service = BuildService();
        var result = await service.GetForecastVsHarvestAsync();

        Assert.Equal(2, result.Summary.Cycles);
        Assert.Equal(2, result.Summary.CyclesWithYield);
        Assert.Equal(1, result.Summary.CyclesWithDays);

        var rowA = result.Rows.Single(r => r.LotId == lotA.Id);
        Assert.Equal(2, rowA.DaysError);
        Assert.True(rowA.DaysComputed);
        Assert.Equal(10.0m, rowA.YieldErrorPercent); // |110-100|/100*100

        var rowB = result.Rows.Single(r => r.LotId == lotB.Id);
        Assert.Null(rowB.DaysError);
        Assert.False(rowB.DaysComputed);
        Assert.Equal(20.0m, rowB.YieldErrorPercent); // |60-50|/50*100

        Assert.Equal(15.0m, result.Summary.AverageMapePercent); // (10+20)/2
        Assert.Equal(2.0m, result.Summary.AverageDaysError);    // solo lote A
    }

    // ------------------------------------------------------------------
    // REP-06: estado de lote (predominancia consumida del dominio)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Estado_Lote_Reporta_Mixto_Desde_El_Dominio_Y_Separa_Vacias_De_Descartadas()
    {
        await using var context = _fixture.NewContext();
        var (cropId, ghId) = await GetBaseAsync(context);
        var lot = await CreateLotAsync(context, cropId, ghId, rows: 2, columns: 2);

        await AddPlantAsync(context, lot, 1, 1, "En desarrollo");
        await AddPlantAsync(context, lot, 1, 2, "Baby Leaf apta", score: 88m);
        // Descartada: no participa de la predominancia.
        await AddPlantAsync(context, lot, 2, 1, "Riesgo / fuera de ventana", PlantOperationalState.Descartada);

        var service = BuildService();
        var result = await service.GetLotStateReportAsync(lot.Id);

        Assert.NotNull(result.State);
        var state = result.State!;
        Assert.True(state.IsCommercialStageMixed);                     // 1 vs 1 → Mixto
        Assert.Equal(Services.Lotes.CommercialStageNames.Mixto, state.CommercialStageName);
        Assert.Equal(2, state.ActivePlants);
        Assert.Equal(1, state.DiscardedPlants);
        Assert.Equal(0, state.HarvestedPlants);
        Assert.Equal(1, state.EmptyPositions);                         // 2x2 = 4 - 3 plantas
    }

    // ------------------------------------------------------------------
    // REP-07: reporte de plantas
    // ------------------------------------------------------------------

    [Fact]
    public async Task Plantas_Filtra_Por_Posicion_Estado_Y_Toma_Ultima_Evaluacion()
    {
        await using var context = _fixture.NewContext();
        var (cropId, ghId) = await GetBaseAsync(context);
        var lot = await CreateLotAsync(context, cropId, ghId, rows: 2, columns: 2);

        var p1 = await AddPlantAsync(context, lot, 1, 1, "En desarrollo");
        var p2 = await AddPlantAsync(context, lot, 2, 1, "Baby Leaf apta",
            PlantOperationalState.Cosechada, score: 90m);

        // Dos evaluaciones para p1: la última debe ganar.
        var config = await context.BabyLeafConfigs.FirstAsync();
        context.BabyLeafEvaluations.AddRange(
            new BabyLeafEvaluation
            {
                PlantId = p1.Id, BabyLeafConfigId = config.Id,
                EvaluatedAtUtc = DateTime.UtcNow.AddDays(-2), BabyLeafScore = 40m,
                Result = "En desarrollo", Confidence = 0.8m, MandatoryCriteriaMet = true
            },
            new BabyLeafEvaluation
            {
                PlantId = p1.Id, BabyLeafConfigId = config.Id,
                EvaluatedAtUtc = DateTime.UtcNow, BabyLeafScore = 75m,
                Result = "En desarrollo", Confidence = 0.9m, MandatoryCriteriaMet = true
            });
        await context.SaveChangesAsync();

        var service = BuildService();

        // Filtro por lote: 2 plantas.
        var all = await service.GetPlantReportAsync(new PlantReportRequest(LotId: lot.Id), page: 1, pageSize: 20);
        Assert.Equal(2, all.TotalRows);

        // Filtro por fila.
        var row1 = await service.GetPlantReportAsync(new PlantReportRequest(LotId: lot.Id, Row: 1), page: 1, pageSize: 20);
        var p1row = Assert.Single(row1.Rows);
        Assert.Equal(75m, p1row.BabyLeafScore); // última evaluación
        Assert.Equal(PlantOperationalState.Activa, p1row.OperationalState);

        // Filtro por estado operativo.
        var harvested = await service.GetPlantReportAsync(
            new PlantReportRequest(LotId: lot.Id, OperationalState: PlantOperationalState.Cosechada), page: 1, pageSize: 20);
        var p2row = Assert.Single(harvested.Rows);
        Assert.Equal(p2.Id, p2row.PlantId);
        Assert.Equal(90m, p2row.BabyLeafScore);

        // Filtro por columna inexistente → vacío, no ceros.
        var none = await service.GetPlantReportAsync(new PlantReportRequest(LotId: lot.Id, Column: 9), page: 1, pageSize: 20);
        Assert.Equal(0, none.TotalRows);
        Assert.Contains(none.Warnings, w => w.Contains("No hay plantas", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // REP-08: cambios de estado
    // ------------------------------------------------------------------

    [Fact]
    public async Task Cambios_Declara_Pendiente_Sin_Historial()
    {
        await using var context = _fixture.NewContext();
        var (cropId, ghId) = await GetBaseAsync(context);
        var lot = await CreateLotAsync(context, cropId, ghId);
        await AddPlantAsync(context, lot, 1, 1, "En desarrollo");

        var service = BuildService();
        var result = await service.GetStageChangesAsync(lotId: lot.Id);

        Assert.Equal(0, result.TotalRows);
        Assert.Empty(result.Rows);
        Assert.Contains(result.Warnings, w => w.Contains("PENDIENTE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cambios_Resuelve_Nombres_De_Etapas_Y_Filtra_Por_Origen()
    {
        await using var context = _fixture.NewContext();
        var (cropId, ghId) = await GetBaseAsync(context);
        var lot = await CreateLotAsync(context, cropId, ghId);
        var plant = await AddPlantAsync(context, lot, 1, 1, "En desarrollo");

        var enDesarrollo = await context.CommercialStages.FirstAsync(s => s.Name == "En desarrollo");
        var candidata = await context.CommercialStages.FirstAsync(s => s.Name == "Candidata Baby Leaf");
        var apta = await context.CommercialStages.FirstAsync(s => s.Name == "Baby Leaf apta");

        context.PlantStageHistories.AddRange(
            new PlantStageHistory
            {
                PlantId = plant.Id,
                PreviousCommercialStageId = enDesarrollo.Id,
                NewCommercialStageId = candidata.Id,
                NewOperationalState = PlantOperationalState.Activa.ToString(),
                Source = "daily-flow",
                Reason = "score en banda candidata",
                ChangedAtUtc = DateTime.UtcNow.AddDays(-3)
            },
            new PlantStageHistory
            {
                PlantId = plant.Id,
                PreviousCommercialStageId = candidata.Id,
                NewCommercialStageId = apta.Id,
                NewOperationalState = PlantOperationalState.Cosechada.ToString(),
                Source = "harvest",
                Reason = "cosecha Baby Leaf",
                ChangedAtUtc = DateTime.UtcNow.AddDays(-1)
            });
        await context.SaveChangesAsync();

        var service = BuildService();

        var all = await service.GetStageChangesAsync(lotId: lot.Id);
        Assert.Equal(2, all.TotalRows);
        Assert.DoesNotContain(all.Rows, r => r.NewCommercialStage == enDesarrollo.Id.ToString()); // nombres, no ids
        Assert.Contains(all.Rows, r => r.NewCommercialStage == "Candidata Baby Leaf");
        Assert.Contains(all.Rows, r => r.NewCommercialStage == "Baby Leaf apta");
        Assert.Contains(all.Rows, r => r.Source == "harvest" && r.NewOperationalState == nameof(PlantOperationalState.Cosechada));

        var harvestOnly = await service.GetStageChangesAsync(lotId: lot.Id, source: "harvest");
        var row = Assert.Single(harvestOnly.Rows);
        Assert.Equal("harvest", row.Source);
        Assert.Equal("Candidata Baby Leaf", row.PreviousCommercialStage);
        Assert.Equal("Baby Leaf apta", row.NewCommercialStage);
    }

    private sealed class TestDbContextFactory(LotesSqlFixture fixture)
        : IDbContextFactory<HydroPilotDbContext>
    {
        public HydroPilotDbContext CreateDbContext() => fixture.NewContext();

        public Task<HydroPilotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(fixture.NewContext());
    }
}