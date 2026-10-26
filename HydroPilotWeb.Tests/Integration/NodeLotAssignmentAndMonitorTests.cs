using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Notifications;
using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace HydroPilotWeb.Tests.Integration;

[Collection("sqlserver")]
public class NodeLotAssignmentAndMonitorTests
{
    private readonly SqlServerTestFixture _fixture;

    public NodeLotAssignmentAndMonitorTests(SqlServerTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Monitoreo_recalcula_estados_de_conexion()
    {
        await using var reset = _fixture.Factory.CreateDbContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(reset);

        // Nodo con telemetría aceptada hace 20 minutos: con intervalo 300s y factor 2
        // (600s), 20 minutos (1200s) deben quedar OFFLINE tras recalcular.
        var node = await reset.IotNodes.FirstAsync(n => n.Identifier == "rpi-inv-01");
        node.LastAcceptedAt = DateTime.UtcNow.AddMinutes(-20);
        node.ConnectionState = TelemetryContract.ConnectionOnline;
        await reset.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<HydroPilotDbContext>(o => o.UseSqlServer(_fixture.ConnectionString));
        services.AddOptions<TelemetryOptions>().Configure(o =>
        {
            o.MonitorIntervalSeconds = 10;
            o.DegradedIntervalFactor = 2;
        });
        var provider = services.BuildServiceProvider();

        var monitor = new NodeConnectionMonitorHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptionsMonitor<TelemetryOptions>>(),
            provider.GetRequiredService<ILogger<NodeConnectionMonitorHostedService>>());

        await monitor.RunOnceAsync(default);

        await using var verify = _fixture.Factory.CreateDbContext();
        var updated = await verify.IotNodes.FirstAsync(n => n.Identifier == "rpi-inv-01");
        Assert.Equal(TelemetryContract.ConnectionOffline, updated.ConnectionState);

        // Con telemetría reciente, vuelve a ONLINE.
        updated.LastAcceptedAt = DateTime.UtcNow.AddSeconds(-30);
        await verify.SaveChangesAsync();
        await monitor.RunOnceAsync(default);

        await using var check = _fixture.Factory.CreateDbContext();
        Assert.Equal(TelemetryContract.ConnectionOnline,
            (await check.IotNodes.FirstAsync(n => n.Identifier == "rpi-inv-01")).ConnectionState);
    }

    [Fact]
    public async Task Nodo_desconectado_genera_una_alerta_que_se_resuelve_al_volver()
    {
        await using var reset = _fixture.Factory.CreateDbContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(reset);
        await IntegrationTestHelpers.EnsureNeverConnectedNodeAsync(reset);
        var node = await reset.IotNodes.FirstAsync(n => n.Identifier == "rpi-inv-01");
        var prefix = NodeConnectionAlerts.Prefix(node.Id);
        reset.NotificationAlerts.RemoveRange(reset.NotificationAlerts.Where(a => a.Fingerprint.StartsWith(prefix)));
        node.LastAcceptedAt = DateTime.UtcNow.AddMinutes(-20);
        node.ConnectionState = TelemetryContract.ConnectionOnline;
        await reset.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<HydroPilotDbContext>(o => o.UseSqlServer(_fixture.ConnectionString));
        services.AddSingleton(_fixture.Factory);
        services.AddScoped<NotificationService>();
        services.AddOptions<TelemetryOptions>().Configure(o =>
        {
            o.MonitorIntervalSeconds = 10;
            o.DegradedIntervalFactor = 2;
        });
        var provider = services.BuildServiceProvider();
        var monitor = new NodeConnectionMonitorHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptionsMonitor<TelemetryOptions>>(),
            provider.GetRequiredService<ILogger<NodeConnectionMonitorHostedService>>());

