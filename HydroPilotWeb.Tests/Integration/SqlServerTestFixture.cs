using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HydroPilotWeb.Tests.Integration;

/// <summary>
/// Fixture de integración sobre SQL Server local (que es el mismo motor que Azure SQL).
/// Crea una base de prueba (drop + create), aplica las MIGRACIONES REALES y corre el
/// DbInitializer (catálogos + nodo demo + asignación fixture). La base se elimina al
/// terminar la colección de pruebas. Los secretos de conexión se leen de user-secrets.
/// </summary>
[CollectionDefinition("sqlserver")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerTestFixture>;

/// <summary>Factory simple sin pooling (el OnConfiguring del contexto lo impide).</summary>
public sealed class SimpleDbContextFactory : IDbContextFactory<HydroPilotDbContext>
{
    private readonly DbContextOptions<HydroPilotDbContext> _options;

    public SimpleDbContextFactory(DbContextOptions<HydroPilotDbContext> options)
    {
        _options = options;
    }

    public HydroPilotDbContext CreateDbContext() => new(_options);
}

public sealed class SqlServerTestFixture : IAsyncLifetime
{
    public const string DatabaseName = "hydropilot_test_iot";

    public string ConnectionString { get; private set; } = string.Empty;
    public IConfiguration Configuration { get; private set; } = null!;
    public IDbContextFactory<HydroPilotDbContext> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Configuration = new ConfigurationBuilder()
            .AddUserSecrets<SqlServerTestFixture>(optional: true)
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
        // Factory simple: el OnConfiguring del contexto (ConfigureWarnings) no es
        // compatible con pooling de contextos.
        Factory = new SimpleDbContextFactory(options);

        await using var context = Factory.CreateDbContext();
        await context.Database.MigrateAsync();
        DbInitializer.Initialize(context, Configuration);
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

/// <summary>Helpers compartidos: builder de envelope, limpieza de datos y servicios.</summary>
public static class IntegrationTestHelpers
{
    /// <summary>"Ahora" del fixture: se evalúa en vivo para que las reglas de frescura
    /// (STALE/FUTURE) se comparen contra el reloj real del servidor de pruebas.</summary>
    public static DateTime NowUtc => DateTime.UtcNow;

    public static ParsedEnvelope Envelope(string batchId, params ParsedReading[] readings) =>
        new("2", batchId, "rpi-inv-01", null, NowUtc, "1.0.0", readings);

    public static ParsedReading R(string id, string sensorRef, decimal value, DateTime? observedAt = null,
        string? unit = null, string? quality = null) =>
        new(id, sensorRef, observedAt ?? NowUtc, value, unit, quality);

    /// <summary>Limpia lecturas/batches/rechazos y el estado de conexión de los nodos
    /// (para que cada prueba parta de NEVER_CONNECTED sin telemetría).</summary>
    public static async Task ResetTelemetryAsync(HydroPilotDbContext context)
    {
        context.SensorReadings.RemoveRange(context.SensorReadings);
        context.TelemetryBatches.RemoveRange(context.TelemetryBatches);
        context.TelemetryRejections.RemoveRange(context.TelemetryRejections);

        foreach (var node in context.IotNodes)
        {
            node.ConnectionState = TelemetryContract.ConnectionNeverConnected;
            node.LastConnection = null;
            node.LastAcceptedAt = null;
            node.LastRejectedAt = null;
        }

        await context.SaveChangesAsync();
    }

    public static async Task<int> NodeIdAsync(HydroPilotDbContext context, string identifier = "rpi-inv-01")
    {
        var node = await context.IotNodes.FirstAsync(n => n.Identifier == identifier);
        return node.Id;
    }
}