using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Control;
using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Control;
using HydroPilotWeb.Services.Dashboard;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.SimulationEnvironment;
using HydroPilotWeb.Tests.Integration;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Entorno de simulación contra SQL Server: base propia (hydropilot_sim_test, migraciones reales + seed), historial de
/// 5 meses cargado con SqlBulkCopy y leído por los servicios reales. Requiere SQL Server (HYDROPILOT_TEST_SQL o el
/// default de los fixtures).
/// </summary>
public sealed class SimulationEnvironmentSqlFixture : IAsyncLifetime
{
    public const string DbName = "hydropilot_sim_test";

    public string ConnectionString { get; }
    public IDbContextFactory<HydroPilotDbContext> Factory { get; private set; } = null!;
    public ServiceProvider Services { get; private set; } = null!;
    public SimulationEnvironmentResult Result { get; private set; } = null!;
    public DateTime NowUtc { get; private set; }

    public SimulationEnvironmentSqlFixture()
    {
        var env = Environment.GetEnvironmentVariable("HYDROPILOT_TEST_SQL")
                  ?? $"Server=localhost,1433;Database={DbName};User Id=sa;Password=GuardamosCositas0!;TrustServerCertificate=True";
        ConnectionString = new SqlConnectionStringBuilder(env) { InitialCatalog = DbName }.ConnectionString;
    }

    public HydroPilotDbContext NewContext() => new(
        new DbContextOptionsBuilder<HydroPilotDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using (var drop = NewContext())
            await drop.Database.EnsureDeletedAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:Password"] = "PassDeTestNoProductiva",
                ["HydroPilot:UseLocalDb"] = "true",
            })
            .Build();

        Factory = new ForecastDbContextFactory(new DbContextOptionsBuilder<HydroPilotDbContext>().UseSqlServer(ConnectionString).Options);
        await using (var context = NewContext())
        {
            await context.Database.MigrateAsync();
            DbInitializer.Initialize(context, configuration);
        }

        var services = new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton(Factory)
            .AddSingleton(new HttpClient())
            .AddScoped<SettingsService>()
            .AddScoped<WeatherService>()
            .AddScoped<GddService>()
            .AddScoped<YieldService>()
            .AddScoped<ForecastService>()
            .AddSingleton(Options.Create(new SimulationEnvironmentOptions { Enabled = true, HistoryMonths = 5 }))
            .AddSingleton<SimulationEnvironmentService>();
        Services = services.BuildServiceProvider();

        NowUtc = DateTime.UtcNow;
        Result = await Services.GetRequiredService<SimulationEnvironmentService>().RegenerateAsync();
    }

    public async Task DisposeAsync()
    {
        await Services.DisposeAsync();
        SqlConnection.ClearAllPools();
        await using var drop = NewContext();
        await drop.Database.EnsureDeletedAsync(); // ~100 MB de historial: no queda ocupando LocalDB
    }
}

[CollectionDefinition("simulation-env-sql")]
public class SimulationEnvironmentSqlCollection : ICollectionFixture<SimulationEnvironmentSqlFixture>;

[Collection("simulation-env-sql")]
public class SimulationEnvironmentSqlServerTests
{
    private readonly SimulationEnvironmentSqlFixture _fx;

    public SimulationEnvironmentSqlServerTests(SimulationEnvironmentSqlFixture fx) => _fx = fx;

