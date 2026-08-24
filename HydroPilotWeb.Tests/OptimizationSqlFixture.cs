using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Optimization;
using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.Optimization;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Colección serializada (xunit.runner.json) para los tests de integración del
/// módulo de optimización sobre SQL Server local.
/// </summary>
[CollectionDefinition("optimization-sql")]
public class OptimizationSqlCollection : ICollectionFixture<OptimizationSqlFixture>;

/// <summary>
/// Fixture de integración del módulo de optimización: base SQL limpia
/// (drop + migrate con las MIGRACIONES REALES) y catálogos mínimos del dominio
/// (cultivo con rangos, etapas fenológicas, estados comerciales, Baby Leaf,
/// invernadero/nodo/sensores pH-CE-Temperatura, lote con plantas y catálogo de
/// costos/precios). Requiere SQL Server local (SETUP.md) o HYDROPILOT_TEST_SQL.
/// </summary>
public sealed class OptimizationSqlFixture : IAsyncLifetime
{
    public const string DbName = "hydropilot_optimization_test";

    public string ConnectionString { get; }
    public IDbContextFactory<HydroPilotDbContext> Factory { get; private set; } = null!;
    public OptimizationService Optimization { get; private set; } = null!;
    public ForecastService Forecast { get; private set; } = null!;

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
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var settings = new SettingsService(Factory);
        var weather = new WeatherService(new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, Factory, configuration, NullLogger<WeatherService>.Instance);
        var gdd = new GddService(Factory, weather, settings);
        var yieldService = new YieldService(Factory);
        Forecast = new ForecastService(Factory, gdd, yieldService, weather, settings, NullLogger<ForecastService>.Instance);
        var agg = new LotAggregateService(Factory, gdd);

        Optimization = new OptimizationService(
            Factory,
            Forecast,
            agg,
            new NoPlantRiskProvider(),
            new NoOpenAnomalyProvider(),
            new NoopOptimizationEventSink(NullLogger<NoopOptimizationEventSink>.Instance),
            Options.Create(new OptimizationOptions()),
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

        var babyLeafApta = new CommercialStage { CropTypeId = crop.Id, Name = "Baby Leaf apta" };
        var riesgo = new CommercialStage { CropTypeId = crop.Id, Name = "Riesgo / fuera de ventana" };
        context.CommercialStages.AddRange(
            new CommercialStage { CropTypeId = crop.Id, Name = "En desarrollo" },
            new CommercialStage { CropTypeId = crop.Id, Name = "Candidata Baby Leaf" },
            babyLeafApta,
            new CommercialStage { CropTypeId = crop.Id, Name = "Cosecha convencional" },
            riesgo);

        var config = new BabyLeafConfig
        {
            CropTypeId = crop.Id,
            Name = "Baby Leaf test v1",
            GddMin = 250m,
            GddMax = 450m,
            ScoreMinCandidate = 60m,
            ScoreMinReady = 80m,
            Version = "1.0"
        };
        context.BabyLeafConfigs.Add(config);
        context.SaveChanges();

        // Invernadero + nodo + sensores pH/CE/temperatura.
        var greenhouse = new Greenhouse { Name = "Invernadero Optimization Test", Location = "Test", CreatedAt = DateTime.UtcNow };
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

        // Catálogo de costos/precios (mismo horizonte que el demo).
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        context.CostPriceCatalogs.AddRange(
            new CostPriceCatalog { CropTypeId = crop.Id, Destination = "baby_leaf", Item = "price_per_kg", Value = 2600m, Currency = "ARS", ValidFrom = today.AddDays(-10), ValidUntil = today.AddDays(90), Source = "fixture", CreatedAtUtc = DateTime.UtcNow },
            new CostPriceCatalog { CropTypeId = crop.Id, Destination = "baby_leaf", Item = "seed_cost_m2", Value = 900m, Currency = "ARS", ValidFrom = today.AddDays(-10), ValidUntil = today.AddDays(90), Source = "fixture", CreatedAtUtc = DateTime.UtcNow },
            new CostPriceCatalog { CropTypeId = crop.Id, Destination = "baby_leaf", Item = "nutrient_cost_m2", Value = 320m, Currency = "ARS", ValidFrom = today.AddDays(-10), ValidUntil = today.AddDays(90), Source = "fixture", CreatedAtUtc = DateTime.UtcNow },
            new CostPriceCatalog { CropTypeId = crop.Id, Destination = "baby_leaf", Item = "energy_cost_m2", Value = 260m, Currency = "ARS", ValidFrom = today.AddDays(-10), ValidUntil = today.AddDays(90), Source = "fixture", CreatedAtUtc = DateTime.UtcNow },
            new CostPriceCatalog { CropTypeId = crop.Id, Destination = "baby_leaf", Item = "transplant_cost_m2", Value = 0m, Currency = "ARS", ValidFrom = today.AddDays(-10), ValidUntil = today.AddDays(90), Source = "fixture", CreatedAtUtc = DateTime.UtcNow },
            new CostPriceCatalog { CropTypeId = crop.Id, Destination = "conventional", Item = "price_per_kg", Value = 1800m, Currency = "ARS", ValidFrom = today.AddDays(-10), ValidUntil = today.AddDays(90), Source = "fixture", CreatedAtUtc = DateTime.UtcNow },
            new CostPriceCatalog { CropTypeId = crop.Id, Destination = "conventional", Item = "seed_cost_m2", Value = 250m, Currency = "ARS", ValidFrom = today.AddDays(-10), ValidUntil = today.AddDays(90), Source = "fixture", CreatedAtUtc = DateTime.UtcNow },
            new CostPriceCatalog { CropTypeId = crop.Id, Destination = "conventional", Item = "nutrient_cost_m2", Value = 420m, Currency = "ARS", ValidFrom = today.AddDays(-10), ValidUntil = today.AddDays(90), Source = "fixture", CreatedAtUtc = DateTime.UtcNow },
            new CostPriceCatalog { CropTypeId = crop.Id, Destination = "conventional", Item = "energy_cost_m2", Value = 520m, Currency = "ARS", ValidFrom = today.AddDays(-10), ValidUntil = today.AddDays(90), Source = "fixture", CreatedAtUtc = DateTime.UtcNow },
            new CostPriceCatalog { CropTypeId = crop.Id, Destination = "conventional", Item = "transplant_cost_m2", Value = 480m, Currency = "ARS", ValidFrom = today.AddDays(-10), ValidUntil = today.AddDays(90), Source = "fixture", CreatedAtUtc = DateTime.UtcNow });
        await context.SaveChangesAsync();
    }

