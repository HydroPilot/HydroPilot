using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Optimization;
using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Anomalies;
using HydroPilotWeb.Services.Dashboard;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.Optimization;
using HydroPilotWeb.Services.Simulation;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Colección serializada (xunit.runner.json) para los tests de integración del
/// módulo de optimización sobre SQL Server local.
/// </summary>
[CollectionDefinition("optimization-sql")]
public class OptimizationSqlCollection : ICollectionFixture<OptimizationSqlFixture>;

/// <summary>Sink de prueba: guarda los avisos que Optimización manda a Notificaciones.</summary>
public sealed class RecordingOptimizationSink : IOptimizationEventSink
{
    private readonly List<RecommendationEventDto> _events = [];

    public IReadOnlyList<RecommendationEventDto> Events
    {
        get { lock (_events) return _events.ToList(); }
    }

    public Task OnRecommendationCreatedAsync(RecommendationEventDto recommendation, CancellationToken ct = default)
    {
        lock (_events) _events.Add(recommendation);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Fixture de integración de Optimización: base SQL limpia (drop + migrate con las MIGRACIONES REALES),
/// catálogos mínimos (cultivo con rangos, etapas, estados comerciales, Baby Leaf), un sensor de temperatura
/// ambiente con 20,5 GDD por día durante los últimos 45 días (los lotes avanzan según cuántos días hace que se
/// sembraron), pH/CE, pesos (30 g / 250 g), precios ($50.000 / $11.800 por kg) y nutrientes ($25.000/L).
/// Servicios reales: Lotes, Simulación, Anomalías (solución) y Optimización. Requiere SQL Server local
/// (SETUP.md) o HYDROPILOT_TEST_SQL.
/// </summary>
public sealed class OptimizationSqlFixture : IAsyncLifetime
{
    public const string DbName = "hydropilot_optimization_test";

    /// <summary>GDD por día de las temperaturas sembradas: (30 + 20) / 2 − 4,5.</summary>
    public const decimal DailyGdd = 20.5m;

    public string ConnectionString { get; }
    public IDbContextFactory<HydroPilotDbContext> Factory { get; private set; } = null!;
    public OptimizationService Optimization { get; private set; } = null!;
    public SolutionStatusService Solutions { get; private set; } = null!;
    public RecordingOptimizationSink Sink { get; } = new();

    public OptimizationSqlFixture()
    {
        var env = Environment.GetEnvironmentVariable("HYDROPILOT_TEST_SQL");
        ConnectionString = env
            ?? $"Server=localhost,1433;Database={DbName};User Id=sa;Password=GuardamosCositas0!;TrustServerCertificate=True";
    }

    public HydroPilotDbContext NewContext() => new(
        new DbContextOptionsBuilder<HydroPilotDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var dropContext = NewContext();
        await dropContext.Database.EnsureDeletedAsync();

        await using var context = NewContext();
        await context.Database.MigrateAsync(); // valida la cadena completa de migraciones sobre base limpia

        await SeedBaseAsync(context);

        Factory = new SimpleFactory(NewOptions());

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Weather:ApiKey"] = "" })
            .Build();

        var settings = new SettingsService(Factory);
        await settings.SetDecimalAsync(SystemParametersService.BabyLeafGramsKey, 30m);
        await settings.SetDecimalAsync(SystemParametersService.ConventionalGramsKey, 250m);
        await settings.SetDecimalAsync(SystemParametersService.NutrientPricePerLiterKey, 25_000m);

        var weather = new WeatherService(new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, Factory, configuration, NullLogger<WeatherService>.Instance);
        var gdd = new GddService(Factory, weather, settings);
        var forecast = new ForecastService(Factory, gdd, new YieldService(Factory), weather, settings, NullLogger<ForecastService>.Instance);
        var aggregate = new LotAggregateService(Factory, gdd);
        var parameters = new SystemParametersService(Factory, settings, NullLogger<SystemParametersService>.Instance);
        var dashboardOptions = Options.Create(new DashboardOptions());
        var overview = new LotOverviewService(Factory, aggregate, forecast, gdd, parameters, dashboardOptions, NullLogger<LotOverviewService>.Instance);
        var simulation = new SimulationService(Factory, gdd, new ClimateScenarioProvider(gdd, weather), aggregate, parameters,
            new BabyLeafOutlookService(Factory, gdd));

        Solutions = new SolutionStatusService(Factory, dashboardOptions);
        Optimization = new OptimizationService(Factory, overview, simulation, Solutions, parameters, Sink,
            NullLogger<OptimizationService>.Instance);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private DbContextOptions<HydroPilotDbContext> NewOptions() =>
        new DbContextOptionsBuilder<HydroPilotDbContext>().UseSqlServer(ConnectionString).Options;

    /// <summary>Factory simple (OnConfiguring del contexto no permite pooling).</summary>
    private sealed class SimpleFactory : IDbContextFactory<HydroPilotDbContext>
    {
        private readonly DbContextOptions<HydroPilotDbContext> _options;
        public SimpleFactory(DbContextOptions<HydroPilotDbContext> options) => _options = options;
        public HydroPilotDbContext CreateDbContext() => new(_options);
    }

    // ------------------------------------------------------------------
    // Seed base
    // ------------------------------------------------------------------

    private static async Task SeedBaseAsync(HydroPilotDbContext context)
    {
        var crop = new CropType
        {
            Name = "Lechuga Baby Leaf",
            GddTarget = 300m,
            BaseTemperature = 4.5m,
            OptimalPhMin = 5.5m,
            OptimalPhTarget = 6.0m,
            OptimalPhMax = 6.5m,
            OptimalEcMin = 1.2m,
            OptimalEcMax = 1.8m,
            EstimatedDaysToHarvest = 25,
            YieldPerM2 = 3.0m,
        };
        context.CropTypes.Add(crop);
        context.LotStatuses.AddRange(
            new LotStatus { Name = "ACTIVO" },
            new LotStatus { Name = "COSECHADO" },
            new LotStatus { Name = "DESCARTADO" },
            new LotStatus { Name = "EN_PAUSA" });
        context.SaveChanges();

        context.PhenologicalStages.AddRange(
            new PhenologicalStage { CropTypeId = crop.Id, Name = "Establecimiento", Order = 1, GddMin = 0, GddMax = 150, EcMin = 0.8m, EcObjective = 1.0m, EcMax = 1.2m },
            new PhenologicalStage { CropTypeId = crop.Id, Name = "Crecimiento vegetativo", Order = 2, GddMin = 150, GddMax = 450, EcMin = 1.2m, EcObjective = 1.5m, EcMax = 1.8m },
            new PhenologicalStage { CropTypeId = crop.Id, Name = "Formación y madurez", Order = 3, GddMin = 450, GddMax = 750, EcMin = 1.5m, EcObjective = 1.7m, EcMax = 1.8m });

        context.CommercialStages.AddRange(
            new CommercialStage { CropTypeId = crop.Id, Name = CommercialStageNames.EnDesarrollo },
            new CommercialStage { CropTypeId = crop.Id, Name = CommercialStageNames.CandidataBabyLeaf },
            new CommercialStage { CropTypeId = crop.Id, Name = CommercialStageNames.BabyLeafApta },
            new CommercialStage { CropTypeId = crop.Id, Name = CommercialStageNames.CosechaConvencional },
            new CommercialStage { CropTypeId = crop.Id, Name = CommercialStageNames.RiesgoFueraDeVentana });

        context.BabyLeafConfigs.Add(new BabyLeafConfig
        {
            CropTypeId = crop.Id,
            Name = "Baby Leaf test v1",
            GddMin = 250m,
            GddMax = 450m,
            ScoreMinCandidate = 60m,
            ScoreMinReady = 80m,
            Version = "1.0"
        });
        context.SaveChanges();

        // Invernadero (UTC: el GDD diario se agrupa sin depender de la zona) + nodo + sensores pH/CE/temperatura.
        var greenhouse = new Greenhouse { Name = "Invernadero Optimization Test", Location = "Test", TimeZoneId = "UTC", CreatedAt = DateTime.UtcNow };
        context.Greenhouses.Add(greenhouse);
        context.SaveChanges();

        var node = new IotNode { GreenhouseId = greenhouse.Id, Identifier = "opt-node-01", Status = "ACTIVO", CreatedAt = DateTime.UtcNow };
        context.IotNodes.Add(node);
        context.SensorTypes.AddRange(
            new SensorType { Name = "pH" },
            new SensorType { Name = "CE" },
            new SensorType { Name = "Temperatura" });
        context.MeasurementUnits.AddRange(
            new MeasurementUnit { Name = "pH", Symbol = "pH" },
            new MeasurementUnit { Name = "milisiemens por centímetro", Symbol = "mS/cm" },
            new MeasurementUnit { Name = "grados Celsius", Symbol = "°C" });
        context.SaveChanges();

        var types = context.SensorTypes.ToDictionary(t => t.Name);
        var units = context.MeasurementUnits.ToDictionary(u => u.Name);
        context.Sensors.AddRange(
            new Sensor { NodeId = node.Id, SensorTypeId = types["pH"].Id, MeasurementUnitId = units["pH"].Id, Name = "ph-solucion", TechnicalKey = "ph-solucion", IsActive = true, CreatedAt = DateTime.UtcNow },
            new Sensor { NodeId = node.Id, SensorTypeId = types["CE"].Id, MeasurementUnitId = units["milisiemens por centímetro"].Id, Name = "ec-solucion", TechnicalKey = "ec-solucion", IsActive = true, CreatedAt = DateTime.UtcNow },
            new Sensor { NodeId = node.Id, SensorTypeId = types["Temperatura"].Id, MeasurementUnitId = units["grados Celsius"].Id, Name = "temp-amb", TechnicalKey = "temp-amb", IsActive = true, CreatedAt = DateTime.UtcNow });
        context.SaveChanges();

        // Temperaturas: mínima 20 °C y máxima 30 °C cada día de los últimos 45 (20,5 GDD por día).
        var temperature = context.Sensors.First(s => s.Name == "temp-amb");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var counter = 0;
        for (var day = today.AddDays(-45); day <= today; day = day.AddDays(1))
        {
            foreach (var (hour, value) in new[] { (3, 20m), (15, 30m) })
            {
                var observed = day.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Utc);
                context.SensorReadings.Add(new SensorReading
                {
                    SensorId = temperature.Id,
                    NodeId = temperature.NodeId,
                    MeasurementUnitId = temperature.MeasurementUnitId,
                    Value = value,
                    Timestamp = observed,
                    ObservedAtUtc = observed,
                    ReceivedAtUtc = observed,
                    ExternalReadingId = $"opt-temp-{++counter}",
                    Quality = TelemetryContract.QualityValid,
                    IngestionResult = TelemetryContract.ResultAceptada,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        // Precios por kg cargados por el usuario (globales).
        context.CostPriceCatalogs.AddRange(
            Price(null, OptimizationContractDestination.BabyLeaf, 50_000m),
            Price(null, OptimizationContractDestination.Conventional, 11_800m));
        await context.SaveChangesAsync();
    }

    public static CostPriceCatalog Price(int? lotId, string destination, decimal value) => new()
    {
        LotId = lotId,
        Destination = destination,
        Item = OptimizationContractCostItem.PricePerKg,
        Value = value,
        Currency = "ARS",
        ValidFrom = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-60),
        Source = "manual",
        CreatedAtUtc = DateTime.UtcNow
    };

    // ------------------------------------------------------------------
    // Helpers para los tests
    // ------------------------------------------------------------------

    /// <summary>
    /// Crea un lote ACTIVO sembrado hace <paramref name="daysAgo"/> días (GDD ≈ (daysAgo + 1) × 20,5) con
    /// las plantas pedidas, etapa aplicada "Crecimiento vegetativo" (CE 1,2–1,8, objetivo 1,5). Devuelve su id.
    /// </summary>
    public static async Task<int> SeedLotAsync(
        HydroPilotDbContext context,
        int daysAgo,
        int apta = 0,
        int candidate = 0,
        int developing = 0,
        int risk = 0,
        decimal? babyLeafGoal = null,
        decimal? maxLoss = null)
    {
        var crop = await context.CropTypes.FirstAsync();
        var status = await context.LotStatuses.FirstAsync(s => s.Name == "ACTIVO");
        var stage2 = await context.PhenologicalStages.FirstAsync(s => s.Order == 2);
        var greenhouse = await context.Greenhouses.FirstAsync();

        var total = apta + candidate + developing + risk;
        var lot = new Lot
        {
            GreenhouseId = greenhouse.Id,
            CropTypeId = crop.Id,
            StatusId = status.Id,
            Name = "Lote Opt " + Interlocked.Increment(ref _lotCounter),
            SowingDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-daysAgo),
            PlantedAreaM2 = 0m,
            GridRows = 1,
            GridColumns = Math.Max(1, total),
            AppliedPhenologicalStageId = stage2.Id,
            BabyLeafGoalPercent = babyLeafGoal,
            MaxLossPercent = maxLoss,
            CreatedAt = DateTime.UtcNow
        };
        context.Lots.Add(lot);
        await context.SaveChangesAsync();

        var comm = await context.CommercialStages.ToDictionaryAsync(s => s.Name);
        var column = 0;
        void Add(int count, string stage)
        {
            for (var i = 0; i < count; i++)
            {
                context.Plants.Add(new Plant
                {
                    LotId = lot.Id,
                    Row = 1,
                    Column = ++column,
                    CommercialStageId = comm[stage].Id,
                    OperationalState = PlantOperationalState.Activa,
                    CreatedAtUtc = DateTime.UtcNow
                });
            }
        }
        Add(apta, CommercialStageNames.BabyLeafApta);
        Add(candidate, CommercialStageNames.CandidataBabyLeaf);
        Add(developing, CommercialStageNames.EnDesarrollo);
        Add(risk, CommercialStageNames.RiesgoFueraDeVentana);
        await context.SaveChangesAsync();

        return lot.Id;
    }

    /// <summary>Lectura de pH o CE del lote.</summary>
    public static async Task AddReadingAsync(HydroPilotDbContext context, int lotId, string sensorName, decimal value, DateTime observedAtUtc)
    {
        var sensor = await context.Sensors.FirstAsync(s => s.Name == sensorName);
        context.SensorReadings.Add(new SensorReading
        {
            SensorId = sensor.Id,
            NodeId = sensor.NodeId,
            LotId = lotId,
            MeasurementUnitId = sensor.MeasurementUnitId,
            Value = value,
            Timestamp = observedAtUtc,
            ObservedAtUtc = observedAtUtc,
            ReceivedAtUtc = DateTime.UtcNow,
            ExternalReadingId = $"opt-{sensorName}-{Interlocked.Increment(ref _readingCounter)}",
            Quality = TelemetryContract.QualityValid,
            IngestionResult = TelemetryContract.ResultAceptada,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    /// <summary>Cosecha hoy las plantas activas del lote en ese estado comercial (lo congela, como Lotes).</summary>
    public static async Task HarvestAsync(HydroPilotDbContext context, int lotId, string commercialStage)
    {
        var plants = await context.Plants
            .Where(p => p.LotId == lotId
                        && p.OperationalState == PlantOperationalState.Activa
                        && p.CommercialStage != null && p.CommercialStage.Name == commercialStage)
            .ToListAsync();
        foreach (var plant in plants)
        {
            plant.OperationalState = PlantOperationalState.Cosechada;
            plant.HarvestDate = DateOnly.FromDateTime(DateTime.UtcNow);
        }
        await context.SaveChangesAsync();
    }

    private static int _lotCounter;
    private static int _readingCounter;
}