    [Fact]
    public async Task El_historial_llena_todas_las_tablas_de_lotes()
    {
        Assert.True(_fx.Result.Succeeded, _fx.Result.Message);
        await using var context = _fx.NewContext();

        var counts = new Dictionary<string, int>
        {
            ["Lots"] = await context.Lots.CountAsync(),
            ["Plants"] = await context.Plants.CountAsync(),
            ["PlantStageHistories"] = await context.PlantStageHistories.CountAsync(),
            ["PlantImages"] = await context.PlantImages.CountAsync(),
            ["PlantImageAnalyses"] = await context.PlantImageAnalyses.CountAsync(),
            ["BabyLeafEvaluations"] = await context.BabyLeafEvaluations.CountAsync(),
            ["Predictions"] = await context.Predictions.CountAsync(),
            ["CircuitAssignments"] = await context.CircuitAssignments.CountAsync(),
            ["SensorReadings"] = await context.SensorReadings.CountAsync(),
            ["TelemetryBatches"] = await context.TelemetryBatches.CountAsync(),
            ["TelemetryRejections"] = await context.TelemetryRejections.CountAsync(),
            ["ActuatorCommands"] = await context.ActuatorCommands.CountAsync(),
            ["LotSetpointHistories"] = await context.LotSetpointHistories.CountAsync(),
            ["LotRecipes"] = await context.LotRecipes.CountAsync(),
            ["ControlPauses"] = await context.ControlPauses.CountAsync(),
            ["AnomalyEvents"] = await context.AnomalyEvents.CountAsync(),
            ["NotificationAlerts"] = await context.NotificationAlerts.CountAsync(),
            ["NotificationDeliveries"] = await context.NotificationDeliveries.CountAsync(),
            ["NotificationAttempts"] = await context.NotificationAttempts.CountAsync(),
            ["DailyWeatherForecasts"] = await context.DailyWeatherForecasts.CountAsync(),
            ["WeatherRecords"] = await context.WeatherRecords.CountAsync(),
            ["CostPriceCatalogs"] = await context.CostPriceCatalogs.CountAsync(),
        };
        Assert.All(counts, kv => Assert.True(kv.Value > 0, $"{kv.Key} quedó vacía"));

        // Las claves foráneas se apagan durante la carga: ninguna fila quedó colgada.
        var connection = (SqlConnection)context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "DBCC CHECKCONSTRAINTS WITH ALL_CONSTRAINTS, NO_INFOMSGS";
            check.CommandTimeout = 600;
            await using var reader = await check.ExecuteReaderAsync();
            var broken = new List<string>();
            while (await reader.ReadAsync())
                broken.Add($"{reader.GetString(0)} · {reader.GetString(1)} · {reader.GetString(2)}");
            Assert.True(broken.Count == 0, string.Join(Environment.NewLine, broken.Take(10)));
        }

