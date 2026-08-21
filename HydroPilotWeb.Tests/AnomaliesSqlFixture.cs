using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using Microsoft.Extensions.Options;
using HydroPilotWeb.Services.Anomalies;
using Microsoft.Extensions.Logging.Abstractions;

namespace HydroPilotWeb.Tests;

/// <summary>Colección serializada para los tests del módulo de anomalías (SQL Server local).</summary>
[CollectionDefinition("anomalies-sql")]
public class AnomaliesSqlCollection : ICollectionFixture<AnomaliesSqlFixture>;

/// <summary>
/// Fixture de integración del módulo de anomalías: crea una base SQL Server limpia
/// (drop + migrate, validando la cadena de migraciones) y siembra el dominio mínimo
/// (cultivo con rangos de pH, etapas con receta de EC, invernadero, nodo, sensores
/// pH/CE/temperatura/humedad, catálogo de reglas y estados de lote).
/// </summary>
public sealed class AnomaliesSqlFixture : IAsyncLifetime
{
    public const string DbName = "hydropilot_anomalies_test";

    private TestDbFactory _factory = null!;
    private sealed class TestDbFactory(string connectionString) : IDbContextFactory<HydroPilotDbContext>
    {
        private readonly DbContextOptions<HydroPilotDbContext> _options =
            new DbContextOptionsBuilder<HydroPilotDbContext>().UseSqlServer(connectionString).Options;

        public HydroPilotDbContext CreateDbContext() => new(_options);
    }

    public AnomaliesSqlFixture()
    {
        var env = Environment.GetEnvironmentVariable("HYDROPILOT_TEST_SQL");
        ConnectionString = env
            ?? $"Server=localhost,1433;Database={DbName};User Id=sa;Password=GuardamosCositas0!;TrustServerCertificate=True";
        _factory = new TestDbFactory(ConnectionString);
    }

    public string ConnectionString { get; }

    public IDbContextFactory<HydroPilotDbContext> Factory => _factory;

    public HydroPilotDbContext NewContext() => _factory.CreateDbContext();

    public AnomalyEventService NewEventService(AnomalyOptions? options = null) =>
        new(_factory, Options.Create(options ?? new AnomalyOptions()), NullLogger<AnomalyEventService>.Instance);

    public AnomalyQueryService NewQueryService(AnomalyOptions? options = null) =>
        new(_factory, Options.Create(options ?? new AnomalyOptions()),
            new AnomalyLotRiskEvaluator(_factory));

    public AnomalyRiskProvider NewRiskProvider() =>
        new(new AnomalyLotRiskEvaluator(_factory));

    public async Task InitializeAsync()
    {
        await using var dropContext = NewContext();
        await dropContext.Database.EnsureDeletedAsync();

        await using var context = NewContext();
        await context.Database.MigrateAsync(); // valida la cadena completa sobre base limpia

        await SeedCatalogsAsync(context);
    }

