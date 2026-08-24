using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Dashboard;
using HydroPilotWeb.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Tests de integración del dashboard sobre SQL Server local (plan 12):
/// último valor usable por tipo, delta real, frescura, exclusión de calidades no
/// usables, estados de nodos por frescura, serie agregada por bucket, dos
/// invernaderos sin mezcla y propagación de errores de base.
/// </summary>
[Collection("sql-server-dashboard")]
public class DashboardSqlServerTests
{
    private readonly DashboardSqlFixture _fixture;

    public DashboardSqlServerTests(DashboardSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private DashboardService NewService(DashboardOptions? options = null) => new(
        new SimpleDbContextFactory(
            new DbContextOptionsBuilder<HydroPilotDbContext>().UseSqlServer(_fixture.ConnectionString).Options),
        new TestOptionsMonitor<DashboardOptions>(options ?? new DashboardOptions()),
        NullLogger<DashboardService>.Instance);

    /// <summary>Monitor de opciones simple para DI en pruebas.</summary>
    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T> where T : class
    {
        private readonly T _value;

        public TestOptionsMonitor(T value) => _value = value;

        public T CurrentValue => _value;

        public T Get(string? name) => _value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed record World(
        int GreenhouseId,
        int OtherGreenhouseId,
        int NodeAId,
        int PhSensorId,
        int CeSensorId,
        int TempSensorId,
        int HumSensorId,
        int PressureSensorId,
        int PhUnitId,
        int CeUnitId,
        int TempUnitId,
        int HumUnitId);

    // ------------------------------------------------------------------
    // Seeds
    // ------------------------------------------------------------------

    private static int _seed;

    private static async Task CleanAsync(HydroPilotDbContext ctx)
    {
        // Orden FK-safe: lecturas → asignaciones → rechazos → batches → plantas
        // → evaluaciones → imágenes → lotes → sensores → nodos → invernaderos → catálogos.
        ctx.SensorReadings.RemoveRange(ctx.SensorReadings);
        ctx.NodeLotAssignments.RemoveRange(ctx.NodeLotAssignments);
        ctx.TelemetryRejections.RemoveRange(ctx.TelemetryRejections);
        ctx.TelemetryBatches.RemoveRange(ctx.TelemetryBatches);
        ctx.PlantStageHistories.RemoveRange(ctx.PlantStageHistories);
        ctx.BabyLeafEvaluations.RemoveRange(ctx.BabyLeafEvaluations);
        ctx.PlantImageAnalyses.RemoveRange(ctx.PlantImageAnalyses);
        ctx.PlantImages.RemoveRange(ctx.PlantImages);
        ctx.Plants.RemoveRange(ctx.Plants);
        ctx.Predictions.RemoveRange(ctx.Predictions);
        ctx.Lots.RemoveRange(ctx.Lots);
        ctx.Sensors.RemoveRange(ctx.Sensors);
        ctx.IotNodes.RemoveRange(ctx.IotNodes);
        ctx.Greenhouses.RemoveRange(ctx.Greenhouses);
        ctx.SensorTypes.RemoveRange(ctx.SensorTypes);
        ctx.MeasurementUnits.RemoveRange(ctx.MeasurementUnits);
        await ctx.SaveChangesAsync();
    }

    private async Task<World> SeedWorldAsync(HydroPilotDbContext ctx)
    {
        var now = DateTime.UtcNow;

        var gh1 = new Greenhouse { Name = "Invernadero Alpha Real", Location = "UTN Campus", CreatedAt = now };
        var gh2 = new Greenhouse { Name = "Invernadero B", Location = "Sede 2", CreatedAt = now };
        ctx.Greenhouses.AddRange(gh1, gh2);
        await ctx.SaveChangesAsync();

        var nodeA = new IotNode
        {
            GreenhouseId = gh1.Id, Identifier = "rpi-a", Status = "ACTIVO", ExpectedIntervalSeconds = 300,
            ConnectionState = TelemetryContract.ConnectionOnline, LastAcceptedAt = now.AddMinutes(-2), CreatedAt = now
        };
        var nodeB = new IotNode
        {
            GreenhouseId = gh1.Id, Identifier = "rpi-b", Status = "ACTIVO", ExpectedIntervalSeconds = 300,
            ConnectionState = TelemetryContract.ConnectionNeverConnected, CreatedAt = now
        };
        var nodeC = new IotNode
        {
            GreenhouseId = gh2.Id, Identifier = "rpi-c", Status = "ACTIVO", ExpectedIntervalSeconds = 300,
            ConnectionState = TelemetryContract.ConnectionOnline, LastAcceptedAt = now.AddMinutes(-2), CreatedAt = now
        };
        ctx.IotNodes.AddRange(nodeA, nodeB, nodeC);
        await ctx.SaveChangesAsync();

        var phType = new SensorType { Name = "pH" };
        var ceType = new SensorType { Name = "CE" };
        var tempType = new SensorType { Name = "Temperatura" };
        var humType = new SensorType { Name = "Humedad" };
        var pressureType = new SensorType { Name = "Presión" };
        ctx.SensorTypes.AddRange(phType, ceType, tempType, humType, pressureType);

        var phUnit = new MeasurementUnit { Name = "pH", Symbol = "pH" };
        var ceUnit = new MeasurementUnit { Name = "milisiemens por centímetro", Symbol = "mS/cm" };
        var tempUnit = new MeasurementUnit { Name = "grados Celsius", Symbol = "°C" };
        var humUnit = new MeasurementUnit { Name = "porcentaje", Symbol = "%" };
        var pressureUnit = new MeasurementUnit { Name = "hectopascales", Symbol = "hPa" };
        ctx.MeasurementUnits.AddRange(phUnit, ceUnit, tempUnit, humUnit, pressureUnit);
        await ctx.SaveChangesAsync();

        Sensor Sensor(int nodeId, int typeId, int? unitId, string name) => new()
        {
            NodeId = nodeId, SensorTypeId = typeId, MeasurementUnitId = unitId,
            Name = name, TechnicalKey = name, IsActive = true, CreatedAt = now
        };

        var phSensor = Sensor(nodeA.Id, phType.Id, phUnit.Id, "ph-solucion");
        var ceSensor = Sensor(nodeA.Id, ceType.Id, ceUnit.Id, "ec-solucion");
        var tempSensor = Sensor(nodeA.Id, tempType.Id, tempUnit.Id, "temp-ambiente");
        var humSensor = Sensor(nodeA.Id, humType.Id, humUnit.Id, "hum-ambiente");
        var pressureSensor = Sensor(nodeA.Id, pressureType.Id, pressureUnit.Id, "pres-presion");
        var phSensorGh2 = Sensor(nodeC.Id, phType.Id, phUnit.Id, "ph-gh2");
        ctx.Sensors.AddRange(phSensor, ceSensor, tempSensor, humSensor, pressureSensor, phSensorGh2);
        await ctx.SaveChangesAsync();

        return new World(
            gh1.Id, gh2.Id, nodeA.Id,
            phSensor.Id, ceSensor.Id, tempSensor.Id, humSensor.Id, pressureSensor.Id,
            phUnit.Id, ceUnit.Id, tempUnit.Id, humUnit.Id);
    }

    private async Task AddReadingAsync(
        HydroPilotDbContext ctx,
        int sensorId,
        int nodeId,
        int? unitId,
        decimal value,
        DateTime observedAtUtc,
        string quality = TelemetryContract.QualityValid)
    {
        var tag = Interlocked.Increment(ref _seed);
        ctx.SensorReadings.Add(new SensorReading
        {
            SensorId = sensorId,
            NodeId = nodeId,
            MeasurementUnitId = unitId,
            Value = value,
            ExternalReadingId = $"dash-{tag}",
            Quality = quality,
            IngestionResult = TelemetryContract.ResultAceptada,
            ObservedAtUtc = observedAtUtc,
            Timestamp = observedAtUtc,
            ReceivedAtUtc = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
    }

    // ------------------------------------------------------------------
    // KPIs
    // ------------------------------------------------------------------

    [Fact]
    public async Task Kpis_ShowLastUsableValue_WithUnitTimestampSourceDeltaAndFreshness()
    {
        var now = DateTime.UtcNow;
        await using (var ctx = _fixture.NewContext())
        {
            await CleanAsync(ctx);
            var w = await SeedWorldAsync(ctx);

            await AddReadingAsync(ctx, w.PhSensorId, w.NodeAId, w.PhUnitId, 5.8m, now.AddMinutes(-90));
            await AddReadingAsync(ctx, w.PhSensorId, w.NodeAId, w.PhUnitId, 5.9m, now.AddMinutes(-20));
            await AddReadingAsync(ctx, w.PhSensorId, w.NodeAId, w.PhUnitId, 6.1m, now.AddMinutes(-2));
            await AddReadingAsync(ctx, w.TempSensorId, w.NodeAId, w.TempUnitId, 23.5m, now.AddMinutes(-3));
            await ctx.SaveChangesAsync();
        }

        var service = NewService(new DashboardOptions { KpiFreshnessMinutes = 30 });
        var snapshot = await service.GetSnapshotAsync(DashboardVariableKeys.Ph, DashboardTimeRange.Day1);

        var ph = Assert.Single(snapshot.Kpis, k => k.Key == DashboardVariableKeys.Ph);
        Assert.Equal(6.1m, ph.Value);
        Assert.Equal("pH", ph.Unit);
        Assert.Equal(DashboardFreshness.Reciente, ph.FreshnessState);
        Assert.Equal(5.9m, ph.PreviousValue);
        Assert.NotNull(ph.DeltaPercent);
        Assert.Equal(3.39, ph.DeltaPercent!.Value, precision: 2);
        Assert.Equal("rpi-a", ph.NodeIdentifier);
        Assert.Equal("ph-solucion", ph.SensorName);
        Assert.Equal(TelemetryContract.QualityValid, ph.Quality);
        Assert.NotNull(ph.ObservedAtUtc);
        Assert.Equal("pH", ph.Label); // nombre del catálogo, no hardcodeado

        var temp = Assert.Single(snapshot.Kpis, k => k.Key == DashboardVariableKeys.Temperatura);
        Assert.Equal(23.5m, temp.Value);
        Assert.Equal("°C", temp.Unit);
        Assert.Null(temp.DeltaPercent); // una sola lectura: no hay anterior

        // KPIs sin lecturas: nulo NO se vuelve cero, frescura "sin datos".
        var ce = Assert.Single(snapshot.Kpis, k => k.Key == DashboardVariableKeys.Ce);
        Assert.Null(ce.Value);
        Assert.Equal(DashboardFreshness.SinDatos, ce.FreshnessState);
        Assert.Null(ce.DeltaPercent);

        var pressure = Assert.Single(snapshot.Kpis, k => k.Key == "presión");
        Assert.Null(pressure.Value);
        Assert.Equal(DashboardFreshness.SinDatos, pressure.FreshnessState);

        // Los KPIs canónicos ordenan primero (pH, CE, temp, humedad).
        Assert.Equal(DashboardVariableKeys.Ph, snapshot.Kpis[0].Key);
    }

    [Fact]
    public async Task Kpis_ExcludeNonOperationallyUsableQualities()
    {
        var now = DateTime.UtcNow;
        await using (var ctx = _fixture.NewContext())
        {
            await CleanAsync(ctx);
            var w = await SeedWorldAsync(ctx);

            await AddReadingAsync(ctx, w.PhSensorId, w.NodeAId, w.PhUnitId, 6.1m, now.AddMinutes(-2));
            // Más nueva pero NO usable (SENSOR_ERROR/FUTURE): debe quedar fuera del KPI.
            await AddReadingAsync(ctx, w.PhSensorId, w.NodeAId, w.PhUnitId, 99.9m, now.AddMinutes(-1), TelemetryContract.QualitySensorError);
            await AddReadingAsync(ctx, w.PhSensorId, w.NodeAId, w.PhUnitId, 7.0m, now.AddMinutes(1), TelemetryContract.QualityFuture);
            await ctx.SaveChangesAsync();
        }

        var service = NewService(new DashboardOptions { KpiFreshnessMinutes = 30 });
        var snapshot = await service.GetSnapshotAsync(DashboardVariableKeys.Ph, DashboardTimeRange.Day1);

        var ph = Assert.Single(snapshot.Kpis, k => k.Key == DashboardVariableKeys.Ph);
        Assert.Equal(6.1m, ph.Value); // no 99.9 ni 7.0
        Assert.Equal(DashboardFreshness.Reciente, ph.FreshnessState);
    }

    [Fact]
    public async Task Kpi_StaleReading_ShowsValueButDesactualizado()
    {
        var now = DateTime.UtcNow;
        await using (var ctx = _fixture.NewContext())
        {
            await CleanAsync(ctx);
            var w = await SeedWorldAsync(ctx);
            await AddReadingAsync(ctx, w.PhSensorId, w.NodeAId, w.PhUnitId, 6.0m, now.AddHours(-3), TelemetryContract.QualityStale);
            await ctx.SaveChangesAsync();
        }

        var service = NewService(new DashboardOptions { KpiFreshnessMinutes = 30 });
        var snapshot = await service.GetSnapshotAsync(DashboardVariableKeys.Ph, DashboardTimeRange.Day1);

        var ph = Assert.Single(snapshot.Kpis, k => k.Key == DashboardVariableKeys.Ph);
        Assert.Equal(6.0m, ph.Value); // STALE es usable: el valor real se muestra...
        Assert.Equal(DashboardFreshness.Desactualizado, ph.FreshnessState); // ...pero avisando la antigüedad
    }

    [Fact]
    public async Task Kpis_DoesNotMixGreenhouses()
    {
        var now = DateTime.UtcNow;
        await using (var ctx = _fixture.NewContext())
        {
            await CleanAsync(ctx);
            var w = await SeedWorldAsync(ctx);

            await AddReadingAsync(ctx, w.PhSensorId, w.NodeAId, w.PhUnitId, 6.1m, now.AddMinutes(-2));
            await ctx.SaveChangesAsync();

            // El segundo invernadero tiene su propia lectura de pH mucho más nueva.
            await using (var ctx2 = _fixture.NewContext())
            {
                var phGh2Sensor = await ctx2.Sensors.FirstAsync(s => s.Name == "ph-gh2");
                await AddReadingAsync(ctx2, phGh2Sensor.Id, phGh2Sensor.NodeId, phGh2Sensor.MeasurementUnitId, 9.9m, now.AddMinutes(-1));
                await ctx2.SaveChangesAsync();
            }
        }

        var service = NewService(new DashboardOptions { KpiFreshnessMinutes = 30 });
        var snapshot = await service.GetSnapshotAsync(DashboardVariableKeys.Ph, DashboardTimeRange.Day1);

        var ph = Assert.Single(snapshot.Kpis, k => k.Key == DashboardVariableKeys.Ph);
        Assert.Equal(6.1m, ph.Value); // no 9.9
        Assert.Equal("rpi-a", ph.NodeIdentifier);
    }

    // ------------------------------------------------------------------
    // Nodos y estado del sistema
    // ------------------------------------------------------------------

    [Fact]
    public async Task Nodes_ReportPersistedConnectionStates_AndLastAcceptedTelemetry()
    {
        var now = DateTime.UtcNow;
        await using (var ctx = _fixture.NewContext())
        {
            await CleanAsync(ctx);
            await SeedWorldAsync(ctx);
            await ctx.SaveChangesAsync();
        }

        var service = NewService();
        var snapshot = await service.GetSnapshotAsync(DashboardVariableKeys.Ph, DashboardTimeRange.Day1);

        Assert.Equal(2, snapshot.Nodes.Count); // solo nodos del invernadero (no rpi-c)

        var online = Assert.Single(snapshot.Nodes, n => n.Identifier == "rpi-a");
        Assert.Equal(TelemetryContract.ConnectionOnline, online.ConnectionState);
        Assert.NotNull(online.LastAcceptedAt);
        Assert.Equal(5, online.SensorCount); // pH, CE, temp, humedad y presión

        var never = Assert.Single(snapshot.Nodes, n => n.Identifier == "rpi-b");
        Assert.Equal(TelemetryContract.ConnectionNeverConnected, never.ConnectionState);
        Assert.Null(never.LastAcceptedAt); // nunca aceptó telemetría
    }

    [Fact]
    public async Task HeaderStatus_IsDerivedFromNodes_NotFixedText()
    {
        await using (var ctx = _fixture.NewContext())
        {
            await CleanAsync(ctx);
            await SeedWorldAsync(ctx);
            await ctx.SaveChangesAsync();
        }

        var service = NewService();

        // Sin snapshot previo: la barra consulta los nodos y deriva el estado real.
        var status = await service.GetHeaderStatusAsync();
        Assert.Equal("Invernadero Alpha Real", status.GreenhouseName);
        Assert.Equal("Invernadero Alpha Real · sin lote activo", status.Subtitle);
        Assert.Equal("Atención requerida", status.StatusText); // rpi-b nunca conectado
        Assert.Equal("status-pill--warn", status.StatusCss);

        // Con snapshot: misma derivación desde el último snapshot.
        await service.GetSnapshotAsync(DashboardVariableKeys.Ph, DashboardTimeRange.Day1);
        var afterSnapshot = service.GetHeaderStatus();
        Assert.Equal("Atención requerida", afterSnapshot.StatusText);
    }

    // ------------------------------------------------------------------
    // Serie temporal
    // ------------------------------------------------------------------

    [Fact]
    public async Task Series_AggregatesHourlyReadings_IntoOrderedBuckets()
    {
        var now = DateTime.UtcNow;
        await using (var ctx = _fixture.NewContext())
        {
            await CleanAsync(ctx);
            var w = await SeedWorldAsync(ctx);

            // Lecturas bien separadas (10 min) dentro de la última hora.
            await AddReadingAsync(ctx, w.PhSensorId, w.NodeAId, w.PhUnitId, 6.3m, now.AddMinutes(-35));
            await AddReadingAsync(ctx, w.PhSensorId, w.NodeAId, w.PhUnitId, 6.1m, now.AddMinutes(-25));
            await AddReadingAsync(ctx, w.PhSensorId, w.NodeAId, w.PhUnitId, 6.2m, now.AddMinutes(-15));
            await AddReadingAsync(ctx, w.PhSensorId, w.NodeAId, w.PhUnitId, 6.0m, now.AddMinutes(-5));
            await ctx.SaveChangesAsync();
        }

        var service = NewService(new DashboardOptions { SeriesMaxPoints = 60 });
        var snapshot = await service.GetSnapshotAsync(DashboardVariableKeys.Ph, DashboardTimeRange.Hour1);

        var series = snapshot.Series;
        Assert.Equal(DashboardVariableKeys.Ph, series.VariableKey);
        Assert.Equal("pH", series.Label);
        Assert.Equal("pH", series.Unit);
        Assert.Equal(4, series.Points.Count);
        Assert.All(series.Points, p => Assert.Equal(1, p.ReadingCount));
        Assert.Equal(6.3m, series.Points[0].Value); // ordenados por tiempo: el más viejo primero

        // Sin lecturas de CE: estado vacío explícito (no valores inventados).
        var ceSeries = (await service.GetSnapshotAsync(DashboardVariableKeys.Ce, DashboardTimeRange.Hour1)).Series;
        Assert.Empty(ceSeries.Points);
    }

    [Fact]
    public async Task Series_CapsPointCount_WithTinyMaxPoints()
    {
        var now = DateTime.UtcNow;
        await using (var ctx = _fixture.NewContext())
        {
            await CleanAsync(ctx);
            var w = await SeedWorldAsync(ctx);

            // 13 lecturas cada 5 minutos dentro de la última hora.
            for (var i = 0; i < 13; i++)
            {
                await AddReadingAsync(ctx, w.PhSensorId, w.NodeAId, w.PhUnitId, 6.0m, now.AddMinutes(-5 * i));
            }
            await ctx.SaveChangesAsync();
        }

        var service = NewService(new DashboardOptions { SeriesMaxPoints = 5 });
        var snapshot = await service.GetSnapshotAsync(DashboardVariableKeys.Ph, DashboardTimeRange.Hour1);

        // 3600 s / 5 = 720 s por bucket: a lo sumo 5 puntos, sin descargar todo el histórico.
        Assert.InRange(snapshot.Series.Points.Count, 1, 5);
    }

    // ------------------------------------------------------------------
    // Errores de base
    // ------------------------------------------------------------------

    [Fact]
    public async Task Service_PropagatesDatabaseErrors_SoTheViewCanRetry()
    {
        var badConnection = "Server=localhost,1433;Database=hydropilot_db_que_no_existe_xyz;User Id=sa;Password=GuardamosCositas0!;TrustServerCertificate=True";

        var badService = new DashboardService(
            new SimpleDbContextFactory(new DbContextOptionsBuilder<HydroPilotDbContext>().UseSqlServer(badConnection).Options),
            new TestOptionsMonitor<DashboardOptions>(new DashboardOptions()),
            NullLogger<DashboardService>.Instance);

        // El servicio NO traga el error: la vista lo captura, conserva el último
        // snapshot válido y ofrece reintento.
        await Assert.ThrowsAnyAsync<Exception>(
            () => badService.GetSnapshotAsync(DashboardVariableKeys.Ph, DashboardTimeRange.Day1));
    }

    [Fact]
    public async Task AnomaliesAndRecommendations_AreEmpty_UntilModulesExist()
    {
        await using (var ctx = _fixture.NewContext())
        {
            await CleanAsync(ctx);
            await SeedWorldAsync(ctx);
            await ctx.SaveChangesAsync();
        }

        var service = NewService();
        var snapshot = await service.GetSnapshotAsync(DashboardVariableKeys.Ph, DashboardTimeRange.Day1);

        Assert.Empty(snapshot.Anomalies);        // Sin módulo de anomalías integrado
        Assert.Empty(snapshot.Recommendations);  // Sin módulo de optimization integrado
    }
}