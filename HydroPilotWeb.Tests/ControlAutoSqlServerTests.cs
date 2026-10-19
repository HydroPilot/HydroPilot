using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Control;
using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Control;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Control automático contra SQL Server: la ingesta avisa al motor, el motor crea la dosis, el barrido mide el
/// efecto (autocalibra y pausa) y el modo y las pausas definen qué corrige el motor.
/// </summary>
[Collection("control-sql")]
public class ControlAutoSqlServerTests
{
    private readonly ControlSqlFixture _fx;

    public ControlAutoSqlServerTests(ControlSqlFixture fx) => _fx = fx;

    [Fact]
    public async Task Lecturas_de_la_ingesta_disparan_el_motor_y_crea_la_dosis()
    {
        await _fx.ResetCommandsAsync();
        var batchId = $"auto-test-{Guid.NewGuid():N}";
        try
        {
            // La Pi viene consultando y el nodo está en automático.
            await _fx.Control.PollAsync(ControlSqlFixture.NodeIdentifier, DateTime.UtcNow);
            await SetModeRawAsync(ControlContract.ModeAuto);

            var trigger = new ControlTrigger();
            var telemetry = Microsoft.Extensions.Options.Options.Create(new TelemetryOptions());
            var ingestion = new TelemetryIngestionService(_fx.Factory, new TelemetryValidationService(telemetry), telemetry,
                NullLogger<TelemetryIngestionService>.Instance, trigger);

            var observed = DateTime.UtcNow.AddSeconds(-5).ToString("o");
            var body = JsonDocument.Parse($$"""
                {
                  "batchId": "{{batchId}}",
                  "nodeId": "{{ControlSqlFixture.NodeIdentifier}}",
                  "schemaVersion": "2",
                  "sentAt": "{{observed}}",
                  "readings": [
                    { "readingId": "{{batchId}}-ph", "sensorRef": "ph-solucion", "value": 6.9, "unit": "pH", "observedAt": "{{observed}}" },
                    { "readingId": "{{batchId}}-ec", "sensorRef": "ec-solucion", "value": 1.48, "unit": "mS/cm", "observedAt": "{{observed}}" }
                  ]
                }
                """).RootElement;
            var outcome = await ingestion.IngestAsync(TelemetryContractParser.Parse(body).Envelope!, CancellationToken.None);
            Assert.Equal(IngestBatchStatus.Processed, outcome.Status);

            Assert.True(trigger.Reader.TryRead(out var nodeId));
            Assert.Equal(_fx.NodeId, nodeId);

            // pH 6,9 firme (con el 6,82 anterior) y CE en rango: pH Down, recortado al máximo por dosis.
            var decision = await _fx.Control.EvaluateNodeAsync(nodeId, DateTime.UtcNow);
            Assert.Equal(ControlEngine.Outcome.Dose, decision.Outcome);
            Assert.Equal(ControlContract.KindPhDown, decision.Pump!.Kind);

            await using (var context = _fx.NewContext())
            {
                var command = await context.ActuatorCommands.SingleAsync();
                Assert.Equal(ControlContract.SourceAuto, command.Source);
                Assert.Equal(ControlContract.ControllerAuto, command.ControllerVersion);
                Assert.Equal(ControlContract.StatusPending, command.Status);
                Assert.Equal(50m, command.Amount);
                Assert.Equal(ControlContract.ParameterPh, command.Parameter);
                Assert.Equal(6.9m, command.ValueBefore);
                Assert.Equal(6.0m, command.Target);
                Assert.Equal(-0.25m, command.ExpectedEffect);
                Assert.True(command.Strength is > 0m and <= 0.9m);
                Assert.Null(command.RequestedBy);
            }

            // Con la dosis en curso no crea otra.
            Assert.Equal(ControlEngine.Codes.InFlight, (await _fx.Control.EvaluateNodeAsync(nodeId, DateTime.UtcNow)).Code);
        }
        finally
        {
            await SetModeRawAsync(ControlContract.ModeManual);
            await using var context = _fx.NewContext();
            await context.SensorReadings.Where(r => r.ExternalReadingId.StartsWith(batchId)).ExecuteDeleteAsync();
            await context.TelemetryBatches.Where(b => b.BatchId == batchId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task En_manual_las_lecturas_no_crean_dosis()
    {
        await _fx.ResetCommandsAsync();
        await _fx.Control.PollAsync(ControlSqlFixture.NodeIdentifier, DateTime.UtcNow);

        var decision = await _fx.Control.EvaluateNodeAsync(_fx.NodeId, DateTime.UtcNow);

        Assert.Equal(ControlEngine.Outcome.Off, decision.Outcome);
        await using var context = _fx.NewContext();
        Assert.False(await context.ActuatorCommands.AnyAsync());
    }

    [Fact]
    public async Task Barrido_mide_el_efecto_calibra_y_pausa_tras_tres_dosis_sin_efecto()
    {
        await _fx.ResetCommandsAsync();
        var node = _fx.SpareNodeId;
        var phDown = await _fx.ActuatorIdAsync(ControlContract.KindPhDown, node);
        var now = DateTime.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];

        await using (var context = _fx.NewContext())
        {
            await context.Actuators.Where(a => a.Id == phDown)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.EffectPerUnit, 2.5m).SetProperty(a => a.EffectSamples, 0));

            // Cuatro dosis de 50 ml separadas 50 min: la primera bajó 0,30; las otras tres no movieron el pH.
            int[] completedMinutes = [-210, -160, -110, -60];
            decimal[] after = [6.60m, 6.90m, 6.90m, 6.91m];
            for (var i = 0; i < completedMinutes.Length; i++)
            {
                var before = Reading(node, 6.90m, now.AddMinutes(completedMinutes[i] - 1), $"eff-{tag}-b{i}");
                context.SensorReadings.Add(before);
                context.SensorReadings.Add(Reading(node, after[i], now.AddMinutes(completedMinutes[i] + 11), $"eff-{tag}-a{i}"));
                await context.SaveChangesAsync();
                context.ActuatorCommands.Add(Executed(node, phDown, now.AddMinutes(completedMinutes[i]), before.Id));
            }
            await context.SaveChangesAsync();
        }

        var sweep = await _fx.Control.SweepAsync(now);
        Assert.Equal(4, sweep.EffectsMeasured);
        Assert.Equal(1, sweep.Paused);

        await using (var check = _fx.NewContext())
        {
            var commands = await check.ActuatorCommands.Where(c => c.NodeId == node).OrderBy(c => c.CompletedAtUtc).ToListAsync();
            Assert.Equal([true, false, false, false], commands.Select(c => c.Effective!.Value));
            Assert.Equal(-0.30m, commands[0].ObservedEffect);
            Assert.Equal(6.60m, commands[0].ValueAfter);

            // 50 ml en 500 L bajaron 0,30 → 3,0 por ml/L; promedio con 2,5: 2,65.
            var pump = await check.Actuators.SingleAsync(a => a.Id == phDown);
            Assert.Equal(2.65m, pump.EffectPerUnit);
            Assert.Equal(1, pump.EffectSamples);

            var pause = await check.ControlPauses.SingleAsync(p => p.NodeId == node && p.ResumedAtUtc == null);
            Assert.Equal(ControlContract.ParameterPh, pause.Parameter);
            Assert.Equal("3 dosis seguidas sin efecto", pause.Reason);
        }
        Assert.Contains(_fx.Sink.Paused, p => p.NodeId == node && p.Parameter == ControlContract.ParameterPh);

        // Reanudar cierra la pausa y avisa; la racha de antes no vuelve a pausar.
        Assert.True((await _fx.Control.ResumeAsync(node, ControlContract.ParameterPh, "operador@test")).Succeeded);
        Assert.Contains(_fx.Sink.Resumed, r => r.NodeId == node && r.Parameter == ControlContract.ParameterPh);
        Assert.False((await _fx.Control.ResumeAsync(node, ControlContract.ParameterPh, "operador@test")).Succeeded);

        await using (var context = _fx.NewContext())
        {
            var before = Reading(node, 6.90m, now.AddMinutes(-31), $"eff-{tag}-b9");
            context.SensorReadings.Add(before);
            context.SensorReadings.Add(Reading(node, 6.90m, now.AddMinutes(-19), $"eff-{tag}-a9"));
            await context.SaveChangesAsync();
            context.ActuatorCommands.Add(Executed(node, phDown, now.AddMinutes(-30), before.Id));
            await context.SaveChangesAsync();
        }
        var again = await _fx.Control.SweepAsync(now);
        Assert.Equal((1, 0), (again.EffectsMeasured, again.Paused));
    }

