using HydroPilotWeb.Data;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Colección serializada para los tests del dashboard que requieren SQL Server
/// local (misma base dedicada, distinta de los fixtures de lotes e IoT).
/// </summary>
[CollectionDefinition("sql-server-dashboard")]
public class DashboardSqlCollection : ICollectionFixture<DashboardSqlFixture>;

/// <summary>
/// Fixture de integración del dashboard: base SQL Server limpia (drop + migrate
/// reales). El dashboard es principalmente de consulta; no se agregan migraciones.
/// </summary>
public sealed class DashboardSqlFixture : IAsyncLifetime
{
    public const string DbName = "hydropilot_dashboard_test";

    public string ConnectionString { get; }

    public DashboardSqlFixture()
    {
        var env = Environment.GetEnvironmentVariable("HYDROPILOT_TEST_SQL");
        ConnectionString = env
            ?? $"Server=localhost,1433;Database={DbName};User Id=sa;Password=GuardamosCositas0!;TrustServerCertificate=True";
    }

    public HydroPilotDbContext NewContext() => new(
        new DbContextOptionsBuilder<HydroPilotDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var context = NewContext();
        await context.Database.EnsureDeletedAsync();
        await context.Database.MigrateAsync(); // valida la cadena real de migraciones
    }

    public Task DisposeAsync() => Task.CompletedTask;
}