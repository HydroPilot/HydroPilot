using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Control;
using HydroPilotWeb.Services.Dashboard;
using HydroPilotWeb.Services.Forecasting;

namespace HydroPilotWeb.Tests;

[CollectionDefinition("control-sql")]
public class ControlSqlCollection : ICollectionFixture<ControlSqlFixture>;

/// <summary>Sink de prueba: graba las pausas y reanudaciones que el control manda a Notificaciones.</summary>
public sealed class RecordingControlSink : IControlEventSink
{
    private readonly List<ControlPauseEvent> _paused = [];
    private readonly List<(int NodeId, string Parameter)> _resumed = [];

    public IReadOnlyList<ControlPauseEvent> Paused { get { lock (_paused) return _paused.ToList(); } }
    public IReadOnlyList<(int NodeId, string Parameter)> Resumed { get { lock (_resumed) return _resumed.ToList(); } }

    public Task OnPausedAsync(ControlPauseEvent pause, CancellationToken ct = default)
    {
        lock (_paused) _paused.Add(pause);
        return Task.CompletedTask;
    }

    public Task OnResumedAsync(int nodeId, string parameter, CancellationToken ct = default)
    {
        lock (_resumed) _resumed.Add((nodeId, parameter));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Fixture del control de pH/CE: base SQL limpia (drop + migrate con las MIGRACIONES REALES), un cultivo con
/// pH 5,5–6,5 (obj. 6,0), la etapa "Crecimiento vegetativo" (CE 1,2–1,8, obj. 1,5) aplicada a un lote activo,
/// un nodo asignado al lote con sus 4 bombas (seed real) y lecturas recientes de pH 6,8 y CE 1,48.
/// Requiere SQL Server local (SETUP.md) o HYDROPILOT_TEST_SQL.
/// </summary>
public sealed class ControlSqlFixture : IAsyncLifetime
{
    public const string DbName = "hydropilot_control_test";
    public const string NodeIdentifier = "ctl-node-01";
    public const string PhReadingExternalId = "ctl-ph-2";

    public string ConnectionString { get; }
    public IDbContextFactory<HydroPilotDbContext> Factory { get; private set; } = null!;
    public ControlService Control { get; private set; } = null!;
    public ControlOptions Options { get; } = new();

    public int NodeId { get; private set; }
    public int LotId { get; private set; }
    public int VegetativeStageId { get; private set; }
    public int MaturityStageId { get; private set; }
    public int PhReadingId { get; private set; }
    public int SpareNodeId { get; private set; }
    public int SparePhSensorId { get; private set; }
    public RecordingControlSink Sink { get; } = new();

    public ControlSqlFixture()
    {
        var env = Environment.GetEnvironmentVariable("HYDROPILOT_TEST_SQL");
        ConnectionString = env
            ?? $"Server=localhost,1433;Database={DbName};User Id=sa;Password=GuardamosCositas0!;TrustServerCertificate=True";
    }

    public HydroPilotDbContext NewContext() => new(
        new DbContextOptionsBuilder<HydroPilotDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using (var dropContext = NewContext())
            await dropContext.Database.EnsureDeletedAsync();

        await using var context = NewContext();
        await context.Database.MigrateAsync();
        await SeedAsync(context);

        Factory = new SimpleFactory(new DbContextOptionsBuilder<HydroPilotDbContext>().UseSqlServer(ConnectionString).Options);
        Control = new ControlService(
            Factory,
            Microsoft.Extensions.Options.Options.Create(Options),
            Microsoft.Extensions.Options.Options.Create(new DashboardOptions()),
            Sink,
            new HttpPollingCommandChannel(),
            NullLogger<ControlService>.Instance);
    }

    /// <summary>Otro ControlService sobre la misma base, con el transporte que se le pase (IoT Hub en los tests).</summary>
    public ControlService NewControl(ICommandChannel channel) => new(
        Factory,
        Microsoft.Extensions.Options.Options.Create(Options),
        Microsoft.Extensions.Options.Options.Create(new DashboardOptions()),
        Sink,
        channel,
        NullLogger<ControlService>.Instance);

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Cada test arranca sin comandos (el índice permite una sola dosis en curso por bomba).</summary>
    public async Task ResetCommandsAsync()
    {
        await using var context = NewContext();
        await context.ActuatorCommands.ExecuteDeleteAsync();
        await context.ControlPauses.ExecuteDeleteAsync();
    }

    public async Task<int> ActuatorIdAsync(string kind, int? nodeId = null)
    {
        await using var context = NewContext();
        var node = nodeId ?? NodeId;
        return await context.Actuators.Where(a => a.NodeId == node && a.Kind == kind).Select(a => a.Id).SingleAsync();
    }

    private async Task SeedAsync(HydroPilotDbContext context)
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
        };
        context.CropTypes.Add(crop);
        context.LotStatuses.AddRange(
            new LotStatus { Name = LotStatusNames.Activo },
            new LotStatus { Name = LotStatusNames.Cosechado });
        await context.SaveChangesAsync();

        var vegetative = new PhenologicalStage { CropTypeId = crop.Id, Name = "Crecimiento vegetativo", Order = 2, GddMin = 150, GddMax = 450, EcMin = 1.2m, EcObjective = 1.5m, EcMax = 1.8m };
        var maturity = new PhenologicalStage { CropTypeId = crop.Id, Name = "Formación y madurez", Order = 3, GddMin = 450, GddMax = 750, EcMin = 1.5m, EcObjective = 1.7m, EcMax = 1.8m };
        context.PhenologicalStages.AddRange(vegetative, maturity);

        var greenhouse = new Greenhouse { Name = "Invernadero Control Test", Location = "Test", TimeZoneId = "UTC", CreatedAt = DateTime.UtcNow };
        context.Greenhouses.Add(greenhouse);
        context.SensorTypes.AddRange(new SensorType { Name = "pH" }, new SensorType { Name = "CE" });
        context.MeasurementUnits.AddRange(
            new MeasurementUnit { Name = "pH", Symbol = "pH" },
            new MeasurementUnit { Name = "milisiemens por centímetro", Symbol = "mS/cm" });
        await context.SaveChangesAsync();

        var node = new IotNode { GreenhouseId = greenhouse.Id, Identifier = NodeIdentifier, Status = "ACTIVO", CreatedAt = DateTime.UtcNow };
        // Un segundo nodo sin lote: el seed le da sus bombas y no tiene que mezclarse con el primero.
        var spare = new IotNode { GreenhouseId = greenhouse.Id, Identifier = "ctl-node-02", Status = "ACTIVO", CreatedAt = DateTime.UtcNow };
        context.IotNodes.AddRange(node, spare);
        await context.SaveChangesAsync();

        var types = context.SensorTypes.ToDictionary(t => t.Name);
        var units = context.MeasurementUnits.ToDictionary(u => u.Name);
        var phSensor = new Sensor { NodeId = node.Id, SensorTypeId = types["pH"].Id, MeasurementUnitId = units["pH"].Id, Name = "ctl-ph", TechnicalKey = "ph-solucion", IsActive = true, CreatedAt = DateTime.UtcNow };
        var ecSensor = new Sensor { NodeId = node.Id, SensorTypeId = types["CE"].Id, MeasurementUnitId = units["milisiemens por centímetro"].Id, Name = "ctl-ec", TechnicalKey = "ec-solucion", IsActive = true, CreatedAt = DateTime.UtcNow };
        // El nodo de repuesto tiene su sonda de pH para medir efectos y pausas sin tocar el tanque del primero.
        var sparePh = new Sensor { NodeId = spare.Id, SensorTypeId = types["pH"].Id, MeasurementUnitId = units["pH"].Id, Name = "ctl2-ph", TechnicalKey = "ph-solucion", IsActive = true, CreatedAt = DateTime.UtcNow };
        context.Sensors.AddRange(phSensor, ecSensor, sparePh);

        var lot = new Lot
        {
            GreenhouseId = greenhouse.Id,
            CropTypeId = crop.Id,
            StatusId = context.LotStatuses.Single(s => s.Name == LotStatusNames.Activo).Id,
            Name = "Lote Control",
            SowingDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-12),
            AppliedPhenologicalStageId = vegetative.Id,
        };
        context.Lots.Add(lot);
        await context.SaveChangesAsync();

