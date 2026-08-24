using HydroPilotWeb.Controllers;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HydroPilotWeb.Tests.Integration;

/// <summary>
/// Pruebas de integración del módulo forecasting (F-01..F-11) sobre SQL Server
/// real con las MIGRACIONES y el SEED del producto (fixture forecast-sql).
/// Cada prueba usa una ventana de fechas DISJUNTA (siembra→asOf) para que las
/// lecturas del sensor compartido del invernadero no contaminen otras pruebas;
/// las de rendimiento/precisión aíslan lotes y predicciones al inicio.
/// </summary>
[Collection("forecast-sql")]
public class ForecastSqlServerTests
{
    private readonly ForecastSqlFixture _fixture;

    public ForecastSqlServerTests(ForecastSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static DateTime UtcDate(DateOnly d) => d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private HydroPilotDbContext NewContext() => _fixture.Factory.CreateDbContext();

    // --- F-02: GDD histórico ---

    [Fact]
    public async Task Gdd_Observado_Calcula_Desde_Lecturas_Usables()
    {
        await using var context = NewContext();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-40));
        await ForecastTestData.SeedTemperatureReadingsAsync(context, UtcDate(lot.SowingDate), 10);

        var result = await _fixture.Forecast.GetForecastAsync(lot.Id, Today.AddDays(-31));