    [Fact]
    public async Task Modo_y_pausas_definen_lo_que_controla_el_motor()
    {
        await _fx.ResetCommandsAsync();
        try
        {
            await _fx.Control.PollAsync(ControlSqlFixture.NodeIdentifier, DateTime.UtcNow);
            await SetModeRawAsync(ControlContract.ModeAuto);
            await using (var context = _fx.NewContext())
            {
                context.ControlPauses.Add(new ControlPause
                {
                    NodeId = _fx.NodeId,
                    Parameter = ControlContract.ParameterPh,
                    PausedAtUtc = DateTime.UtcNow,
                    Reason = "prueba",
                });
                await context.SaveChangesAsync();
            }

            // pH alto pero pausado, CE en rango: el motor no dosifica y lo muestra.
            var decision = await _fx.Control.EvaluateNodeAsync(_fx.NodeId, DateTime.UtcNow);
            Assert.Equal((ControlEngine.Outcome.Paused, ControlContract.ParameterPh), (decision.Outcome, decision.Parameter));

            var view = await _fx.Control.GetViewAsync(_fx.NodeId);
            Assert.Equal(ControlEngine.Codes.Paused, view!.Status.Code);
            Assert.Equal(ControlContract.ParameterPh, Assert.Single(view.OpenPauses).Parameter);

            await using (var context = _fx.NewContext())
            {
                Assert.Equal([ControlContract.ParameterEc],
                    await ControlService.AutoControlledForLotAsync(context, _fx.LotId, DateTime.UtcNow));
            }

            Assert.False((await _fx.Control.SetModeAsync(_fx.NodeId, "TURBO", "admin")).Succeeded);
            Assert.True((await _fx.Control.SetModeAsync(_fx.NodeId, ControlContract.ModeManual, "admin")).Succeeded);
            await using (var context = _fx.NewContext())
            {
                Assert.Empty(await ControlService.AutoControlledForLotAsync(context, _fx.LotId, DateTime.UtcNow));
            }
        }
        finally
        {
            await SetModeRawAsync(ControlContract.ModeManual);
            await _fx.ResetCommandsAsync();
        }
    }

