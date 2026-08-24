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
public class TelemetryIngestionIntegrationTests
{
    private readonly SqlServerTestFixture _fixture;

    public TelemetryIngestionIntegrationTests(SqlServerTestFixture fixture)
    {
        _fixture = fixture;
    }

    private TelemetryIngestionService CreateIngestion(int maxReadingsPerBatch = 1000)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<TelemetryOptions>().Configure(o => o.MaxReadingsPerBatch = maxReadingsPerBatch);
        services.AddSingleton<IDbContextFactory<HydroPilotDbContext>>(_fixture.Factory);
        services.AddScoped<TelemetryValidationService>();
        services.AddScoped<TelemetryIngestionService>();
        return services.BuildServiceProvider().GetRequiredService<TelemetryIngestionService>();
    }

    [Fact]
    public async Task Batch_valido_persiste_una_vez_y_asigna_por_fecha_de_observacion()
    {
        await using var reset = _fixture.Factory.CreateDbContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(reset);

        var nodeId = await IntegrationTestHelpers.NodeIdAsync(reset);

        // Limpia asignaciones y crea una propia con ventana abierta.
        reset.NodeLotAssignments.RemoveRange(reset.NodeLotAssignments);
        await reset.SaveChangesAsync();
        var lot = await reset.Lots.OrderBy(l => l.Id).FirstAsync();
        var assignmentService = new NodeLotAssignmentService(_fixture.Factory);
        var created = await assignmentService.CreateAsync(
            nodeId, lot.Id, IntegrationTestHelpers.NowUtc.AddDays(-30), null,
            TelemetryContract.AssignmentSourceFixture, default);
        Assert.Null(created.Error);

        var ingestion = CreateIngestion();
        var envelope = IntegrationTestHelpers.Envelope("b-valid-1",
            IntegrationTestHelpers.R("r-1", "ph-solucion", 5.9m, unit: "pH"),
            IntegrationTestHelpers.R("r-2", "ec-solucion", 1.8m, unit: "mS/cm"),
            IntegrationTestHelpers.R("r-3", "temp-ambiente", 23.5m, unit: "°C"),
            IntegrationTestHelpers.R("r-4", "hum-ambiente", 65.0m, unit: "%"));

        var outcome = await ingestion.IngestAsync(envelope, default);

        Assert.Equal(IngestBatchStatus.Processed, outcome.Status);
        Assert.All(outcome.Readings!, r =>
        {
            Assert.Equal(TelemetryContract.ResultAceptada, r.Result);
            Assert.Equal(TelemetryContract.QualityValid, r.Quality);
            Assert.True(r.Assigned);
            Assert.Equal(TelemetryContract.AssignmentStateAsignado, r.AssignmentState);
        });

        await using var verify = _fixture.Factory.CreateDbContext();
        Assert.Equal(4, await verify.SensorReadings.CountAsync());
        Assert.All(await verify.SensorReadings.ToListAsync(), r =>
        {
            Assert.Equal(lot.Id, r.LotId);
            Assert.Equal(TelemetryContract.QualityValid, r.Quality);
            Assert.Equal(TelemetryContract.ResultAceptada, r.IngestionResult);
            Assert.Equal(TelemetryContract.AssignmentStateAsignado, r.LotId.HasValue ? TelemetryContract.AssignmentStateAsignado : null);
        });

        Assert.NotNull(await verify.TelemetryBatches.FirstOrDefaultAsync(b => b.BatchId == "b-valid-1"));

        var node = await verify.IotNodes.FirstAsync(n => n.Id == nodeId);
        Assert.NotNull(node.LastAcceptedAt);
        Assert.Equal(TelemetryContract.ConnectionOnline, node.ConnectionState);
    }

    [Fact]
    public async Task Reenvio_identico_repite_resultado_anterior_sin_duplicar()
    {
        await using var reset = _fixture.Factory.CreateDbContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(reset);

        var ingestion = CreateIngestion();
        var envelope = IntegrationTestHelpers.Envelope("b-dup-1",
            IntegrationTestHelpers.R("r-1", "ph-solucion", 5.9m, unit: "pH"));

        var first = await ingestion.IngestAsync(envelope, default);
        var second = await ingestion.IngestAsync(envelope, default);

        Assert.Equal(IngestBatchStatus.Processed, first.Status);
        Assert.Equal(IngestBatchStatus.Duplicate, second.Status);
        Assert.Equal(first.Readings![0].Result, second.Readings![0].Result);

        await using var verify = _fixture.Factory.CreateDbContext();
        Assert.Equal(1, await verify.SensorReadings.CountAsync());
        Assert.Equal(1, await verify.TelemetryBatches.CountAsync());
    }

    [Fact]
    public async Task Mismo_batch_con_contenido_distinto_devuelve_conflicto()
    {
        await using var reset = _fixture.Factory.CreateDbContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(reset);

        var ingestion = CreateIngestion();
        var first = IntegrationTestHelpers.Envelope("b-conf-1",
            IntegrationTestHelpers.R("r-1", "ph-solucion", 5.9m, unit: "pH"));
        var mutated = IntegrationTestHelpers.Envelope("b-conf-1",
            IntegrationTestHelpers.R("r-1", "ph-solucion", 9.5m, unit: "pH"));

        Assert.Equal(IngestBatchStatus.Processed, (await ingestion.IngestAsync(first, default)).Status);
        var conflict = await ingestion.IngestAsync(mutated, default);

        Assert.Equal(IngestBatchStatus.Conflict, conflict.Status);

        await using var verify = _fixture.Factory.CreateDbContext();
        Assert.Equal(1, await verify.SensorReadings.CountAsync());
    }

    [Fact]
    public async Task Reintentos_concurrentes_no_duplican_lecturas()
    {
        await using var reset = _fixture.Factory.CreateDbContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(reset);

        var ingestion = CreateIngestion();
        var envelope = IntegrationTestHelpers.Envelope("b-concurrente-1",
            IntegrationTestHelpers.R("r-1", "ph-solucion", 5.9m, unit: "pH"),
            IntegrationTestHelpers.R("r-2", "temp-ambiente", 23.0m, unit: "°C"));

        // 6 intentos simultáneos del mismo batch (retry concurrente real sobre SQL Server).
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => ingestion.IngestAsync(envelope, default)));

        Assert.All(outcomes, o =>
            Assert.True(o.Status is IngestBatchStatus.Processed or IngestBatchStatus.Duplicate,
                $"Status inesperado: {o.Status}"));

        await using var verify = _fixture.Factory.CreateDbContext();
        Assert.Equal(2, await verify.SensorReadings.CountAsync());       // una sola copia de cada lectura
        Assert.Equal(1, await verify.TelemetryBatches.CountAsync());     // un solo registro de batch

        var rows = await verify.SensorReadings.ToListAsync();
        Assert.Equal(2, rows.Select(r => r.ExternalReadingId).Distinct().Count());
    }

    [Fact]
    public async Task Sensor_desconocido_genera_rechazo_y_no_persiste_lectura()
    {
        await using var reset = _fixture.Factory.CreateDbContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(reset);

        var ingestion = CreateIngestion();
        var envelope = IntegrationTestHelpers.Envelope("b-unk-1",
            IntegrationTestHelpers.R("r-1", "sensor-fantasma", 5.9m, unit: "pH"));

        var outcome = await ingestion.IngestAsync(envelope, default);

        Assert.Equal(IngestBatchStatus.Processed, outcome.Status);
        var reading = Assert.Single(outcome.Readings!);
        Assert.Equal(TelemetryContract.ResultDesconocida, reading.Result);
        Assert.Equal(TelemetryContract.ReasonSensorDesconocido, reading.Reason);

        await using var verify = _fixture.Factory.CreateDbContext();
        Assert.Equal(0, await verify.SensorReadings.CountAsync());
        var rejection = await verify.TelemetryRejections.SingleAsync();
        Assert.Equal(TelemetryContract.ReasonSensorDesconocido, rejection.Reason);
        Assert.Equal("sensor-fantasma", rejection.SensorRef);
    }

    [Fact]
    public async Task Unidad_incorrecta_genera_error_de_unidad()
    {
        await using var reset = _fixture.Factory.CreateDbContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(reset);

        var ingestion = CreateIngestion();
        var envelope = IntegrationTestHelpers.Envelope("b-unit-1",
            IntegrationTestHelpers.R("r-1", "ph-solucion", 5.9m, unit: "mS/cm"));

        var outcome = await ingestion.IngestAsync(envelope, default);

        var reading = Assert.Single(outcome.Readings!);
        Assert.Equal(TelemetryContract.ResultErrorUnidad, reading.Result);

        await using var verify = _fixture.Factory.CreateDbContext();
        Assert.Equal(0, await verify.SensorReadings.CountAsync());
        Assert.Equal(TelemetryContract.ReasonErrorUnidad, (await verify.TelemetryRejections.SingleAsync()).Reason);
    }

    [Fact]
    public async Task Valor_fisicamente_imposible_queda_en_cuarentena_INVALID_sin_entrar_a_GDD()
    {
        await using var reset = _fixture.Factory.CreateDbContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(reset);

        var ingestion = CreateIngestion();
        var envelope = IntegrationTestHelpers.Envelope("b-range-1",
            IntegrationTestHelpers.R("r-1", "ph-solucion", 25.0m, unit: "pH")); // pH imposible

        var outcome = await ingestion.IngestAsync(envelope, default);

        var reading = Assert.Single(outcome.Readings!);
        Assert.Equal(TelemetryContract.ResultFueraDeRango, reading.Result);

        await using var verify = _fixture.Factory.CreateDbContext();
        var stored = await verify.SensorReadings.SingleAsync();
        Assert.Equal(TelemetryContract.QualityInvalid, stored.Quality);
        Assert.Equal(TelemetryContract.ReasonFueraDeRango, stored.QualityReason);
        // Fuera de la usabilidad operativa (GDD/anomalías).
        Assert.False(TelemetryQualityPolicy.IsOperationallyUsable(stored.Quality));
    }

    [Fact]
    public async Task Timestamp_futuro_se_rechaza_sin_persistir()
    {
        await using var reset = _fixture.Factory.CreateDbContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(reset);

        var ingestion = CreateIngestion();
        var future = IntegrationTestHelpers.NowUtc.AddHours(1);
        var envelope = IntegrationTestHelpers.Envelope("b-future-1",
            IntegrationTestHelpers.R("r-1", "ph-solucion", 6.0m, observedAt: future, unit: "pH"));

        var outcome = await ingestion.IngestAsync(envelope, default);

        var reading = Assert.Single(outcome.Readings!);
        Assert.Equal(TelemetryContract.ResultInvalida, reading.Result);
        Assert.Equal(TelemetryContract.QualityFuture, reading.Quality);

        await using var verify = _fixture.Factory.CreateDbContext();
        Assert.Equal(0, await verify.SensorReadings.CountAsync());
        var rejection = await verify.TelemetryRejections.SingleAsync();
        Assert.Equal(TelemetryContract.ReasonTimestampFuturo, rejection.Reason);
        Assert.Equal(TelemetryContract.QualityFuture, rejection.Quality);
    }

    [Fact]
    public async Task Timestamp_antiguo_persiste_con_quality_STALE()
    {
        await using var reset = _fixture.Factory.CreateDbContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(reset);

        var ingestion = CreateIngestion();
        var old = IntegrationTestHelpers.NowUtc.AddDays(-3);
        var envelope = IntegrationTestHelpers.Envelope("b-stale-1",
            IntegrationTestHelpers.R("r-1", "ph-solucion", 6.0m, observedAt: old, unit: "pH"));

        var outcome = await ingestion.IngestAsync(envelope, default);
        Assert.Equal(TelemetryContract.ResultAceptada, outcome.Readings![0].Result);

        await using var verify = _fixture.Factory.CreateDbContext();
        var stored = await verify.SensorReadings.SingleAsync();
        Assert.Equal(TelemetryContract.QualityStale, stored.Quality);
        Assert.Equal(old, stored.ObservedAtUtc);
        // STALE sigue siendo utilizable por GDD (transmisión real, sólo vieja).
        Assert.True(TelemetryQualityPolicy.IsOperationallyUsable(stored.Quality));
    }

    [Fact]
    public async Task Sin_asignacion_resultado_sin_lote_y_estado_explicito()
    {
        await using var reset = _fixture.Factory.CreateDbContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(reset);
        reset.NodeLotAssignments.RemoveRange(reset.NodeLotAssignments);
        await reset.SaveChangesAsync();

        var ingestion = CreateIngestion();
        var envelope = IntegrationTestHelpers.Envelope("b-nolot-1",
            IntegrationTestHelpers.R("r-1", "ph-solucion", 6.0m, unit: "pH"));

        var outcome = await ingestion.IngestAsync(envelope, default);

        var reading = Assert.Single(outcome.Readings!);
        Assert.Equal(TelemetryContract.ResultSinLote, reading.Result);
        Assert.False(reading.Assigned);
        Assert.Equal(TelemetryContract.AssignmentStateSinAsignacion, reading.AssignmentState);

        await using var verify = _fixture.Factory.CreateDbContext();
        var stored = await verify.SensorReadings.SingleAsync();
        Assert.Null(stored.LotId);
        Assert.Equal(TelemetryContract.ResultSinLote, stored.IngestionResult);
    }

    [Fact]
    public async Task Asignacion_temporal_se_resuelve_por_fecha_de_observacion()
    {
        await using var reset = _fixture.Factory.CreateDbContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(reset);
        reset.NodeLotAssignments.RemoveRange(reset.NodeLotAssignments);
        await reset.SaveChangesAsync();

        var nodeId = await IntegrationTestHelpers.NodeIdAsync(reset);
        var firstLot = await reset.Lots.OrderBy(l => l.Id).FirstAsync();

        // Fixture necesita 2 lotes para probar la ventana temporal; crea el segundo.
        var crop = await reset.CropTypes.FirstAsync();
        var status = await reset.LotStatuses.FirstAsync(s => s.Name == "ACTIVO");
        var secondLot = new Lot
        {
            GreenhouseId = firstLot.GreenhouseId,
            CropTypeId = crop.Id,
            StatusId = status.Id,
            SowingDate = DateOnly.FromDateTime(IntegrationTestHelpers.NowUtc.AddDays(-10)),
            PlantedAreaM2 = 3.0m,
        };
        reset.Lots.Add(secondLot);
        await reset.SaveChangesAsync();
        var lots = new[] { firstLot, secondLot };

        var assignmentService = new NodeLotAssignmentService(_fixture.Factory);
        Assert.Null((await assignmentService.CreateAsync(nodeId, lots[0].Id,
            IntegrationTestHelpers.NowUtc.AddDays(-30), IntegrationTestHelpers.NowUtc.AddDays(-10), "fixture", default)).Error);
        Assert.Null((await assignmentService.CreateAsync(nodeId, lots[1].Id,
            IntegrationTestHelpers.NowUtc.AddDays(-10), null, "fixture", default)).Error);

        var ingestion = CreateIngestion();
        var duringFirst = IntegrationTestHelpers.Envelope("b-asig-a",
            IntegrationTestHelpers.R("r-1", "ph-solucion", 6.0m, observedAt: IntegrationTestHelpers.NowUtc.AddDays(-20), unit: "pH"));
        var duringSecond = IntegrationTestHelpers.Envelope("b-asig-b",
            IntegrationTestHelpers.R("r-2", "ph-solucion", 6.1m, observedAt: IntegrationTestHelpers.NowUtc.AddDays(-2), unit: "pH"));

        Assert.Equal(lots[0].Id, (await ingestion.IngestAsync(duringFirst, default)).Readings![0].LotId);
        Assert.Equal(lots[1].Id, (await ingestion.IngestAsync(duringSecond, default)).Readings![0].LotId);
    }

    [Fact]
    public async Task Batch_que_supera_el_limite_es_rechazado()
    {
        await using var reset = _fixture.Factory.CreateDbContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(reset);

        var ingestion = CreateIngestion(maxReadingsPerBatch: 50);
        var readings = Enumerable.Range(0, 51)
            .Select(i => IntegrationTestHelpers.R($"r-{i}", "ph-solucion", 6.0m, unit: "pH"))
            .ToArray();
        var envelope = IntegrationTestHelpers.Envelope("b-large-1", readings);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ingestion.IngestAsync(envelope, default));

        await using var verify = _fixture.Factory.CreateDbContext();
        Assert.Equal(0, await verify.SensorReadings.CountAsync());
        Assert.Equal(0, await verify.TelemetryBatches.CountAsync());
    }

    [Fact]
    public async Task Nodo_con_telemetria_invalida_no_se_marca_como_aceptado()
    {
        await using var reset = _fixture.Factory.CreateDbContext();
        await IntegrationTestHelpers.ResetTelemetryAsync(reset);

        var ingestion = CreateIngestion();
        var envelope = IntegrationTestHelpers.Envelope("b-rej-only",
            IntegrationTestHelpers.R("r-1", "sensor-fantasma", 6.0m));

        await ingestion.IngestAsync(envelope, default);

        await using var verify = _fixture.Factory.CreateDbContext();
        var node = await verify.IotNodes.FirstAsync(n => n.Identifier == "rpi-inv-01");
        Assert.Null(node.LastAcceptedAt);   // nada aceptado
        Assert.NotNull(node.LastRejectedAt);
        // El monitor lo mantiene como nunca conectado hasta la primera aceptación.
        Assert.Equal(TelemetryContract.ConnectionNeverConnected, node.ConnectionState);
    }
}