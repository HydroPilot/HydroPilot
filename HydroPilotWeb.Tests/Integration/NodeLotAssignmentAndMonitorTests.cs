using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services;
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

    private NodeLotAssignmentService CreateAssignmentService() => new(_fixture.Factory);

    [Fact]
    public async Task Asignacion_solapada_se_rechaza()
    {
        await using var reset = _fixture.Factory.CreateDbContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(reset);
        reset.NodeLotAssignments.RemoveRange(reset.NodeLotAssignments);
        await reset.SaveChangesAsync();

        var nodeId = await IntegrationTestHelpers.NodeIdAsync(reset);
        var lot = await reset.Lots.OrderBy(l => l.Id).FirstAsync();
        var service = CreateAssignmentService();

        var first = await service.CreateAsync(nodeId, lot.Id,
            IntegrationTestHelpers.NowUtc.AddDays(-10), null, "manual", default);
        Assert.Null(first.Error);

        var overlapping = await service.CreateAsync(nodeId, lot.Id,
            IntegrationTestHelpers.NowUtc.AddDays(-5), null, "manual", default);
        Assert.NotNull(overlapping.Error);
        Assert.Contains("solapa", overlapping.Error);

        // Ventana cerrada ANTERIOR a la vigente: no solapa y debe aceptarse.
        var historical = await service.CreateAsync(nodeId, lot.Id,
            IntegrationTestHelpers.NowUtc.AddDays(-40), IntegrationTestHelpers.NowUtc.AddDays(-30), "auto", default);
        Assert.Null(historical.Error);

        var list = await service.ListAsync(nodeId, default);
        Assert.Equal(2, list.Count);
        Assert.Equal("auto", list.First(a => a.Source == "auto").Source);
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
    public async Task Nodo_nunca_conectado_queda_NEVER_CONNECTED_tras_monitoreo()
    {
        await using var reset = _fixture.Factory.CreateDbContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(reset);

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
        // rpi-inv-02 (seed) nunca envió telemetría.
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