    // ------------------------------------------------------------------

    private async Task SetModeRawAsync(string mode)
    {
        await using var context = _fx.NewContext();
        await context.IotNodes.Where(n => n.Id == _fx.NodeId)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ControlMode, mode));
    }

    private SensorReading Reading(int nodeId, decimal value, DateTime observedAt, string externalId) => new()
    {
        SensorId = _fx.SparePhSensorId,
        NodeId = nodeId,
        Value = value,
        Timestamp = observedAt,
        ObservedAtUtc = observedAt,
        ReceivedAtUtc = observedAt,
        ExternalReadingId = externalId,
        Quality = TelemetryContract.QualityValid,
        IngestionResult = TelemetryContract.ResultSinLote,
        CreatedAt = DateTime.UtcNow,
    };

    private static ActuatorCommand Executed(int nodeId, int actuatorId, DateTime completedAt, int triggerReadingId) => new()
    {
        Id = Guid.NewGuid(),
        NodeId = nodeId,
        ActuatorId = actuatorId,
        Source = ControlContract.SourceManual,
        Amount = 50m,
        ExecutedAmount = 50m,
        Unit = ControlContract.UnitMl,
        Status = ControlContract.StatusExecuted,
        CreatedAtUtc = completedAt.AddSeconds(-30),
        ExpiresAtUtc = completedAt.AddMinutes(5),
        DeliveredAtUtc = completedAt.AddSeconds(-20),
        CompletedAtUtc = completedAt,
        Parameter = ControlContract.ParameterPh,
        TriggerReadingId = triggerReadingId,
        ValueBefore = 6.90m,
        ExpectedEffect = -0.25m,
    };
}
