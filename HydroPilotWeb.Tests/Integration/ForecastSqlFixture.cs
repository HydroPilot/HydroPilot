using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HydroPilotWeb.Tests.Integration;

/// <summary>Colección SQL Server para el módulo forecasting (F-01..F-11).</summary>
[CollectionDefinition("forecast-sql")]
public sealed class ForecastSqlCollection : ICollectionFixture<ForecastSqlFixture>;

/// <summary>Factory simple sin pooling (el OnConfiguring del contexto lo impide).</summary>
public sealed class ForecastDbContextFactory(DbContextOptions<HydroPilotDbContext> options)
    : IDbContextFactory<HydroPilotDbContext>
{
    public HydroPilotDbContext CreateDbContext() => new(options);

    public Task<HydroPilotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}

/// <summary>
/// Fixture de integración de forecasting: base SQL Server limpia (drop + migrate)
/// con el seed completo del DbInitializer (catálogos IoT, lotes, plantas demo).
/// Provee los servicios reales del módulo (GddService, YieldService, ForecastService,
/// controller) para pruebas de extremo a extremo sobre el mismo motor que Azure SQL.
/// </summary>
public sealed class ForecastSqlFixture : IAsyncLifetime
{
    public const string DatabaseName = "hydropilot_forecast_test";

    public string ConnectionString { get; private set; } = string.Empty;
    public IConfiguration Configuration { get; private set; } = null!;
    public IDbContextFactory<HydroPilotDbContext> Factory { get; private set; } = null!;

