using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using HydroPilotWeb.Models.Control;
using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Control;
using HydroPilotWeb.Services.Control.IotHub;

namespace HydroPilotWeb.Tests;

/// <summary>Canal de prueba con forma de IoT Hub: graba lo que se manda y puede fallar las primeras veces.</summary>
public sealed class RecordingCommandChannel : ICommandChannel
{
    private readonly List<DeviceCommandDto> _sent = [];
    private int _failuresLeft;

    public RecordingCommandChannel(int failFirst = 0) => _failuresLeft = failFirst;

    public bool PushesCommands => true;

    public IReadOnlyList<DeviceCommandDto> Sent { get { lock (_sent) return _sent.ToList(); } }

    public Task DispatchAsync(DeviceCommandDto command, CancellationToken ct = default)
    {
        lock (_sent)
        {
            if (_failuresLeft > 0)
            {
                _failuresLeft--;
                throw new InvalidOperationException("hub caído");
            }
            _sent.Add(command);
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Transporte Azure IoT Hub contra SQL Server, sin hub real: los mensajes de la Pi (lecturas y resultados) pasan
/// por <see cref="IotHubMessageHandler"/> igual que si llegaran del hub, y los comandos salen por un canal que
/// graba lo que se manda.
/// </summary>
[Collection("control-sql")]
public class ControlIotHubSqlServerTests
{
    private readonly ControlSqlFixture _fx;

    public ControlIotHubSqlServerTests(ControlSqlFixture fx) => _fx = fx;

    private IotHubMessageHandler Handler(ControlService control)
    {
        var telemetry = Microsoft.Extensions.Options.Options.Create(new TelemetryOptions());
        var ingestion = new TelemetryIngestionService(_fx.Factory, new TelemetryValidationService(telemetry), telemetry,
            NullLogger<TelemetryIngestionService>.Instance);
        return new IotHubMessageHandler(ingestion, control, NullLogger<IotHubMessageHandler>.Instance);
    }

    [Fact]
    public async Task Lecturas_por_IoT_Hub_se_ingieren_y_cuentan_como_senal_de_la_Pi()
    {
        var batchId = $"iot-test-{Guid.NewGuid():N}";
        try
        {
            await using (var context = _fx.NewContext())
                await context.IotNodes.Where(n => n.Id == _fx.NodeId)
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.LastCommandPollAtUtc, (DateTime?)null));

            var handler = Handler(_fx.NewControl(new RecordingCommandChannel()));
            var body = Telemetry(batchId, ControlSqlFixture.NodeIdentifier, 6.3m);

            var result = await handler.HandleAsync(ControlSqlFixture.NodeIdentifier, IotHubMessages.TypeTelemetry, body, DateTime.UtcNow);
            Assert.Equal((IotHubMessageHandler.Outcome.Telemetry, nameof(IngestBatchStatus.Processed)), (result.Outcome, result.Detail));

            // El mismo mensaje dos veces (el hub entrega "al menos una vez"): no duplica.
            var again = await handler.HandleAsync(ControlSqlFixture.NodeIdentifier, IotHubMessages.TypeTelemetry, body, DateTime.UtcNow);
            Assert.Equal(nameof(IngestBatchStatus.Duplicate), again.Detail);

            await using var check = _fx.NewContext();
            Assert.Equal(1, await check.SensorReadings.CountAsync(r => r.ExternalReadingId == $"{batchId}-ph"));
            Assert.NotNull((await check.IotNodes.SingleAsync(n => n.Id == _fx.NodeId)).LastCommandPollAtUtc);
        }
        finally
        {
            await DeleteBatchAsync(batchId);
        }
    }

    [Fact]
    public async Task Un_dispositivo_no_puede_mandar_lecturas_de_otro_nodo()
    {
        var batchId = $"iot-test-{Guid.NewGuid():N}";
        var handler = Handler(_fx.NewControl(new RecordingCommandChannel()));

        var result = await handler.HandleAsync("ctl-node-02", IotHubMessages.TypeTelemetry,
            Telemetry(batchId, ControlSqlFixture.NodeIdentifier, 6.3m), DateTime.UtcNow);

        Assert.Equal(IotHubMessageHandler.Outcome.NodeMismatch, result.Outcome);
        await using var check = _fx.NewContext();
        Assert.False(await check.SensorReadings.AnyAsync(r => r.ExternalReadingId.StartsWith(batchId)));

        Assert.Equal(IotHubMessageHandler.Outcome.NoDevice,
            (await handler.HandleAsync(null, IotHubMessages.TypeTelemetry, [], DateTime.UtcNow)).Outcome);
        Assert.Equal(IotHubMessageHandler.Outcome.UnknownType,
            (await handler.HandleAsync(ControlSqlFixture.NodeIdentifier, "otra-cosa", [], DateTime.UtcNow)).Outcome);
    }

    [Fact]
    public async Task Dosis_se_manda_por_IoT_Hub_y_vuelve_su_resultado()
    {
        await _fx.ResetCommandsAsync();
        var channel = new RecordingCommandChannel();
        var control = _fx.NewControl(channel);
        var phDown = await _fx.ActuatorIdAsync(ControlContract.KindPhDown);

        Assert.True((await control.RequestDoseAsync(_fx.NodeId, phDown, 20m, "operador@test")).Succeeded);

        var sent = Assert.Single(channel.Sent);
        Assert.Equal((ControlSqlFixture.NodeIdentifier, ControlContract.KindPhDown, 20m), (sent.NodeId, sent.Actuator, sent.Amount));
        Assert.Equal(ControlSqlFixture.PhReadingExternalId, sent.Reason.ReadingId);

        await using (var context = _fx.NewContext())
        {
            var command = await context.ActuatorCommands.SingleAsync(c => c.Id == sent.CommandId);
            Assert.Equal(ControlContract.StatusPending, command.Status);
            Assert.NotNull(command.DispatchedAtUtc);
        }

        // Ya está en la cola del dispositivo: no se puede cancelar.
        Assert.False((await control.CancelAsync(_fx.NodeId, sent.CommandId, "admin")).Succeeded);

        // Acuse del hub (la Pi completó el mensaje) y resultado por D2C.
        await control.ApplyDeliveryFeedbackAsync(sent.CommandId, DeliveryFeedback.Delivered, DateTime.UtcNow);
        var result = await Handler(control).HandleAsync(ControlSqlFixture.NodeIdentifier, IotHubMessages.TypeCommandResult,
            Encoding.UTF8.GetBytes($$"""{ "commandId": "{{sent.CommandId}}", "status": "EXECUTED", "executedAmount": 20 }"""),
            DateTime.UtcNow);
        Assert.Equal((IotHubMessageHandler.Outcome.Result, nameof(DeviceResultOutcome.Applied)), (result.Outcome, result.Detail));

        await using var check = _fx.NewContext();
        var done = await check.ActuatorCommands.SingleAsync(c => c.Id == sent.CommandId);
        Assert.Equal(ControlContract.StatusExecuted, done.Status);
        Assert.NotNull(done.DeliveredAtUtc);
    }

    [Fact]
    public async Task Si_el_hub_falla_el_barrido_reintenta_y_un_rechazo_cierra_el_comando()
    {
        await _fx.ResetCommandsAsync();
        var channel = new RecordingCommandChannel(failFirst: 1);
        var control = _fx.NewControl(channel);
        var nutrient = await _fx.ActuatorIdAsync(ControlContract.KindNutrient);

        Assert.True((await control.RequestDoseAsync(_fx.NodeId, nutrient, 100m, null)).Succeeded);
        Assert.Empty(channel.Sent);
        await using (var context = _fx.NewContext())
            Assert.Null((await context.ActuatorCommands.SingleAsync()).DispatchedAtUtc);

        await control.SweepAsync(DateTime.UtcNow);
        var sent = Assert.Single(channel.Sent);

        await control.ApplyDeliveryFeedbackAsync(sent.CommandId, DeliveryFeedback.Rejected, DateTime.UtcNow);
        await using var check = _fx.NewContext();
        var command = await check.ActuatorCommands.SingleAsync();
        Assert.NotNull(command.DispatchedAtUtc);
        Assert.Equal(ControlContract.StatusRejected, command.Status);
    }

    [Fact]
    public async Task Con_IoT_Hub_la_senal_de_la_Pi_tolera_el_intervalo_de_telemetria()
    {
        await _fx.ResetCommandsAsync();
        try
        {
            // Señal de la Pi hace 5 min: por HTTP es "La Pi no consulta"; por IoT Hub (telemetría cada 5 min) no.
            await using (var context = _fx.NewContext())
                await context.IotNodes.Where(n => n.Id == _fx.NodeId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(n => n.ControlMode, ControlContract.ModeAuto)
                        .SetProperty(n => n.LastCommandPollAtUtc, DateTime.UtcNow.AddMinutes(-5)));

            Assert.Equal(ControlEngine.Codes.PiSilent, (await _fx.Control.EvaluateNodeAsync(_fx.NodeId, DateTime.UtcNow)).Code);

            var channel = new RecordingCommandChannel();
            var decision = await _fx.NewControl(channel).EvaluateNodeAsync(_fx.NodeId, DateTime.UtcNow);
            Assert.Equal(ControlEngine.Outcome.Dose, decision.Outcome);
            Assert.Single(channel.Sent);
        }
        finally
        {
            await using var context = _fx.NewContext();
            await context.IotNodes.Where(n => n.Id == _fx.NodeId)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ControlMode, ControlContract.ModeManual));
            await _fx.ResetCommandsAsync();
        }
    }

    // ------------------------------------------------------------------

    private static byte[] Telemetry(string batchId, string nodeId, decimal ph)
    {
        var observed = DateTime.UtcNow.AddSeconds(-5).ToString("o");
        return Encoding.UTF8.GetBytes($$"""
            {
              "batchId": "{{batchId}}",
              "nodeId": "{{nodeId}}",
              "schemaVersion": "2",
              "sentAt": "{{observed}}",
              "readings": [
                { "readingId": "{{batchId}}-ph", "sensorRef": "ph-solucion", "value": {{ph.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "unit": "pH", "observedAt": "{{observed}}" }
              ]
            }
            """);
    }

    private async Task DeleteBatchAsync(string batchId)
    {
        await using var context = _fx.NewContext();
        await context.SensorReadings.Where(r => r.ExternalReadingId.StartsWith(batchId)).ExecuteDeleteAsync();
        await context.TelemetryBatches.Where(b => b.BatchId == batchId).ExecuteDeleteAsync();
    }
}