        try
        {
            // Pasa a OFFLINE: una alerta Alta "Nodo rpi-inv-01 desconectado"; la vuelta siguiente no la repite.
            await monitor.RunOnceAsync(default);
            await monitor.RunOnceAsync(default);
            await using (var check = _fixture.Factory.CreateDbContext())
            {
                var alert = await check.NotificationAlerts.SingleAsync(a => a.Fingerprint.StartsWith(prefix));
                Assert.Equal(NotificationTypes.NodoDesconectado, alert.Type);
                Assert.Equal(NotificationSeverities.Alta, alert.Severity);
                Assert.Equal("Nodo rpi-inv-01 desconectado", alert.Title);
                Assert.False(alert.IsResolved);
            }

            // Vuelven las lecturas: la alerta se resuelve sola.
            await using (var back = _fixture.Factory.CreateDbContext())
            {
                var updated = await back.IotNodes.FirstAsync(n => n.Id == node.Id);
                updated.LastAcceptedAt = DateTime.UtcNow.AddSeconds(-30);
                await back.SaveChangesAsync();
            }
            await monitor.RunOnceAsync(default);
            await using (var check = _fixture.Factory.CreateDbContext())
                Assert.True((await check.NotificationAlerts.SingleAsync(a => a.Fingerprint.StartsWith(prefix))).IsResolved);

            // Un nodo que nunca se conectó no genera alerta.
            await using (var check = _fixture.Factory.CreateDbContext())
            {
                var never = await check.IotNodes.FirstAsync(n => n.Identifier == "rpi-inv-02");
                Assert.False(await check.NotificationAlerts.AnyAsync(a => a.Fingerprint.StartsWith(NodeConnectionAlerts.Prefix(never.Id))));
            }
        }
        finally
        {
            await using var cleanup = _fixture.Factory.CreateDbContext();
            var alerts = cleanup.NotificationAlerts.Where(a => a.Fingerprint.StartsWith(prefix));
            cleanup.NotificationDeliveries.RemoveRange(cleanup.NotificationDeliveries.Where(d => alerts.Any(a => a.Id == d.AlertId)));
            cleanup.NotificationAlerts.RemoveRange(alerts);
            await cleanup.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Nodo_nunca_conectado_queda_NEVER_CONNECTED_tras_monitoreo()
    {
        await using var reset = _fixture.Factory.CreateDbContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(reset);
        await IntegrationTestHelpers.EnsureNeverConnectedNodeAsync(reset);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<HydroPilotDbContext>(o => o.UseSqlServer(_fixture.ConnectionString));
        services.AddOptions<TelemetryOptions>().Configure(o => o.MonitorIntervalSeconds = 10);
        var provider = services.BuildServiceProvider();
        var monitor = new NodeConnectionMonitorHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptionsMonitor<TelemetryOptions>>(),
            provider.GetRequiredService<ILogger<NodeConnectionMonitorHostedService>>());

        await monitor.RunOnceAsync(default);

        await using var verify = _fixture.Factory.CreateDbContext();
        // rpi-inv-02 nunca envió telemetría.
        var never = await verify.IotNodes.FirstAsync(n => n.Identifier == "rpi-inv-02");
        Assert.Equal(TelemetryContract.ConnectionNeverConnected, never.ConnectionState);
    }

    [Fact]
    public async Task Reporting_lecturas_consulta_por_calidad_y_lote()
    {
        await using var reset = _fixture.Factory.CreateDbContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(reset);

        var ingestion = new TelemetryIngestionService(
            _fixture.Factory,
            new TelemetryValidationService(Options.Create(new TelemetryOptions())),
            Options.Create(new TelemetryOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TelemetryIngestionService>.Instance);

        // Dos lecturas: una válida y una físicamente imposible (INVALID).
        await ingestion.IngestAsync(IntegrationTestHelpers.Envelope("b-q-1",
            IntegrationTestHelpers.R("r-1", "ph-solucion", 6.0m, unit: "pH"),
            IntegrationTestHelpers.R("r-2", "ph-solucion", 42.0m, unit: "pH")), default);

        await using var verify = _fixture.Factory.CreateDbContext();
        var valid = await verify.SensorReadings
            .Where(r => r.Quality == TelemetryContract.QualityValid).ToListAsync();
        var invalid = await verify.SensorReadings
            .Where(r => r.Quality == TelemetryContract.QualityInvalid).ToListAsync();

        Assert.Single(valid);
        Assert.Single(invalid);
        Assert.Equal(6.0m, valid[0].Value);
        Assert.Equal(42.0m, invalid[0].Value);
        Assert.Equal(TelemetryContract.ReasonFueraDeRango, invalid[0].QualityReason);
    }
}