        context.NodeLotAssignments.Add(new NodeLotAssignment
        {
            NodeId = node.Id,
            LotId = lot.Id,
            ValidFromUtc = DateTime.UtcNow.AddDays(-30),
            Source = "fixture",
            CreatedAt = DateTime.UtcNow,
        });

        // Dos lecturas recientes y consistentes: pH alto (firme) y CE dentro del rango.
        var now = DateTime.UtcNow;
        context.SensorReadings.AddRange(
            Reading(phSensor, lot.Id, 6.80m, now.AddMinutes(-2), "ctl-ph-1"),
            Reading(phSensor, lot.Id, 6.82m, now.AddMinutes(-1), PhReadingExternalId),
            Reading(ecSensor, lot.Id, 1.47m, now.AddMinutes(-2), "ctl-ec-1"),
            Reading(ecSensor, lot.Id, 1.48m, now.AddMinutes(-1), "ctl-ec-2"));
        await context.SaveChangesAsync();

        DbInitializer.SeedActuators(context);

        NodeId = node.Id;
        SpareNodeId = spare.Id;
        SparePhSensorId = sparePh.Id;
        LotId = lot.Id;
        VegetativeStageId = vegetative.Id;
        MaturityStageId = maturity.Id;
        PhReadingId = context.SensorReadings.Single(r => r.ExternalReadingId == PhReadingExternalId).Id;
    }

    private static SensorReading Reading(Sensor sensor, int lotId, decimal value, DateTime observedAt, string externalId) => new()
    {
        SensorId = sensor.Id,
        NodeId = sensor.NodeId,
        LotId = lotId,
        MeasurementUnitId = sensor.MeasurementUnitId,
        Value = value,
        Timestamp = observedAt,
        ObservedAtUtc = observedAt,
        ReceivedAtUtc = observedAt,
        ExternalReadingId = externalId,
        Quality = TelemetryContract.QualityValid,
        IngestionResult = TelemetryContract.ResultAceptada,
        CreatedAt = DateTime.UtcNow,
    };

    private sealed class SimpleFactory : IDbContextFactory<HydroPilotDbContext>
    {
        private readonly DbContextOptions<HydroPilotDbContext> _options;
        public SimpleFactory(DbContextOptions<HydroPilotDbContext> options) => _options = options;
        public HydroPilotDbContext CreateDbContext() => new(_options);
    }
}