    public Task DisposeAsync()
    {
        // La base se re-crea en InitializeAsync del siguiente fixture; no se borra acá
        // para permitir inspección posterior (mismo criterio que LotesSqlFixture).
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------------------
    // Siembra
    // ---------------------------------------------------------------------------
    private static async Task SeedCatalogsAsync(HydroPilotDbContext context)
    {
        context.LotStatuses.AddRange(
            new LotStatus { Name = "ACTIVO" },
            new LotStatus { Name = "COSECHADO" },
            new LotStatus { Name = "DESCARTADO" },
            new LotStatus { Name = "EN_PAUSA" });

        var crop = new CropType
        {
            Name = "Lechuga Baby Leaf",
            GddTarget = 300m,
            BaseTemperature = 4.5m,
            OptimalPhMin = 5.8m,
            OptimalPhTarget = 6.0m,
            OptimalPhMax = 6.2m,
            OptimalEcMin = 1.2m,
            OptimalEcMax = 1.8m,
        };
        context.CropTypes.Add(crop);
        await context.SaveChangesAsync();

        context.PhenologicalStages.AddRange(
            new PhenologicalStage { CropTypeId = crop.Id, Name = "Establecimiento", Order = 1, GddMin = 0, GddMax = 150, EcMin = 0.8m, EcObjective = 1.0m, EcMax = 1.2m },
            new PhenologicalStage { CropTypeId = crop.Id, Name = "Crecimiento vegetativo", Order = 2, GddMin = 150, GddMax = 450, EcMin = 1.2m, EcObjective = 1.5m, EcMax = 1.8m },
            new PhenologicalStage { CropTypeId = crop.Id, Name = "Formación y madurez", Order = 3, GddMin = 450, GddMax = 750, EcMin = 1.5m, EcObjective = 1.7m, EcMax = 1.8m });

        context.CommercialStages.AddRange(
            new CommercialStage { CropTypeId = crop.Id, Name = "En desarrollo" },
            new CommercialStage { CropTypeId = crop.Id, Name = "Candidata Baby Leaf" },
            new CommercialStage { CropTypeId = crop.Id, Name = "Baby Leaf apta" },
            new CommercialStage { CropTypeId = crop.Id, Name = "Cosecha convencional" },
            new CommercialStage { CropTypeId = crop.Id, Name = "Riesgo / fuera de ventana" });
        await context.SaveChangesAsync();

        // Invernadero + nodo + tipos de sensor + unidades + sensores (contrato IoT).
        var greenhouse = new Greenhouse { Name = "Invernadero Test", Location = "Test", TimeZoneId = "America/Argentina/Buenos_Aires", CreatedAt = DateTime.UtcNow };
        context.Greenhouses.Add(greenhouse);
        await context.SaveChangesAsync();

        var node = new IotNode { GreenhouseId = greenhouse.Id, Identifier = "anomaly-test-node", Status = "ACTIVO", CreatedAt = DateTime.UtcNow };
        context.IotNodes.Add(node);

        var phType = new SensorType { Name = "pH" };
        var ecType = new SensorType { Name = "CE" };
        var tempType = new SensorType { Name = "Temperatura" };
        var humType = new SensorType { Name = "Humedad" };
        context.SensorTypes.AddRange(phType, ecType, tempType, humType);

        var phUnit = new MeasurementUnit { Name = "pH", Symbol = "pH" };
        var ecUnit = new MeasurementUnit { Name = "milisiemens por centímetro", Symbol = "mS/cm" };
        var tempUnit = new MeasurementUnit { Name = "grados Celsius", Symbol = "°C" };
        var humUnit = new MeasurementUnit { Name = "porcentaje", Symbol = "%" };
        context.MeasurementUnits.AddRange(phUnit, ecUnit, tempUnit, humUnit);
        await context.SaveChangesAsync();

        context.Sensors.AddRange(
            new Sensor { NodeId = node.Id, SensorTypeId = phType.Id, MeasurementUnitId = phUnit.Id, Name = "ph-solucion", TechnicalKey = "ph-solucion", IsActive = true, CreatedAt = DateTime.UtcNow },
            new Sensor { NodeId = node.Id, SensorTypeId = ecType.Id, MeasurementUnitId = ecUnit.Id, Name = "ec-solucion", TechnicalKey = "ec-solucion", IsActive = true, CreatedAt = DateTime.UtcNow },
            new Sensor { NodeId = node.Id, SensorTypeId = tempType.Id, MeasurementUnitId = tempUnit.Id, Name = "temp-ambiente", TechnicalKey = "temp-ambiente", IsActive = true, CreatedAt = DateTime.UtcNow },
            new Sensor { NodeId = node.Id, SensorTypeId = humType.Id, MeasurementUnitId = humUnit.Id, Name = "hum-ambiente", TechnicalKey = "hum-ambiente", IsActive = true, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        // Catálogo de reglas (mismo criterio de arranque que DbInitializer).
        context.AnomalyRuleCatalogs.AddRange(
            new AnomalyRuleCatalog { CropTypeId = crop.Id, Code = AnomalyContract.TypePhFueraDeBanda, Name = "pH fuera de banda operativa", SensorTypeName = "pH", ConsecutiveToOpen = 2, CooldownMinutes = 30, IsActive = true, Source = "crop-config" },
            new AnomalyRuleCatalog { CropTypeId = crop.Id, Code = AnomalyContract.TypeCeFueraDeBanda, Name = "CE fuera de banda operativa", SensorTypeName = "CE", ConsecutiveToOpen = 2, CooldownMinutes = 30, IsActive = true, Source = "stage-config" },
            new AnomalyRuleCatalog { CropTypeId = crop.Id, Code = AnomalyContract.TypeTemperaturaFueraDeBanda, Name = "Temperatura ambiente fuera de banda", SensorTypeName = "Temperatura", ConsecutiveToOpen = 2, CooldownMinutes = 30, IsActive = false, Source = "pendiente", Notes = "Sin umbral aprobado" },
            new AnomalyRuleCatalog { CropTypeId = crop.Id, Code = AnomalyContract.TypeHumedadFueraDeBanda, Name = "Humedad ambiente fuera de banda", SensorTypeName = "Humedad", ConsecutiveToOpen = 2, CooldownMinutes = 30, IsActive = false, Source = "pendiente", Notes = "Sin umbral aprobado" });
        await context.SaveChangesAsync();
    }

    // ---------------------------------------------------------------------------
    // Helpers de datos
    // ---------------------------------------------------------------------------

    /// <summary>Lote ACTIVO con la etapa fenológica "Crecimiento vegetativo" aplicada (receta de EC vigente).</summary>
    public static async Task<Lot> CreateActiveLotAsync(HydroPilotDbContext context, string name)
    {
        var crop = await context.CropTypes.FirstAsync();
        var status = await context.LotStatuses.FirstAsync(s => s.Name == "ACTIVO");
        var stage = await context.PhenologicalStages.FirstAsync(s => s.Name == "Crecimiento vegetativo");

        var lot = new Lot
        {
            GreenhouseId = (await context.Greenhouses.FirstAsync()).Id,
            CropTypeId = crop.Id,
            StatusId = status.Id,
            Name = name,
            SowingDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-15)),
            PlantedAreaM2 = 2m,
            GridRows = 4,
            GridColumns = 4,
            AppliedPhenologicalStageId = stage.Id,
            CurrentPh = 6.0m,
            CurrentEc = 1.5m,
        };
        context.Lots.Add(lot);
        await context.SaveChangesAsync();
        return lot;
    }

    public static async Task ChangeLotStatusAsync(HydroPilotDbContext context, Lot lot, string statusName)
    {
        lot.StatusId = (await context.LotStatuses.FirstAsync(s => s.Name == statusName)).Id;
        await context.SaveChangesAsync();
    }

    public static async Task<Plant> AddPlantAsync(HydroPilotDbContext context, Lot lot, int row, int col)
    {
        var stage = await context.CommercialStages.FirstAsync(s => s.Name == "Baby Leaf apta");
        var pheno = await context.PhenologicalStages.FirstAsync(s => s.Name == "Crecimiento vegetativo");
        var plant = new Plant
        {
            LotId = lot.Id,
            Row = row,
            Column = col,
            PhenologicalStageId = pheno.Id,
            CommercialStageId = stage.Id,
            OperationalState = PlantOperationalState.Activa,
            CreatedAtUtc = DateTime.UtcNow,
        };
        context.Plants.Add(plant);
        await context.SaveChangesAsync();
        return plant;
    }

    /// <summary>Inserta una lectura usable (contrato v2) para el sensor/lote indicado.</summary>
    public static async Task AddReadingAsync(
        HydroPilotDbContext context,
        Sensor sensor,
        Lot lot,
        decimal value,
        DateTime observedAtUtc,
        string readingTag,
        string quality = TelemetryContract.QualityValid)
    {
        context.SensorReadings.Add(new SensorReading
        {
            SensorId = sensor.Id,
            NodeId = sensor.NodeId,
            LotId = lot.Id,
            MeasurementUnitId = sensor.MeasurementUnitId,
            Value = value,
            Timestamp = observedAtUtc,
            ObservedAtUtc = observedAtUtc,
            ReceivedAtUtc = DateTime.UtcNow,
            ExternalReadingId = $"{readingTag}-{observedAtUtc.Ticks}",
            Quality = quality,
            IngestionResult = TelemetryContract.ResultAceptada,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    public static async Task<Sensor> GetSensorAsync(HydroPilotDbContext context, string typeName) =>
        await context.Sensors.FirstAsync(s => s.SensorType!.Name == typeName);
}