    // ------------------------------------------------------------------
    // Helpers para los tests
    // ------------------------------------------------------------------

    public sealed record SeededLot(int LotId, int CropTypeId, DateTime SowingUtc);

    /// <summary>
    /// Crea un lote ACTIVO con grilla 2x3, 2 plantas aptas, 1 planta en riesgo y
    /// 1 en desarrollo; nodo asignado al lote; AppliedPhenologicalStage = etapa 2
    /// (EC objetivo 1.5). Devuelve el lote con sus ids.
    /// </summary>
    public static async Task<SeededLot> SeedOperativeLotAsync(
        HydroPilotDbContext context,
        decimal? currentPh = 6.0m,
        decimal? currentEc = 1.5m,
        string? name = "Lote Opt Test")
    {
        var crop = await context.CropTypes.FirstAsync();
        var status = await context.LotStatuses.FirstAsync(s => s.Name == "ACTIVO");
        var stage2 = await context.PhenologicalStages.FirstAsync(s => s.Order == 2);
        var greenhouse = await context.Greenhouses.FirstAsync();
        var node = await context.IotNodes.FirstAsync();

        var lot = new Lot
        {
            GreenhouseId = greenhouse.Id,
            CropTypeId = crop.Id,
            StatusId = status.Id,
            Name = name + " " + Interlocked.Increment(ref _lotCounter),
            SowingDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-15)),
            PlantedAreaM2 = 4.5m,
            GridRows = 2,
            GridColumns = 3,
            CurrentPh = currentPh,
            CurrentEc = currentEc,
            AppliedPhenologicalStageId = stage2.Id,
            BabyLeafHarvestTargetPercent = 70m,
            CreatedAt = DateTime.UtcNow
        };
        context.Lots.Add(lot);
        await context.SaveChangesAsync();

        // Una sola asignación abierta por nodo (índice filtrado): cierra previas.
        var openAssignments = context.NodeLotAssignments
            .Where(a => a.NodeId == node.Id && a.ValidUntilUtc == null)
            .ToList();
        foreach (var assignment in openAssignments)
        {
            assignment.ValidUntilUtc = DateTime.UtcNow;
        }

        var comm = await context.CommercialStages.ToDictionaryAsync(s => s.Name);
        context.Plants.AddRange(
            new Plant { LotId = lot.Id, Row = 1, Column = 1, CommercialStageId = comm["Baby Leaf apta"].Id, OperationalState = PlantOperationalState.Activa, CreatedAtUtc = DateTime.UtcNow },
            new Plant { LotId = lot.Id, Row = 1, Column = 2, CommercialStageId = comm["Baby Leaf apta"].Id, OperationalState = PlantOperationalState.Activa, CreatedAtUtc = DateTime.UtcNow },
            new Plant { LotId = lot.Id, Row = 1, Column = 3, CommercialStageId = comm["En desarrollo"].Id, OperationalState = PlantOperationalState.Activa, CreatedAtUtc = DateTime.UtcNow },
            new Plant { LotId = lot.Id, Row = 2, Column = 1, CommercialStageId = comm["Riesgo / fuera de ventana"].Id, OperationalState = PlantOperationalState.Activa, CreatedAtUtc = DateTime.UtcNow });
        await context.SaveChangesAsync();

        context.NodeLotAssignments.Add(new NodeLotAssignment
        {
            NodeId = node.Id,
            LotId = lot.Id,
            ValidFromUtc = DateTime.UtcNow.AddDays(-30),
            ValidUntilUtc = null,
            Source = "fixture",
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        return new SeededLot(lot.Id, crop.Id, DateTime.UtcNow);
    }

    /// <summary>Inserta una lectura de sensor (pH/CE/temperatura) con fecha de observación dada.</summary>
    public static async Task AddReadingAsync(
        HydroPilotDbContext context,
        string sensorName,
        decimal value,
        DateTime observedAtUtc,
        string quality = TelemetryContract.QualityValid)
    {
        var sensor = await context.Sensors.FirstAsync(s => s.Name == sensorName);
        var seedTag = $"opt-{sensorName}-" + Interlocked.Increment(ref _readingCounter);
        context.SensorReadings.Add(new SensorReading
        {
            SensorId = sensor.Id,
            NodeId = sensor.NodeId,
            MeasurementUnitId = sensor.MeasurementUnitId,
            Value = value,
            Timestamp = observedAtUtc,
            ObservedAtUtc = observedAtUtc,
            ReceivedAtUtc = DateTime.UtcNow,
            ExternalReadingId = seedTag,
            Quality = quality,
            IngestionResult = TelemetryContract.ResultAceptada,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    /// <summary>Asigna una lectura de pH/CE al lote (el LotId lo resuelve el sensor del lote).</summary>
    public static async Task AssignLatestReadingsToLotAsync(HydroPilotDbContext context, int lotId)
    {
        var sensorIds = await context.Sensors
            .Where(s => s.Name == "ph-solucion" || s.Name == "ec-solucion")
            .Select(s => s.Id)
            .ToListAsync();
        var readings = await context.SensorReadings
            .Where(r => sensorIds.Contains(r.SensorId) && r.LotId == null)
            .ToListAsync();
        foreach (var reading in readings)
        {
            reading.LotId = lotId;
        }
        await context.SaveChangesAsync();
    }

    /// <summary>Borra lecturas de pH/CE (deja limpio para cada caso).</summary>
    public static async Task ClearReadingsAsync(HydroPilotDbContext context)
    {
        var sensorIds = await context.Sensors
            .Where(s => s.Name == "ph-solucion" || s.Name == "ec-solucion")
            .Select(s => s.Id)
            .ToListAsync();
        var readings = await context.SensorReadings
            .Where(r => sensorIds.Contains(r.SensorId))
            .ToListAsync();
        context.SensorReadings.RemoveRange(readings);
        await context.SaveChangesAsync();
    }

    private static int _lotCounter;
    private static int _readingCounter;
}

/// <summary>Colección de tests de integración del módulo (serializada).</summary>
[Collection("optimization-sql")]
public abstract class OptimizationSqlTestBase
{
    protected OptimizationSqlFixture Fixture { get; }

    protected OptimizationSqlTestBase(OptimizationSqlFixture fixture) => Fixture = fixture;
}