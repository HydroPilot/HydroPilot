using HydroPilotWeb.Data;
using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Notifications;
using HydroPilotWeb.Services.SensorFaults;
using HydroPilotWeb.Services.SimulationEnvironment;
using HydroPilotWeb.Tests.Integration;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Regenerar el historial con la app corriendo (Admin > Mantenimiento): el historial se carga con sus Id, así que lo que
/// agreguen los barridos de fondo en ese rato chocaba ("PK_NotificationAlerts ... duplicate key (1)": el barrido de
/// errores de sensores veía las lecturas borradas y abría "sin datos" con su alerta). Base propia
/// (hydropilot_sim_regen_test), 1 mes de historial.
/// </summary>
public class SimulationRegenerationSqlServerTests
{
    private const string DbName = "hydropilot_sim_regen_test";

    [Fact]
    public async Task Regenerar_con_los_barridos_corriendo_no_choca_con_lo_que_agregan()
    {
        var env = Environment.GetEnvironmentVariable("HYDROPILOT_TEST_SQL")
                  ?? $"Server=localhost,1433;Database={DbName};User Id=sa;Password=GuardamosCositas0!;TrustServerCertificate=True";
        var connectionString = new SqlConnectionStringBuilder(env) { InitialCatalog = DbName }.ConnectionString;
        var options = new DbContextOptionsBuilder<HydroPilotDbContext>().UseSqlServer(connectionString).Options;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:Password"] = "PassDeTestNoProductiva",
                ["HydroPilot:UseLocalDb"] = "true",
            })
            .Build();

        await using (var context = new HydroPilotDbContext(options))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.MigrateAsync();
            DbInitializer.Initialize(context, configuration);
        }

        var services = new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<IDbContextFactory<HydroPilotDbContext>>(new ForecastDbContextFactory(options))
            .AddSingleton(new HttpClient())
            .AddScoped<SettingsService>()
            .AddScoped<WeatherService>()
            .AddScoped<GddService>()
            .AddScoped<YieldService>()
            .AddScoped<ForecastService>()
            .AddScoped<NotificationService>()
            .AddScoped<SensorFaultService>()
            .AddSingleton(Options.Create(new TelemetryOptions()))
            .AddSingleton(Options.Create(new SimulationEnvironmentOptions { Enabled = true, HistoryMonths = 1 }))
            .AddSingleton<SimulationEnvironmentService>()
            .BuildServiceProvider();
        try
        {
            var simulation = services.GetRequiredService<SimulationEnvironmentService>();
            var first = await simulation.RegenerateAsync();
            Assert.True(first.Succeeded, first.Message);

            // El barrido de errores de sensores, como lo corre su servicio de fondo pero sin esperar entre vueltas: con la
            // Pi recién conectada (fin del historial) y las lecturas borradas, abre "sin datos" con su alerta.
            using var stop = new CancellationTokenSource();
            var sweeps = 0;
            var skipped = 0;
            var sweeper = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    using (var lease = RegenerationPause.TryEnter())
                    {
                        if (lease is null)
                        {
                            Interlocked.Increment(ref skipped);
                        }
                        else
                        {
                            await using var scope = services.CreateAsyncScope();
                            await scope.ServiceProvider.GetRequiredService<SensorFaultService>().SweepAsync(DateTime.UtcNow);
                            Interlocked.Increment(ref sweeps);
                        }
                    }
                    await Task.Delay(50);
                }
            });

            var second = await simulation.RegenerateAsync();
            Assert.True(second.Succeeded, second.Message);
            Assert.True(Volatile.Read(ref skipped) > 0, "El barrido no se salteó durante la regeneración.");

            // Al terminar, los barridos vuelven a correr.
            var sweepsAtEnd = Volatile.Read(ref sweeps);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Volatile.Read(ref sweeps) == sweepsAtEnd && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            stop.Cancel();
            await sweeper;
            Assert.True(sweeps > sweepsAtEnd, "El barrido no volvió a correr después de regenerar.");
            Assert.False(RegenerationPause.Paused);

            await using var check = new HydroPilotDbContext(options);
            var alerts = await check.NotificationAlerts.CountAsync();
            Assert.True(alerts > 0);
            Assert.Equal(alerts, await check.NotificationAlerts.Select(a => a.Id).Distinct().CountAsync());
        }
        finally
        {
            await services.DisposeAsync();
            SqlConnection.ClearAllPools();
            await using var drop = new HydroPilotDbContext(options);
            await drop.Database.EnsureDeletedAsync();
        }
    }
}
