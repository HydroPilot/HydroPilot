using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;

namespace HydroPilotWeb.Tests;

/// <summary>Colección serializada para los tests que requieren SQL Server local.</summary>
[CollectionDefinition("sql-server")]
public class LotesSqlCollection : ICollectionFixture<LotesSqlFixture>;

/// <summary>
/// Fixture de integración: crea una base SQL Server limpia (drop + migrate) y
/// siembra los catálogos mínimos del dominio. Requiere SQL Server local
/// (SETUP.md) o la cadena en HYDROPILOT_TEST_SQL.
/// </summary>
public sealed class LotesSqlFixture : IAsyncLifetime
{
    public const string DbName = "hydropilot_lotes_test";

    public string ConnectionString { get; }

    public LotesSqlFixture()
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
        await context.Database.MigrateAsync(); // valida la cadena completa sobre base limpia

        await SeedCatalogsAsync(context);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Catálogos mínimos: cultivo, etapas fenológicas, comerciales y Baby Leaf.</summary>
    private static async Task SeedCatalogsAsync(HydroPilotDbContext context)
    {
        var crop = new CropType
        {
            Name = "Lechuga Baby Leaf",
            GddTarget = 300m,
            BaseTemperature = 4.5m,
            OptimalPhMin = 5.8m,
            OptimalPhTarget = 6.0m,
            OptimalPhMax = 6.2m,
            OptimalEcMin = 0.8m,
            OptimalEcMax = 1.8m,
            EstimatedDaysToHarvest = 25,
            YieldPerM2 = 3.0m
        };
        context.CropTypes.Add(crop);
        context.SaveChanges();

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

        context.BabyLeafCriteria.AddRange(
            new BabyLeafCriterion { BabyLeafConfigId = config.Id, Name = "Ventana GDD", DataType = "GDD", Unit = "GDD", ValueMin = 250, ValueMax = 450, Weight = 20, IsMandatory = true },
            new BabyLeafCriterion { BabyLeafConfigId = config.Id, Name = "Morfología / tamaño", DataType = "MORFOLOGIA", Unit = "score", Weight = 45, IsMandatory = true },
            new BabyLeafCriterion { BabyLeafConfigId = config.Id, Name = "GrowthRate", DataType = "CRECIMIENTO", Unit = "%/día", Weight = 20, IsMandatory = false },
            new BabyLeafCriterion { BabyLeafConfigId = config.Id, Name = "Estado visual", DataType = "VISUAL", Unit = "score", Weight = 15, IsMandatory = true });

        // Invernadero + nodo + sensor de temperatura para el cálculo de GDD
        // (GDD climático = base del lote, fuente GddService del módulo forecasting).
        var greenhouse = new Greenhouse { Name = "Invernadero Test", Location = "Test", CreatedAt = DateTime.UtcNow };
        context.Greenhouses.Add(greenhouse);
        context.SaveChanges();

        var node = new IotNode { GreenhouseId = greenhouse.Id, Identifier = "test-node-01", Status = "ACTIVO", CreatedAt = DateTime.UtcNow };
        context.IotNodes.Add(node);

        var tempType = new SensorType { Name = "Temperatura" };
        context.SensorTypes.Add(tempType);
        var unit = new MeasurementUnit { Name = "grados Celsius", Symbol = "°C" };
        context.MeasurementUnits.Add(unit);
        context.SaveChanges();

        context.Sensors.Add(new Sensor
        {
            NodeId = node.Id,
            SensorTypeId = tempType.Id,
            MeasurementUnitId = unit.Id,
            Name = "temp-test-01",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });

        context.SaveChanges();
    }

    /// <summary>Fecha de siembra fija para reproducibilidad del GDD.</summary>
    public static DateOnly SowingDate => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-20));

    /// <summary>Siembra ~14 días de lecturas de temperatura que dan GDD acumulado ~329.</summary>
    public static async Task SeedTemperatureReadingsAsync(HydroPilotDbContext context, int greenhouseId, DateTime startUtc, int days = 14)
    {
        var sensor = await context.Sensors
            .Include(s => s.Node)
            .FirstAsync(s => s.Node!.GreenhouseId == greenhouseId && s.SensorType!.Name == "Temperatura");
        // Incluimos el SensorType por el filtro del GddService (nombre del tipo).

        for (var i = 0; i < days; i++)
        {
            context.SensorReadings.Add(new SensorReading
            {
                SensorId = sensor.Id,
                MeasurementUnitId = sensor.MeasurementUnitId,
                Value = 28m,
                Timestamp = startUtc.AddDays(i).AddHours(12),
                CreatedAt = DateTime.UtcNow
            });
        }
        await context.SaveChangesAsync();
    }
}