        // Dos mesadas densas (circuitos de la misma Pi) con su lote en curso (puede haber otro con sus adultas en las
        // mesadas de Adultas); el lote demo del seed ya no está.
        Assert.False(await context.Lots.AnyAsync(l => l.Name == "Lote Demo 01"));
        var circuits = await context.Circuits.Where(c => c.IsActive && c.Kind == CircuitKinds.Dense).OrderBy(c => c.Id).ToListAsync();
        Assert.Equal(2, circuits.Count);
        Assert.Equal(5, await context.Circuits.CountAsync(c => c.IsActive && c.Kind == CircuitKinds.Adult));
        Assert.All(circuits, c => Assert.Equal(ControlContract.ModeAuto, c.ControlMode));
        var open = await context.CircuitAssignments.Where(a => a.ValidUntilUtc == null).ToListAsync();
        Assert.All(circuits, c => Assert.Single(open, a => a.CircuitId == c.Id));
        Assert.All(await context.Lots.Where(l => l.Status!.Name == LotStatusNames.Activo).ToListAsync(),
            l => Assert.Contains(l.CircuitId, circuits.Select(c => (int?)c.Id)));
        // Trasplante: las adultas están en circuitos de Adultas, cada una en su hueco.
        var adults = await context.Plants.Where(p => p.TransplantedOn != null).Select(p => p.Circuit!.Kind).ToListAsync();
        Assert.NotEmpty(adults);
        Assert.All(adults, kind => Assert.Equal(CircuitKinds.Adult, kind));
        Assert.True(await context.Lots.AnyAsync(l => l.BabyLeafPeakGdd != null));
        // Temperatura y humedad del invernadero: sin circuito.
        Assert.Equal(2, await context.Sensors.CountAsync(s => s.CircuitId == null && s.IsActive));
    }

    [Fact]
    public async Task Lotes_y_la_receta_reales_leen_el_mismo_historial()
    {
        await using var context = _fx.NewContext();
        var crop = await context.CropTypes.Select(c => c.Id).FirstAsync();
        var closed = await context.Lots.CountAsync(l => l.Status!.Name == LotStatusNames.Cosechado || l.Status!.Name == LotStatusNames.Descartado);

        using var scope = _fx.Services.CreateScope();
        var outlook = new BabyLeafOutlookService(_fx.Factory, scope.ServiceProvider.GetRequiredService<GddService>());
        var outcomes = await outlook.GetCycleOutcomesAsync(crop);
        Assert.Equal(closed, outcomes.Count);
        Assert.All(outcomes.Where(o => o.HarvestedBabyLeaf > 0), o => Assert.InRange(o.AptRate!.Value, 0.05m, 1m));
        Assert.All(outcomes, o => Assert.InRange(o.AverageDailyGdd!.Value, 8m, 25m));

        // La receta del lote en curso, recalculada por RecipeService con la base, da lo mismo que dejó el historial.
        var active = await context.Lots.Where(l => l.Status!.Name == LotStatusNames.Activo).OrderBy(l => l.SowingDate).FirstAsync();
        var seeded = await context.LotRecipes.AsNoTracking().SingleAsync(r => r.LotId == active.Id);
        var recipes = new RecipeService(_fx.Factory, outlook, NullLogger<RecipeService>.Instance);
        var fresh = await recipes.RefreshLotAsync(active.Id, DateTime.UtcNow);
        // Puede diferir un paso: el GDD del último día de cada ciclo la web lo cuenta hasta la medianoche UTC y el
        // historial hasta el cierre del lote, y eso mueve un poco el peso por clima.
        Assert.InRange(fresh!.PhOffset, seeded.PhOffset - 0.05m, seeded.PhOffset + 0.05m);
        Assert.InRange(fresh.EcOffset, seeded.EcOffset - 0.05m, seeded.EcOffset + 0.05m);
        Assert.InRange(fresh.SimilarLots, seeded.SimilarLots - 1, seeded.SimilarLots + 1);
    }

    [Fact]
    public async Task Control_y_pronostico_funcionan_sobre_el_historial()
    {
        await using var context = _fx.NewContext();
        var circuit = await context.Circuits.OrderBy(c => c.Id).FirstAsync();

        var control = new ControlService(_fx.Factory, Options.Create(new ControlOptions()), Options.Create(new DashboardOptions()),
            new NoopControlEventSink(), new HttpPollingCommandChannel(), NullLogger<ControlService>.Instance);
        var view = await control.GetViewAsync(circuit.Id);
        Assert.NotNull(view);
        Assert.NotNull(view!.LotId);
        Assert.NotNull(view.Recipe);
        Assert.True(view.Ph.Band.IsDefined && view.Ec.Band.IsDefined);
        Assert.Equal(4, view.Actuators.Count);
        Assert.Contains(view.Actuators, a => a.EffectSamples > 0); // las bombas se calibraron con el historial

        // Tendencia de las últimas 24 h: lecturas promediadas, banda del lote y de acción, dosis del motor.
        var trend = await control.GetTrendAsync(circuit.Id, 24);
        Assert.NotNull(trend);
        Assert.InRange(trend!.Ph.Points.Count, 50, ControlService.TrendMaxPoints + 1);
        Assert.True(trend.Ph.ActionBand.Max < trend.Ph.Band.Max && trend.Ph.ActionBand.Min > trend.Ph.Band.Min);
        Assert.All(trend.Ph.Doses.Concat(trend.Ec.Doses), d => Assert.True(d.AtUtc >= trend.FromUtc));

        // Pronósticos guardados de cada lote (ForecastService a distintas fechas).
        var lots = await context.Lots.Select(l => l.Id).ToListAsync();
        foreach (var lot in lots)
            Assert.True(await context.Predictions.AnyAsync(p => p.LotId == lot), $"lote {lot} sin pronóstico");

        // Rendimiento por historial (≥ 2 cosechados) y GDD del lote en curso.
        using var scope = _fx.Services.CreateScope();
        var forecast = await scope.ServiceProvider.GetRequiredService<ForecastService>().GetForecastAsync(view.LotId!.Value);
        Assert.NotNull(forecast);
        Assert.True(forecast!.GddAccumulated > 100m);
        Assert.True(forecast.Yield.HistoryCycles >= 2);
    }
}
