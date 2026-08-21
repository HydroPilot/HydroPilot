using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace HydroPilotWeb.Data;

/// <summary>
/// Fábrica design-time para `dotnet ef`. Usa la cadena LocalSqlServer de
/// user-secrets (o appsettings) — no se hardcodea ningún secreto en el repo.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<HydroPilotDbContext>
{
    public HydroPilotDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddUserSecrets<DesignTimeDbContextFactory>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("LocalSqlServer")
            ?? throw new InvalidOperationException(
                "Falta 'ConnectionStrings:LocalSqlServer' para el entorno design-time de EF Core.");

        var options = new DbContextOptionsBuilder<HydroPilotDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new HydroPilotDbContext(options);
    }
}