        Assert.NotNull(result);
        Assert.True(result.GddAccumulated > 0m, "GDD acumulado debe ser > 0 con 10 días de lecturas.");
        Assert.Equal(10, result.CoverageDays);
        Assert.Equal(0, result.MissingDays);
        Assert.All(result.GddHistory, p => Assert.Equal(GddPointSource.Observed, p.Source));
    }

    [Fact]
    public async Task Gdd_Descarta_Lecturas_Con_Calidad_No_Usable()
    {
        await using var context = NewContext();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-80));
        var start = UtcDate(lot.SowingDate);

        await ForecastTestData.SeedTemperatureReadingsAsync(context, start, 3); // VALID
        await ForecastTestData.SeedTemperatureReadingsAsync(context, start, 5,
            qualityOverride: TelemetryContract.QualityInvalid);
        await ForecastTestData.SeedTemperatureReadingsAsync(context, start, 5,
            qualityOverride: TelemetryContract.QualityFuture);
        await ForecastTestData.SeedTemperatureReadingsAsync(context, start, 5,
            qualityOverride: TelemetryContract.QualitySensorError);

        var result = await _fixture.Forecast.GetForecastAsync(lot.Id, Today.AddDays(-71));

        Assert.NotNull(result);
        // Solo los 3 días VALID aportan cobertura; las demás calidades se descartan.
        Assert.Equal(3, result.CoverageDays);
        Assert.Equal(7, result.MissingDays);
        Assert.Contains(result.Warnings, w => w.Contains("calidad no usable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Gdd_Incluye_Suspect_Y_Stale_Como_Usables()
    {
        await using var context = NewContext();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-120));
        var start = UtcDate(lot.SowingDate);

        await ForecastTestData.SeedTemperatureReadingsAsync(context, start, 2, qualityOverride: TelemetryContract.QualitySuspect);
        await ForecastTestData.SeedTemperatureReadingsAsync(context, start.AddDays(2), 2, qualityOverride: TelemetryContract.QualityStale);

        var result = await _fixture.Forecast.GetForecastAsync(lot.Id, Today.AddDays(-111));

        Assert.NotNull(result);
        Assert.Equal(4, result.CoverageDays); // SUSPECT y STALE son operativamente usables.
        Assert.True(result.GddAccumulated > 0m);
    }

    [Fact]
    public async Task Gdd_AsOfDate_No_Usa_Datos_Futuros()
    {
        await using var context = NewContext();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-160));
        var asOf = Today.AddDays(-155);

        // 14 días de lecturas: 6 dentro del período [siembra -160, asOf -155] y 8 posteriores.
        await ForecastTestData.SeedTemperatureReadingsAsync(context, UtcDate(lot.SowingDate), 14);

        var result = await _fixture.Forecast.GetForecastAsync(lot.Id, asOf);

        Assert.NotNull(result);
        Assert.Equal(asOf, result.AsOfDate);
        Assert.Equal(6, result.CoverageDays);
        Assert.Contains(result.Warnings, w => w.Contains("Consulta simulada", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Gdd_Sin_Lecturas_Reporta_Faltantes_No_Cero_Silencioso()
    {
        await using var context = NewContext();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-200));

        var result = await _fixture.Forecast.GetForecastAsync(lot.Id, Today.AddDays(-191));

        Assert.NotNull(result);
        Assert.Equal(0m, result.GddAccumulated);
        Assert.Equal(0, result.CoverageDays);
        Assert.Equal(10, result.MissingDays);
        Assert.Equal(0m, result.CoveragePercent);
        Assert.Contains(result.Warnings, w => w.Contains("faltan", StringComparison.OrdinalIgnoreCase)
                                              || w.Contains("Sin lecturas", StringComparison.OrdinalIgnoreCase));
    }

    // --- F-02: zona horaria del invernadero ---

    [Fact]
    public async Task Gdd_Agrupa_Por_Zona_Horaria_Del_Invernadero()
    {
        await using var context = NewContext();

        // Invernadero dedicado en Pacific/Auckland (UTC+12): una lectura a las
        // 12:00 UTC cae al día siguiente local → las fechas del histórico se corren +1.
        var north = new Greenhouse { Name = "Invernadero NZ", Location = "Test", TimeZoneId = "Pacific/Auckland", CreatedAt = DateTime.UtcNow };
        context.Greenhouses.Add(north);
        await context.SaveChangesAsync();

        var node = new IotNode { GreenhouseId = north.Id, Identifier = "nz-node-01", Status = "ACTIVO", CreatedAt = DateTime.UtcNow };
        context.IotNodes.Add(node);
        await context.SaveChangesAsync();

        var tempType = await context.SensorTypes.FirstAsync(t => t.Name == "Temperatura");
        var unit = await context.MeasurementUnits.FirstAsync(u => u.Name == "grados Celsius");
        context.Sensors.Add(new Sensor
        {
            NodeId = node.Id,
            SensorTypeId = tempType.Id,
            MeasurementUnitId = unit.Id,
            Name = "temp-nz-01",
            TechnicalKey = "temp-nz-01",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var lot = await ForecastTestData.CreateActiveLotInGreenhouseAsync(context, north.Id, Today.AddDays(-100));
        await ForecastTestData.SeedConstantTemperatureReadingsAsync(context, UtcDate(lot.SowingDate), 10, 30m, north.Id);

        var result = await _fixture.Forecast.GetForecastAsync(lot.Id, Today.AddDays(-91));

        Assert.NotNull(result);
        Assert.Equal(lot.SowingDate.AddDays(1), result.GddHistory.First().Date); // +1 por zona UTC+12
        Assert.Equal(10, result.CoverageDays);
    }

    // --- F-03: proyección y pronóstico ---

    [Fact]
    public async Task Proyeccion_Sin_Api_De_Clima_Usa_Fallback_Del_Sensor_Con_Advertencia()
    {
        await using var context = NewContext();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-240));
        await ForecastTestData.SeedTemperatureReadingsAsync(context, UtcDate(lot.SowingDate), 10);

        // El fixture no tiene Weather:ApiKey → el fetch se omite y la proyección
        // cae a fallback del promedio del sensor (nunca a cero silencioso).
        var result = await _fixture.Forecast.GetForecastAsync(lot.Id, Today.AddDays(-231));

        Assert.NotNull(result);
        Assert.Equal(7, result.ForecastHorizonDays);
        Assert.NotEmpty(result.FutureProjection);
        Assert.True(result.FutureProjection.All(p => p.Source == GddPointSource.Fallback),
            "Sin pronóstico persistido, todos los puntos proyectados deben ser fallback.");
        Assert.Contains(result.Warnings, w => w.Contains("Sin pronóstico climático", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ForecastSourceKind.Fallback, result.SourceKind);
    }

    [Fact]
    public async Task Proyeccion_Con_Pronostico_Persistido_Usa_Forecast()
    {
        await using var context = NewContext();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-280));
        await ForecastTestData.SeedTemperatureReadingsAsync(context, UtcDate(lot.SowingDate), 10);

        // Pronóstico persistido exactamente en la ventana proyectada ([-270..-264]).
        var start = Today.AddDays(-270);
        for (var i = 0; i < 7; i++)
        {
            context.DailyWeatherForecasts.Add(new DailyWeatherForecast
            {
                Date = start.AddDays(i),
                TempMin = 16m,
                TempMax = 26m,
                FetchedAt = DateTime.UtcNow
            });
        }
        await context.SaveChangesAsync();

        var result = await _fixture.Forecast.GetForecastAsync(lot.Id, Today.AddDays(-271));

        Assert.NotNull(result);
        Assert.Contains(result.FutureProjection, p => p.Source == GddPointSource.Forecast);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("Sin pronóstico climático", StringComparison.OrdinalIgnoreCase));
    }

    // --- F-05: rendimiento ---

    [Fact]
    public async Task Yield_Usa_Solo_Cosechados_Del_Mismo_Cultivo_Excluyendo_Actual()
    {
        await using var context = NewContext();
        await ForecastTestData.ResetLotsAndPredictionsAsync(context);

        // Dos ciclos cerrados del mismo cultivo (4.5 m²): 13.5 kg y 22.5 kg → 3 y 5 kg/m².
        var closed1 = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-40));
        await ForecastTestData.CloseLotAsync(context, closed1.Id, 13.5m, Today.AddDays(-16));
        var closed2 = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-35));
        await ForecastTestData.CloseLotAsync(context, closed2.Id, 22.5m, Today.AddDays(-11));

        // Lote activo objetivo (se excluye a sí mismo aunque haya cerrado).
        var current = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-10));
        await ForecastTestData.CloseLotAsync(context, current.Id, 999m, Today.AddDays(-1));
        current.StatusId = (await context.LotStatuses.FirstAsync(s => s.Name == "ACTIVO")).Id;
        await context.SaveChangesAsync();

        var result = await _fixture.Forecast.GetForecastAsync(current.Id, Today.AddDays(-1));

        Assert.NotNull(result);
        Assert.Equal(2, result.Yield.HistoryCycles);
        Assert.Equal(18.00m, result.Yield.Base); // (3+5)/2 kg/m² × 4.5 m²
        Assert.True(result.Yield.Conservative <= result.Yield.Base && result.Yield.Base <= result.Yield.Optimistic);
    }

    [Fact]
    public async Task Yield_Con_Menos_De_Dos_Ciclos_Usa_Baseline_Del_Cultivo()
    {
        await using var context = NewContext();
        await ForecastTestData.ResetLotsAndPredictionsAsync(context);

        var current = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-10));

        var result = await _fixture.Forecast.GetForecastAsync(current.Id, Today.AddDays(-1));

        Assert.NotNull(result);
        Assert.Equal(0, result.Yield.HistoryCycles);
        Assert.Equal(13.50m, result.Yield.Base); // 3.0 kg/m² × 4.5 m²
    }

    // --- F-05: precisión ---

    [Fact]
    public async Task Precision_Usa_Ultima_Prediccion_Anterior_A_La_Cosecha()
    {
        await using var context = NewContext();
        await ForecastTestData.ResetLotsAndPredictionsAsync(context);

        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-40));
        await ForecastTestData.AddPredictionAsync(context, lot.Id, Today.AddDays(-20), 300m, 14.0m, Today.AddDays(-13));
        await ForecastTestData.AddPredictionAsync(context, lot.Id, Today.AddDays(-8), 300m, 20.0m, Today.AddDays(-13));
        await ForecastTestData.CloseLotAsync(context, lot.Id, 16.0m, Today.AddDays(-8) /* cosecha en -8 */);

        var result = await _fixture.Forecast.GetForecastAsync(lot.Id);

        Assert.NotNull(result);
        Assert.Equal(1, result.AccuracyCycles);
        // La elegida es la de -8 (última pre-cosecha): |20-16|/16 = 25%.
        Assert.Equal(25.0m, result.AccuracyMape);
        Assert.NotNull(result.AccuracyDaysError);
    }

    [Fact]
    public async Task Precision_Mape_Null_Con_Rendimiento_Real_Cero()
    {
        await using var context = NewContext();
        await ForecastTestData.ResetLotsAndPredictionsAsync(context);

        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-40));
        await ForecastTestData.AddPredictionAsync(context, lot.Id, Today.AddDays(-20), 300m, 14.0m, Today.AddDays(-13));
        await ForecastTestData.CloseLotAsync(context, lot.Id, 0m, Today.AddDays(-8));

        var result = await _fixture.Forecast.GetForecastAsync(lot.Id);

        Assert.NotNull(result);
        Assert.Equal(1, result.AccuracyCycles);
        Assert.Null(result.AccuracyMape); // no divide por cero
        Assert.NotNull(result.AccuracyDaysError); // el error de días sigue disponible
    }

    // --- F-04: cosecha idempotente y validación ---

    [Fact]
    public async Task Cosecha_Idempotente_No_Duplica_Fecha()
    {
        await using var context = NewContext();
        var controller = BuildController();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-20));

        var first = await controller.RecordHarvest(lot.Id, new RecordHarvestRequest(12.5m, Today.AddDays(-1)));
        var second = await controller.RecordHarvest(lot.Id, new RecordHarvestRequest(90.0m, Today.AddDays(-2)));

        Assert.IsType<OkObjectResult>(first);
        Assert.IsType<OkObjectResult>(second);

        await using var check = NewContext();
        var lotAfter = await check.Lots.Include(l => l.Status).FirstAsync(l => l.Id == lot.Id);
        Assert.Equal(12.5m, lotAfter.ActualYieldKg);            // el segundo llamado NO pisó el valor
        Assert.Equal(Today.AddDays(-1), lotAfter.ActualHarvestDate); // ni la fecha
        Assert.Equal("COSECHADO", lotAfter.Status!.Name);
    }

    [Fact]
    public async Task Cosecha_Rechaza_Rendimiento_Negativo()
    {
        await using var context = NewContext();
        var controller = BuildController();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-20));

        var bad = await controller.RecordHarvest(lot.Id, new RecordHarvestRequest(-5m, Today));

        Assert.IsType<BadRequestObjectResult>(bad);
    }

    // --- F-01: contrato común API + UI ---

    [Fact]
    public async Task Controller_Y_Servicio_Devuelven_El_Mismo_Resultado()
    {
        await using var context = NewContext();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-450));
        await ForecastTestData.SeedTemperatureReadingsAsync(context, UtcDate(lot.SowingDate), 10);

        var controller = BuildController();
        var action = await controller.GetForecast(lot.Id);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var fromApi = Assert.IsType<Services.Forecasting.ForecastResult>(ok.Value);

        var fromService = await _fixture.Forecast.GetForecastAsync(lot.Id);

        Assert.NotNull(fromService);
        Assert.Equal(fromApi.GddAccumulated, fromService.GddAccumulated);
        Assert.Equal(fromApi.EstimatedHarvestDate, fromService.EstimatedHarvestDate);
        Assert.Equal(fromApi.GddHarvestDate, fromService.GddHarvestDate);
        Assert.Equal(fromApi.Yield.Base, fromService.Yield.Base);
        Assert.Equal(fromApi.AccuracyMape, fromService.AccuracyMape);
    }

    [Fact]
    public async Task Consulta_Repetida_No_Duplica_Snapshots_Persistidos()
    {
        await using var context = NewContext();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-10));

        // El controller persiste snapshots (persistSnapshot=true); dos consultas
        // iguales deben actualizar la MISMA fila (índice único LotId+AsOfDate+Versión).
        var controller = BuildController();
        await controller.GetForecast(lot.Id);
        await controller.GetForecast(lot.Id);
        await _fixture.Forecast.GetForecastAsync(lot.Id, Today, true);

        await using var check = NewContext();
        var count = await check.Predictions
            .CountAsync(p => p.LotId == lot.Id && p.AsOfDate == Today && p.ModelVersion == Services.Forecasting.ForecastService.ModelVersion);
        Assert.Equal(1, count);
    }

    // --- F-10: regla híbrida de cosecha ---

    [Fact]
    public async Task Readiness_Con_Umbral_Sin_Configurar_Es_Decision_Pendiente()
    {
        await using var context = NewContext();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-320), babyLeafTargetPercent: null);
        await ForecastTestData.SeedConstantTemperatureReadingsAsync(context, UtcDate(lot.SowingDate), 12, 30m);

        var result = await _fixture.Forecast.GetForecastAsync(lot.Id, Today.AddDays(-309));

        Assert.NotNull(result);
        Assert.NotNull(result.HarvestWindow);
        Assert.True(result.HarvestWindow.IsBabyLeaf);
        Assert.True(result.HarvestWindow.GddInWindow); // GDD ~306 en ventana 250-450
        Assert.Null(result.HarvestWindow.IsReady);     // decisión pendiente, no constante oculta
        Assert.Contains(result.Warnings, w => w.Contains("pendiente", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Readiness_Ventana_Con_Porcentaje_Insuficiente_No_Listo()
    {
        await using var context = NewContext();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-350), babyLeafTargetPercent: 70m);
        await ForecastTestData.SeedConstantTemperatureReadingsAsync(context, UtcDate(lot.SowingDate), 12, 30m);

        // 2 aptas de 4 activas = 50% < 70%.
        await AddPlantAsync(context, lot, 1, 1, "Baby Leaf apta");
        await AddPlantAsync(context, lot, 1, 2, "Baby Leaf apta");
        await AddPlantAsync(context, lot, 2, 1, "En desarrollo");
        await AddPlantAsync(context, lot, 2, 2, "En desarrollo");

        var result = await _fixture.Forecast.GetForecastAsync(lot.Id, Today.AddDays(-339));

        Assert.NotNull(result);
        Assert.True(result.HarvestWindow!.IsBabyLeaf);
        Assert.True(result.HarvestWindow.GddInWindow);
        Assert.Equal(2, result.HarvestWindow.AptaCount);
        Assert.Equal(4, result.HarvestWindow.TotalActivePlants);
        Assert.Equal(50.0m, result.HarvestWindow.AptaPercent);
        Assert.False(result.HarvestWindow.IsReady);
        Assert.Contains(result.HarvestWindow.Warnings, w => w.Contains("por debajo del objetivo", StringComparison.OrdinalIgnoreCase));
    }

    // --- F-08: etapa fenológica ---

    [Fact]
    public async Task Etapa_Fenologica_Se_Resuelve_Por_Ventana_Gdd()
    {
        await using var context = NewContext();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-380));
        await ForecastTestData.SeedConstantTemperatureReadingsAsync(context, UtcDate(lot.SowingDate), 20, 30m);

        var result = await _fixture.Forecast.GetForecastAsync(lot.Id, Today.AddDays(-361));

        Assert.NotNull(result);
        Assert.True(result.GddAccumulated >= 450m, $"GDD {result.GddAccumulated} debería superar 450");
        Assert.Equal("Formación y madurez", result.PhenologicalStageName);
    }

    // --- F-09: conteos excluyen cosechadas/descartadas ---

    [Fact]
    public async Task Conteos_Comerciales_Solo_Plantas_Activas()
    {
        await using var context = NewContext();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, Today.AddDays(-410));

        await AddPlantAsync(context, lot, 1, 1, "Baby Leaf apta");
        await AddPlantAsync(context, lot, 1, 2, "En desarrollo");
        await AddPlantAsync(context, lot, 2, 1, "Baby Leaf apta", PlantOperationalState.Cosechada);
        await AddPlantAsync(context, lot, 2, 2, "Riesgo / fuera de ventana", PlantOperationalState.Descartada);

        var result = await _fixture.Forecast.GetForecastAsync(lot.Id, Today.AddDays(-400));

        Assert.NotNull(result);
        var byStage = result.CommercialCounts.ToDictionary(c => c.StageName);
        Assert.Equal(1, byStage["Baby Leaf apta"].Count);
        Assert.Equal(1, byStage["En desarrollo"].Count);
        Assert.False(byStage.ContainsKey("Riesgo / fuera de ventana")); // la descartada no cuenta
        // 1 vs 1 → Mixto.
        Assert.True(result.IsCommercialStageMixed);
        Assert.Equal(CommercialStageNames.Mixto, result.CommercialStageName);
    }

    // --- helpers ---

    private ForecastingController BuildController() => new(
        _fixture.Factory,
        _fixture.Forecast,
        NullLogger<ForecastingController>.Instance);

    private static async Task<Plant> AddPlantAsync(
        HydroPilotDbContext context, Lot lot, int row, int column, string stageName,
        PlantOperationalState state = PlantOperationalState.Activa)
    {
        var stage = await context.CommercialStages
            .FirstAsync(s => s.CropTypeId == lot.CropTypeId && s.Name == stageName);

        var plant = new Plant
        {
            LotId = lot.Id,
            Row = row,
            Column = column,
            CommercialStageId = stage.Id,
            OperationalState = state,
            HarvestDate = state == PlantOperationalState.Cosechada ? Today : null,
            CreatedAtUtc = DateTime.UtcNow
        };
        context.Plants.Add(plant);
        await context.SaveChangesAsync();
        return plant;
    }
}