    /// <summary>Servicios del módulo compartidos por las pruebas.</summary>
    public ForecastService Forecast { get; private set; } = null!;
    public GddService Gdd { get; private set; } = null!;
    public YieldService Yield { get; private set; } = null!;
    public LotAggregateService Aggregate { get; private set; } = null!;
    public SettingsService Settings { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Configuration = new ConfigurationBuilder()
            .AddUserSecrets<ForecastSqlFixture>(optional: true)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:Password"] = "PassDeTestNoProductiva",
                ["TelemetryApiKey"] = "api-key-de-test",
            })
            .Build();

        var baseConnection = Configuration.GetConnectionString("LocalSqlServer")
            ?? throw new InvalidOperationException(
                "Falta 'ConnectionStrings:LocalSqlServer' en user-secrets (ver SETUP.md).");

        ConnectionString = new SqlConnectionStringBuilder(baseConnection)
        {
            InitialCatalog = DatabaseName,
        }.ConnectionString;

        await DropAndCreateDatabaseAsync();

        var options = new DbContextOptionsBuilder<HydroPilotDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
        Factory = new ForecastDbContextFactory(options);

        await using var context = Factory.CreateDbContext();
        await context.Database.MigrateAsync(); // valida la cadena de migraciones sobre base limpia
        DbInitializer.Initialize(context, Configuration); // seed completo (catálogos, nodo, lotes demo)

        // Determinismo espacial de las pruebas: el GDD se agrupa por la zona horaria
        // del invernadero; con UTC los conteos de cobertura son exactos. La zona
        // horaria real se cubre en una prueba dedicada (Gdd_Agrupa_Por_Zona_Del_Invernadero).
        var greenhouse = context.Greenhouses.First();
        greenhouse.TimeZoneId = "UTC";
        await context.SaveChangesAsync();

        var settings = new SettingsService(Factory);
        var weather = new WeatherService(
            new HttpClient(),
            Factory,
            Configuration,
            NullLogger<WeatherService>.Instance);
        var gdd = new GddService(Factory, weather, settings);
        var yield = new YieldService(Factory);
        var aggregate = new LotAggregateService(Factory, gdd);

        Settings = settings;
        Gdd = gdd;
        Yield = yield;
        Aggregate = aggregate;
        Forecast = new ForecastService(
            Factory, gdd, yield, weather, settings, NullLogger<ForecastService>.Instance);
    }

    public async Task DisposeAsync()
    {
        var masterConnection = new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = "master",
        }.ConnectionString;

        await using var connection = new SqlConnection(masterConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF DB_ID(N'{DatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{DatabaseName}];
            END;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task DropAndCreateDatabaseAsync()
    {
        var masterConnection = new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = "master",
        }.ConnectionString;

        await using var connection = new SqlConnection(masterConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF DB_ID(N'{DatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{DatabaseName}];
            END;
            CREATE DATABASE [{DatabaseName}];
            """;
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>Helpers de siembra para las pruebas de forecasting.</summary>
public static class ForecastTestData
{
    private static int _seedCounter;

    /// <summary>
    /// Crea un lote ACTIVO dentro del invernadero del seed, con grilla configurada.
    /// Sin asignación nodo→lote (el helper limpia las asignaciones del fixture).
    /// </summary>
    public static async Task<Lot> CreateActiveLotAsync(
        HydroPilotDbContext context,
        DateOnly sowingDate,
        decimal areaM2 = 4.5m,
        decimal? babyLeafTargetPercent = 70m,
        int rows = 3,
        int columns = 4)
    {
        var greenhouseId = (await context.Greenhouses.FirstAsync()).Id;
        return await CreateActiveLotInGreenhouseAsync(
            context, greenhouseId, sowingDate, areaM2, babyLeafTargetPercent, rows, columns);
    }

    /// <summary>Crea un lote ACTIVO en un invernadero específico (para pruebas de zona horaria).</summary>
    public static async Task<Lot> CreateActiveLotInGreenhouseAsync(
        HydroPilotDbContext context,
        int greenhouseId,
        DateOnly sowingDate,
        decimal areaM2 = 4.5m,
        decimal? babyLeafTargetPercent = 70m,
        int rows = 3,
        int columns = 4)
    {
        await ClearNodeAssignmentsAsync(context);

        var crop = await context.CropTypes.FirstAsync();
        var status = await context.LotStatuses.FirstAsync(s => s.Name == "ACTIVO");

        var lot = new Lot
        {
            GreenhouseId = greenhouseId,
            CropTypeId = crop.Id,
            StatusId = status.Id,
            Name = "Lote Test Forecast " + Interlocked.Increment(ref _seedCounter),
            SowingDate = sowingDate,
            PlantedAreaM2 = areaM2,
            GridRows = rows,
            GridColumns = columns,
            BabyLeafHarvestTargetPercent = babyLeafTargetPercent,
            CreatedAt = DateTime.UtcNow
        };
        context.Lots.Add(lot);
        await context.SaveChangesAsync();
        return lot;
    }

    /// <summary>Cierra un lote como cosechado (rendimiento + fecha) — contrato F-04.</summary>
    public static async Task CloseLotAsync(HydroPilotDbContext context, int lotId, decimal yieldKg, DateOnly harvestDate)
    {
        var lot = await context.Lots.Include(l => l.Status).FirstAsync(l => l.Id == lotId);
        var harvested = await context.LotStatuses.FirstAsync(s => s.Name == "COSECHADO");
        lot.ActualYieldKg = yieldKg;
        lot.ActualHarvestDate = harvestDate;
        lot.StatusId = harvested.Id;
        await context.SaveChangesAsync();
    }

    /// <summary>Limpia asignaciones nodo→lote (el fixture seed asigna el nodo al lote demo).</summary>
    public static async Task ClearNodeAssignmentsAsync(HydroPilotDbContext context)
    {
        context.NodeLotAssignments.RemoveRange(context.NodeLotAssignments);
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Aisla las pruebas globales (rendimiento/precisión): borra asignaciones,
    /// lotes y sus predicciones/plantas (cascade). Las lecturas quedan (comparten
    /// el sensor del invernadero, pero cada prueba usa ventanas de fecha disjuntas).
    /// </summary>
    public static async Task ResetLotsAndPredictionsAsync(HydroPilotDbContext context)
    {
        context.NodeLotAssignments.RemoveRange(context.NodeLotAssignments);
        context.Predictions.RemoveRange(context.Predictions);
        context.Lots.RemoveRange(context.Lots);
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Siembra lecturas de temperatura con valor constante (Tmax == Tmin → GDD diario
    /// = valor - Tbase, cap 30), útil para alcanzar ventanas GDD deterministas.
    /// greenhouseId null = primer invernadero del seed.
    /// </summary>
    public static async Task SeedConstantTemperatureReadingsAsync(
        HydroPilotDbContext context,
        DateTime startUtc,
        int days,
        decimal value,
        int? greenhouseId = null)
    {
        var tag = "ftc-" + Interlocked.Increment(ref _seedCounter);
        var ghId = greenhouseId ?? (await context.Greenhouses.FirstAsync()).Id;

        var sensor = await context.Sensors
            .Include(s => s.Node)
            .Include(s => s.SensorType)
            .FirstAsync(s => s.SensorType!.Name == "Temperatura"
                             && s.Node!.GreenhouseId == ghId);

        for (var i = 0; i < days; i++)
        {
            var observed = startUtc.AddDays(i).AddHours(12);
            context.SensorReadings.Add(new SensorReading
            {
                SensorId = sensor.Id,
                NodeId = sensor.Node!.Id,
                MeasurementUnitId = sensor.MeasurementUnitId,
                Value = value,
                Timestamp = observed,
                ObservedAtUtc = observed,
                ReceivedAtUtc = DateTime.UtcNow,
                ExternalReadingId = $"{tag}-{i}",
                Quality = TelemetryContract.QualityValid,
                CreatedAt = DateTime.UtcNow
            });
        }

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Siembra lecturas de temperatura realistas (onda senoidal) para el sensor
    /// ambiental del invernadero. Cada lectura lleva calidad, y las fechas pueden
    /// extenderse más allá de asOfDate para probar la fuga de datos futuros.
    /// </summary>
    public static async Task<Sensor> SeedTemperatureReadingsAsync(
        HydroPilotDbContext context,
        DateTime startUtc,
        int days,
        string? qualityOverride = null,
        int readingsPerDay = 4)
    {
        var tag = "ft-" + Interlocked.Increment(ref _seedCounter);
        var greenhouseId = (await context.Greenhouses.FirstAsync()).Id;

        var sensor = await context.Sensors
            .Include(s => s.Node)
            .Include(s => s.SensorType)
            .FirstAsync(s => s.SensorType!.Name == "Temperatura"
                             && s.Node!.GreenhouseId == greenhouseId);

        for (var i = 0; i < days; i++)
        {
            for (var slot = 0; slot < readingsPerDay; slot++)
            {
                var hour = slot * 6; // 0,6,12,18
                var observed = startUtc.AddDays(i).AddHours(hour);
                var value = 18m + 8m * (decimal)Math.Sin((hour - 9) * Math.PI / 12) + (i % 3);

                context.SensorReadings.Add(new SensorReading
                {
                    SensorId = sensor.Id,
                    NodeId = sensor.Node!.Id,
                    MeasurementUnitId = sensor.MeasurementUnitId,
                    Value = value,
                    Timestamp = observed,
                    ObservedAtUtc = observed,
                    ReceivedAtUtc = DateTime.UtcNow,
                    ExternalReadingId = $"{tag}-{i}-{slot}",
                    Quality = qualityOverride ?? TelemetryContract.QualityValid,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        await context.SaveChangesAsync();
        return sensor;
    }

    /// <summary>Inserta una predicción con AsOfDate explícito (contrato F-05).</summary>
    public static async Task AddPredictionAsync(
        HydroPilotDbContext context,
        int lotId,
        DateOnly asOfDate,
        decimal accumulatedGdd,
        decimal? estimatedYield,
        DateOnly? estimatedHarvestDate,
        string version = ForecastService.ModelVersion)
    {
        context.Predictions.Add(new Prediction
        {
            LotId = lotId,
            GeneratedAt = asOfDate.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc),
            AsOfDate = asOfDate,
            AccumulatedGdd = accumulatedGdd,
            EstimatedYield = estimatedYield,
            EstimatedHarvestDate = estimatedHarvestDate,
            ModelVersion = version
        });
        await context.SaveChangesAsync();
    }
}