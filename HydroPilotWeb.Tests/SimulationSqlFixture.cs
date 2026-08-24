using HydroPilotWeb.Data;
using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.Simulation;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HydroPilotWeb.Tests.Integration;

/// <summary>Colección SQL Server para el módulo de simulación (SIM-01..SIM-10).</summary>
[CollectionDefinition("simulation-sql")]
public sealed class SimulationSqlCollection : ICollectionFixture<SimulationSqlFixture>;

/// <summary>Factory simple sin pooling (el OnConfiguring del contexto lo impide).</summary>
public sealed class SimulationDbContextFactory(DbContextOptions<HydroPilotDbContext> options)
    : IDbContextFactory<HydroPilotDbContext>
{
    public HydroPilotDbContext CreateDbContext() => new(options);

    public Task<HydroPilotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}

/// <summary>
/// Fixture de integración de simulación: base SQL Server limpia (drop + migrate)
/// con el seed completo del DbInitializer (lote demo con plantas y evaluaciones,
/// catálogos Baby Leaf). Provee los servicios reales (GddService, YieldService,
/// LotAggregateService, ClimateScenarioProvider, YieldModel, SimulationService)
/// sobre el mismo motor que Azure SQL.
/// </summary>
public sealed class SimulationSqlFixture : IAsyncLifetime
{
    public const string DatabaseName = "hydropilot_simulation_test";

    public string ConnectionString { get; private set; } = string.Empty;
    public IConfiguration Configuration { get; private set; } = null!;
    public IDbContextFactory<HydroPilotDbContext> Factory { get; private set; } = null!;

    /// <summary>Servicios del módulo compartidos por las pruebas.</summary>
    public GddService Gdd { get; private set; } = null!;
    public YieldService Yield { get; private set; } = null!;
    public SimulationService Simulation { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Configuration = new ConfigurationBuilder()
            .AddUserSecrets<SimulationSqlFixture>(optional: true)
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
        Factory = new SimulationDbContextFactory(options);

        await using var context = Factory.CreateDbContext();
        await context.Database.MigrateAsync(); // valida la cadena de migraciones sobre base limpia
        DbInitializer.Initialize(context, Configuration); // seed completo (catálogos, nodo, lote/plantas demo)

        // Determinismo espacial: el GDD se agrupa por la zona horaria del invernadero.
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

        Gdd = gdd;
        Yield = yield;
        Simulation = new SimulationService(
            Factory,
            gdd,
            new ClimateScenarioProvider(gdd, weather),
            new YieldModel(yield),
            aggregate);
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