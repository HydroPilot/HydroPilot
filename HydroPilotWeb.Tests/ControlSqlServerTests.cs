using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Control;
using HydroPilotWeb.Services.Anomalies;
using HydroPilotWeb.Services.Control;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Ciclo completo de una dosis contra SQL Server: pedido desde la vista, consulta de la Pi, resultado,
/// vencimientos, cancelación, nivel vigente del lote y la vista Control.
/// </summary>
[Collection("control-sql")]
public class ControlSqlServerTests
{
    private readonly ControlSqlFixture _fx;

    public ControlSqlServerTests(ControlSqlFixture fx) => _fx = fx;

    [Fact]
    public async Task La_vista_muestra_la_ultima_temperatura_y_humedad_del_nodo()
    {
        var now = DateTime.UtcNow;
        var created = new List<Sensor>();
        await using (var context = _fx.NewContext())
        {
            Sensor Add(string type, string key)
            {
                var sensorType = context.SensorTypes.FirstOrDefault(t => t.Name == type) ?? context.SensorTypes.Add(new SensorType { Name = type }).Entity;
                var sensor = new Sensor { NodeId = _fx.NodeId, SensorType = sensorType, Name = $"ctl-{key}-{Guid.NewGuid():N}", TechnicalKey = key, IsActive = true, CreatedAt = now };
                context.Sensors.Add(sensor);
                created.Add(sensor);
                return sensor;
            }
            var temperature = Add(AnomalyContract.SensorTypeTemperatura, "temp-ambiente");
            var humidity = Add(AnomalyContract.SensorTypeHumedad, "hum-ambiente");
            await context.SaveChangesAsync();

            SensorReading Reading(Sensor sensor, decimal value, int minutesAgo) => new()
            {
                SensorId = sensor.Id, NodeId = _fx.NodeId, Value = value,
                Timestamp = now.AddMinutes(-minutesAgo), ObservedAtUtc = now.AddMinutes(-minutesAgo), ReceivedAtUtc = now.AddMinutes(-minutesAgo),
                ExternalReadingId = $"ctl-amb-{Guid.NewGuid():N}", Quality = TelemetryContract.QualityValid,
                IngestionResult = TelemetryContract.ResultAceptada, CreatedAt = now,
            };
            context.SensorReadings.AddRange(Reading(temperature, 22.5m, 10), Reading(temperature, 23.4m, 2), Reading(humidity, 61m, 3));
            await context.SaveChangesAsync();
        }

        try
        {
            var view = await _fx.Control.GetViewAsync(_fx.CircuitId);

            Assert.Equal(23.4m, view!.Temperature?.Value);   // la última, no la anterior
            Assert.Equal(61m, view.Humidity?.Value);
        }
        finally
        {
            await using var context = _fx.NewContext();
            var ids = created.Select(c => c.Id).ToList();
            context.SensorReadings.RemoveRange(context.SensorReadings.Where(r => ids.Contains(r.SensorId)));
            context.Sensors.RemoveRange(context.Sensors.Where(s => ids.Contains(s.Id)));
            await context.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task La_vista_muestra_solo_lo_del_lote_en_curso()
    {
        await _fx.ResetCommandsAsync();
        DateTime original;
        await using (var context = _fx.NewContext())
        {
            var assignment = await context.CircuitAssignments.SingleAsync(a => a.CircuitId == _fx.CircuitId && a.LotId == _fx.LotId);
            original = assignment.ValidFromUtc;
            // El lote se asignó recién: las lecturas del seed (de hace 1 y 2 min) y una dosis de hace 10 min son de antes.
            assignment.ValidFromUtc = DateTime.UtcNow.AddSeconds(-30);
            var phDown = await _fx.ActuatorIdAsync(ControlContract.KindPhDown);
            context.ActuatorCommands.Add(new ActuatorCommand
            {
                Id = Guid.NewGuid(), NodeId = _fx.NodeId, CircuitId = _fx.CircuitId, ActuatorId = phDown, Source = ControlContract.SourceManual,
                Amount = 10m, Unit = ControlContract.UnitMl, Status = ControlContract.StatusExecuted, ExecutedAmount = 10m,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10), CompletedAtUtc = DateTime.UtcNow.AddMinutes(-10).AddSeconds(30),
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5), Parameter = ControlContract.ParameterPh,
            });
            await context.SaveChangesAsync();
        }
        try
        {
            var view = await _fx.Control.GetViewAsync(_fx.CircuitId);
            Assert.NotNull(view!.LotSinceUtc);
            Assert.Empty(view.Timeline);
            Assert.Equal(0, view.Kpis.Executed24h);

            var trend = await _fx.Control.GetTrendAsync(_fx.CircuitId, 6);
            Assert.Empty(trend!.Ph.Points);
            Assert.Empty(trend.Ph.Doses);
        }
        finally
        {
            await using var context = _fx.NewContext();
            await context.CircuitAssignments.Where(a => a.CircuitId == _fx.CircuitId && a.LotId == _fx.LotId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.ValidFromUtc, original));
            await _fx.ResetCommandsAsync();
        }
    }

    [Fact]
    public async Task Seed_crea_las_cuatro_bombas_de_cada_circuito_sin_duplicar()
    {
        await using (var context = _fx.NewContext())
            DbInitializer.SeedActuators(context);

        await using var check = _fx.NewContext();
        var kinds = await check.Actuators.Where(a => a.CircuitId == _fx.CircuitId).Select(a => a.Kind).ToListAsync();
        Assert.Equal(
            ControlContract.DefaultActuators.Select(a => a.Kind).OrderBy(k => k),
            kinds.OrderBy(k => k));
        Assert.Equal(10, await check.Actuators.CountAsync()); // 2 circuitos × 5 bombas (A y B por separado)

        var circuit = await check.Circuits.SingleAsync(c => c.Id == _fx.CircuitId);
        Assert.Equal(ControlContract.ModeManual, circuit.ControlMode);
        Assert.Equal(500m, circuit.ReservoirLiters);
    }

    [Fact]
    public async Task Dosis_manual_queda_pendiente_con_la_lectura_y_el_nivel_buscado()
    {
        await _fx.ResetCommandsAsync();
        var phDown = await _fx.ActuatorIdAsync(ControlContract.KindPhDown);
        var nutrient = await _fx.ActuatorIdAsync(ControlContract.KindNutrientA);

        Assert.True((await _fx.Control.RequestDoseAsync(_fx.CircuitId, phDown, 20m, "operador@test")).Succeeded);
        Assert.True((await _fx.Control.RequestDoseAsync(_fx.CircuitId, nutrient, 150m, "operador@test")).Succeeded);

        await using var context = _fx.NewContext();
        var ph = await context.ActuatorCommands.SingleAsync(c => c.ActuatorId == phDown);
        Assert.Equal(ControlContract.StatusPending, ph.Status);
        Assert.Equal(ControlContract.SourceManual, ph.Source);
        Assert.Equal("operador@test", ph.RequestedBy);
        Assert.Equal(20m, ph.Amount);
        Assert.Equal(ControlContract.UnitMl, ph.Unit);
        Assert.Equal(_fx.LotId, ph.LotId);
        Assert.Equal(ControlContract.ParameterPh, ph.Parameter);
        Assert.Equal(_fx.PhReadingId, ph.TriggerReadingId);
        Assert.Equal(6.82m, ph.ValueBefore);
        Assert.Equal((5.5m, 6.0m, 6.5m), (ph.BandMin!.Value, ph.Target!.Value, ph.BandMax!.Value));
        Assert.Equal(_fx.VegetativeStageId, ph.StageId);
        Assert.Equal(ph.CreatedAtUtc.AddMinutes(_fx.Options.CommandTtlMinutes), ph.ExpiresAtUtc);

        var ec = await context.ActuatorCommands.SingleAsync(c => c.ActuatorId == nutrient);
        Assert.Equal(ControlContract.ParameterEc, ec.Parameter);
        Assert.Equal(1.48m, ec.ValueBefore);
        Assert.Equal((1.2m, 1.5m, 1.8m), (ec.BandMin!.Value, ec.Target!.Value, ec.BandMax!.Value));
    }

    [Fact]
    public async Task Dosis_rechazada_por_limite_por_dosis_dosis_en_curso_y_maximo_de_24_horas()
    {
        await _fx.ResetCommandsAsync();
        var phUp = await _fx.ActuatorIdAsync(ControlContract.KindPhUp);

        var tooMuch = await _fx.Control.RequestDoseAsync(_fx.CircuitId, phUp, 51m, null);
        Assert.False(tooMuch.Succeeded);
        Assert.Contains("50 ml por dosis", tooMuch.Message);

        Assert.True((await _fx.Control.RequestDoseAsync(_fx.CircuitId, phUp, 40m, null)).Succeeded);
        var second = await _fx.Control.RequestDoseAsync(_fx.CircuitId, phUp, 10m, null);
        Assert.False(second.Succeeded);
        Assert.Contains("en curso", second.Message);

        // La primera (40 ml) se ejecuta; con 190 ml más ejecutados hoy, quedan 20 de los 250 ml del día.
        await DeliverAndExecuteAllAsync();
        await AddExecutedAsync(phUp, 190m);
        var overDay = await _fx.Control.RequestDoseAsync(_fx.CircuitId, phUp, 30m, null);
        Assert.False(overDay.Succeeded);
        Assert.Contains("quedan", overDay.Message);
        Assert.True((await _fx.Control.RequestDoseAsync(_fx.CircuitId, phUp, 20m, null)).Succeeded);
    }

    [Fact]
    public async Task Pi_recibe_lo_pendiente_y_se_lo_vuelve_a_mandar_hasta_que_responda()
    {
        await _fx.ResetCommandsAsync();
        var phDown = await _fx.ActuatorIdAsync(ControlContract.KindPhDown);
        await _fx.Control.RequestDoseAsync(_fx.CircuitId, phDown, 20m, null);
        var now = DateTime.UtcNow;

        var first = await _fx.Control.PollAsync(ControlSqlFixture.NodeIdentifier, now);

        var command = Assert.Single(first!);
        Assert.Equal(DeviceContract.TypeDoseCommand, command.Type);
        Assert.Equal(ControlSqlFixture.NodeIdentifier, command.NodeId);
        Assert.Equal(ControlContract.KindPhDown, command.Actuator);
        Assert.Equal(20m, command.Amount);
        Assert.Equal(ControlContract.UnitMl, command.Unit);
        Assert.Equal("manual", command.Source);
        Assert.Equal(DateTimeKind.Utc, command.ExpiresAt.Kind);
        Assert.Equal(new DeviceCommandReason(ControlContract.ParameterPh, 6.82m, 6.0m, ControlSqlFixture.PhReadingExternalId), command.Reason);

        await using (var context = _fx.NewContext())
        {
            var stored = await context.ActuatorCommands.SingleAsync(c => c.Id == command.CommandId);
            Assert.Equal(ControlContract.StatusDelivered, stored.Status);
            Assert.NotNull(stored.DeliveredAtUtc);
            Assert.NotNull((await context.IotNodes.SingleAsync(n => n.Id == _fx.NodeId)).LastCommandPollAtUtc);
        }

        // Sin resultado todavía: se vuelve a entregar (la Pi no repite un commandId).
        var again = await _fx.Control.PollAsync(ControlSqlFixture.NodeIdentifier, now.AddSeconds(10));
        Assert.Equal(command.CommandId, Assert.Single(again!).CommandId);

        Assert.Null(await _fx.Control.PollAsync("nodo-que-no-existe", now));
    }

    [Fact]
    public async Task Resultado_de_la_Pi_es_idempotente()
    {
        await _fx.ResetCommandsAsync();
        var phDown = await _fx.ActuatorIdAsync(ControlContract.KindPhDown);
        await _fx.Control.RequestDoseAsync(_fx.CircuitId, phDown, 20m, null);
        var now = DateTime.UtcNow;
        var command = Assert.Single((await _fx.Control.PollAsync(ControlSqlFixture.NodeIdentifier, now))!);

        var executed = new DeviceResultRequest("EXECUTED", 19.996m, DateTimeOffset.UtcNow.AddSeconds(-4), DateTimeOffset.UtcNow, null);
        var report = await _fx.Control.ReportResultAsync(ControlSqlFixture.NodeIdentifier, command.CommandId, executed, now);
        Assert.Equal(DeviceResultOutcome.Applied, report.Outcome);
        Assert.Equal(ControlContract.StatusExecuted, report.Status);

        Assert.Equal(DeviceResultOutcome.Duplicate,
            (await _fx.Control.ReportResultAsync(ControlSqlFixture.NodeIdentifier, command.CommandId, executed, now)).Outcome);
        Assert.Equal(DeviceResultOutcome.Conflict,
            (await _fx.Control.ReportResultAsync(ControlSqlFixture.NodeIdentifier, command.CommandId,
                new DeviceResultRequest("FAILED", 5m, null, null, "bomba"), now)).Outcome);
        Assert.Equal(DeviceResultOutcome.Invalid,
            (await _fx.Control.ReportResultAsync(ControlSqlFixture.NodeIdentifier, command.CommandId,
                new DeviceResultRequest("LISTO", null, null, null, null), now)).Outcome);
        Assert.Equal(DeviceResultOutcome.CommandNotFound,
            (await _fx.Control.ReportResultAsync(ControlSqlFixture.NodeIdentifier, Guid.NewGuid(), executed, now)).Outcome);
        Assert.Equal(DeviceResultOutcome.CommandNotFound,
            (await _fx.Control.ReportResultAsync("ctl-node-02", command.CommandId, executed, now)).Outcome);
        Assert.Equal(DeviceResultOutcome.NodeNotFound,
            (await _fx.Control.ReportResultAsync("nodo-que-no-existe", command.CommandId, executed, now)).Outcome);

        await using var context = _fx.NewContext();
        var stored = await context.ActuatorCommands.SingleAsync(c => c.Id == command.CommandId);
        Assert.Equal(ControlContract.StatusExecuted, stored.Status);
        Assert.Equal(20m, stored.ExecutedAmount);
        Assert.NotNull(stored.CompletedAtUtc);
        Assert.NotNull(stored.DeviceStartedAtUtc);

        // Ejecutado: ya no se entrega más.
        Assert.Empty((await _fx.Control.PollAsync(ControlSqlFixture.NodeIdentifier, now.AddSeconds(5)))!);
    }

    [Fact]
    public async Task Barrido_vence_lo_que_nadie_tomo_y_deja_sin_confirmar_lo_entregado_sin_respuesta()
    {
        await _fx.ResetCommandsAsync();
        var phDown = await _fx.ActuatorIdAsync(ControlContract.KindPhDown);
        var water = await _fx.ActuatorIdAsync(ControlContract.KindWater);
        var now = DateTime.UtcNow;

        await _fx.Control.RequestDoseAsync(_fx.CircuitId, phDown, 20m, null);
        var delivered = Assert.Single((await _fx.Control.PollAsync(ControlSqlFixture.NodeIdentifier, now))!);
        await _fx.Control.RequestDoseAsync(_fx.CircuitId, water, 10m, null); // nadie la pide

        // Vencido el plazo (5 min): lo pendiente vence; lo entregado todavía tiene el margen (2 min).
        var afterTtl = now.AddMinutes(_fx.Options.CommandTtlMinutes + 0.5);
        var first = await _fx.Control.SweepAsync(afterTtl);
        Assert.Equal((1, 0), (first.Expired, first.Unconfirmed));
        Assert.Empty((await _fx.Control.PollAsync(ControlSqlFixture.NodeIdentifier, afterTtl))!);

        var afterGrace = now.AddMinutes(_fx.Options.CommandTtlMinutes + _fx.Options.UnconfirmedGraceMinutes + 0.5);
        var second = await _fx.Control.SweepAsync(afterGrace);
        Assert.Equal((0, 1), (second.Expired, second.Unconfirmed));

        // El resultado que llega tarde se acepta: es lo que pasó en el tanque.
        var late = await _fx.Control.ReportResultAsync(ControlSqlFixture.NodeIdentifier, delivered.CommandId,
            new DeviceResultRequest("EXECUTED", 20m, null, null, null), afterGrace.AddMinutes(1));
        Assert.Equal(DeviceResultOutcome.Applied, late.Outcome);

        await using var context = _fx.NewContext();
        Assert.Equal(ControlContract.StatusExpired,
            (await context.ActuatorCommands.SingleAsync(c => c.ActuatorId == water)).Status);
        Assert.Equal(ControlContract.StatusExecuted,
            (await context.ActuatorCommands.SingleAsync(c => c.Id == delivered.CommandId)).Status);
    }

    [Fact]
    public async Task Cancelar_solo_mientras_la_Pi_no_la_tomo()
    {
        await _fx.ResetCommandsAsync();
        var phDown = await _fx.ActuatorIdAsync(ControlContract.KindPhDown);
        var nutrient = await _fx.ActuatorIdAsync(ControlContract.KindNutrientA);

        await _fx.Control.RequestDoseAsync(_fx.CircuitId, phDown, 20m, null);
        await using (var context = _fx.NewContext())
        {
            var pending = await context.ActuatorCommands.SingleAsync(c => c.ActuatorId == phDown);
            Assert.True((await _fx.Control.CancelAsync(_fx.CircuitId, pending.Id, "admin")).Succeeded);
        }

        // Cancelada, la bomba queda libre y la Pi no la recibe.
        await _fx.Control.RequestDoseAsync(_fx.CircuitId, nutrient, 100m, null);
        var delivered = Assert.Single((await _fx.Control.PollAsync(ControlSqlFixture.NodeIdentifier, DateTime.UtcNow))!);
        Assert.Equal(ControlContract.KindNutrientA, delivered.Actuator);

        var late = await _fx.Control.CancelAsync(_fx.CircuitId, delivered.CommandId, "admin");
        Assert.False(late.Succeeded);

        await using var check = _fx.NewContext();
        Assert.Equal(ControlContract.StatusCancelled,
            (await check.ActuatorCommands.SingleAsync(c => c.ActuatorId == phDown)).Status);
        Assert.True((await _fx.Control.RequestDoseAsync(_fx.CircuitId, phDown, 10m, null)).Succeeded);
    }

    [Fact]
    public async Task Barrido_guarda_el_nivel_del_lote_solo_cuando_cambia()
    {
        await using (var context = _fx.NewContext())
            await context.LotSetpointHistories.ExecuteDeleteAsync();

        try
        {
            var now = DateTime.UtcNow;
            Assert.Equal(1, (await _fx.Control.SweepAsync(now)).SetpointsRecorded);
            Assert.Equal(0, (await _fx.Control.SweepAsync(now.AddMinutes(1))).SetpointsRecorded);

            await SetStageAsync(_fx.MaturityStageId);
            Assert.Equal(1, (await _fx.Control.SweepAsync(now.AddMinutes(2))).SetpointsRecorded);

            await using var check = _fx.NewContext();
            var rows = await check.LotSetpointHistories.Where(h => h.LotId == _fx.LotId).OrderBy(h => h.ValidFromUtc).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal((5.5m, 6.0m, 6.5m), (rows[0].PhMin!.Value, rows[0].PhTarget!.Value, rows[0].PhMax!.Value));
            Assert.Equal((1.2m, 1.5m, 1.8m, _fx.VegetativeStageId), (rows[0].EcMin!.Value, rows[0].EcTarget!.Value, rows[0].EcMax!.Value, rows[0].StageId!.Value));
            Assert.Equal((1.5m, 1.7m, 1.8m, _fx.MaturityStageId), (rows[1].EcMin!.Value, rows[1].EcTarget!.Value, rows[1].EcMax!.Value, rows[1].StageId!.Value));
            Assert.Equal(ControlContract.SetpointSourceStage, rows[1].Source);
        }
        finally
        {
            await SetStageAsync(_fx.VegetativeStageId);
        }
    }

    [Fact]
    public async Task Vista_muestra_solucion_bombas_y_conversacion()
    {
        await _fx.ResetCommandsAsync();
        var phDown = await _fx.ActuatorIdAsync(ControlContract.KindPhDown);
        await _fx.Control.RequestDoseAsync(_fx.CircuitId, phDown, 20m, "operador@test");
        await DeliverAndExecuteAllAsync();

        var view = await _fx.Control.GetViewAsync(_fx.CircuitId);

        Assert.NotNull(view);
        Assert.Equal(ControlSqlFixture.NodeIdentifier, view.NodeIdentifier);
        Assert.Equal("Circuito Control", view.CircuitName);
        Assert.Equal(ControlContract.ModeManual, view.Mode);
        Assert.Equal(500m, view.ReservoirLiters);
        Assert.Equal(_fx.LotId, view.LotId);
        Assert.Equal("Crecimiento vegetativo", view.StageName);
        Assert.Equal(SolutionState.Lower, view.Ph.State);   // 6,8 firme sobre 6,5
        Assert.Equal(SolutionState.InRange, view.Ec.State); // 1,48 dentro de 1,2–1,8
        Assert.Equal(6.0m, view.Ph.Band.Target);

        Assert.Equal(
            ControlContract.DefaultActuators.Select(a => a.Kind),
            view.Actuators.Select(a => a.Kind));
        var pump = view.Actuators.Single(a => a.Id == phDown);
        Assert.Equal(20m, pump.UsedLast24h);
        Assert.Null(pump.InFlight);
        Assert.Equal(new ControlKpis(1, 0, 0), view.Kpis);

        var kinds = view.Timeline.Select(e => e.Kind).ToList();
        Assert.Contains(ControlTimelineKind.Requested, kinds);
        Assert.Contains(ControlTimelineKind.Delivered, kinds);
        Assert.Contains(ControlTimelineKind.Result, kinds);
        var readings = view.Timeline.Single(e => e.Kind == ControlTimelineKind.Readings).Readings!;
        Assert.Equal((2, 6.82m, 1.48m), (readings.Count, readings.Ph!.Value, readings.Ec!.Value));
    }

    [Fact]
    public async Task Tanque_acepta_litros_validos()
    {
        Assert.False((await _fx.Control.SetReservoirAsync(_fx.CircuitId, 0m, "admin")).Succeeded);
        Assert.True((await _fx.Control.SetReservoirAsync(_fx.CircuitId, 750m, "admin")).Succeeded);
        await using (var context = _fx.NewContext())
            Assert.Equal(750m, (await context.Circuits.SingleAsync(c => c.Id == _fx.CircuitId)).ReservoirLiters);
        Assert.True((await _fx.Control.SetReservoirAsync(_fx.CircuitId, 500m, "admin")).Succeeded);
    }

    // ------------------------------------------------------------------

    private async Task DeliverAndExecuteAllAsync()
    {
        var now = DateTime.UtcNow;
        foreach (var command in (await _fx.Control.PollAsync(ControlSqlFixture.NodeIdentifier, now))!)
        {
            await _fx.Control.ReportResultAsync(ControlSqlFixture.NodeIdentifier, command.CommandId,
                new DeviceResultRequest("EXECUTED", command.Amount, null, null, null), now);
        }
    }

    private async Task AddExecutedAsync(int actuatorId, decimal amount)
    {
        await using var context = _fx.NewContext();
        var now = DateTime.UtcNow;
        context.ActuatorCommands.Add(new ActuatorCommand
        {
            Id = Guid.NewGuid(),
            NodeId = _fx.NodeId,
            CircuitId = _fx.CircuitId,
            ActuatorId = actuatorId,
            Amount = amount,
            Unit = ControlContract.UnitMl,
            Status = ControlContract.StatusExecuted,
            ExecutedAmount = amount,
            CreatedAtUtc = now.AddHours(-3),
            ExpiresAtUtc = now.AddHours(-3).AddMinutes(5),
            CompletedAtUtc = now.AddHours(-3),
            Parameter = ControlContract.ParameterPh,
        });
        await context.SaveChangesAsync();
    }

    private async Task SetStageAsync(int stageId)
    {
        await using var context = _fx.NewContext();
        await context.Lots.Where(l => l.Id == _fx.LotId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.AppliedPhenologicalStageId, (int?)stageId));
    }
}
