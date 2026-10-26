using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Control;
using HydroPilotWeb.Models.Notifications;
using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Control;
using HydroPilotWeb.Services.Notifications;
using HydroPilotWeb.Services.SensorFaults;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Errores de sensores contra SQL Server: la ingesta abre los errores por valor (salto, fuera de lo normal), el
/// control queda frenado hasta que alguien los resuelve, el barrido abre "sin datos" y la vuelta de las lecturas lo
/// cierra, y cada error tiene su alerta.
/// </summary>
[Collection("control-sql")]
public class SensorFaultSqlServerTests
{
    private readonly ControlSqlFixture _fx;

    public SensorFaultSqlServerTests(ControlSqlFixture fx) => _fx = fx;

    [Fact]
    public async Task Salto_de_pH_en_la_ingesta_frena_el_control_hasta_marcarlo_resuelto()
    {
        var prefix = await StartAsync();
        try
        {
            var now = DateTime.UtcNow;
            await AddReadingAsync(_fx.NodeId, "ph-solucion", 6.0m, now.AddSeconds(-20), prefix);
            await IngestAsync(ControlSqlFixture.NodeIdentifier, "ph-solucion", 5.3m, now.AddSeconds(-5), "pH", prefix);

            var fault = await SingleOpenAsync(SensorFaultKinds.Salto);
            Assert.Equal(AnomalyContract.SensorTypePh, fault.SensorType);
            Assert.Equal(6.0m, fault.PreviousValue);
            Assert.Equal(5.3m, fault.Value);
            Assert.Equal("más de 0,5 entre lecturas", fault.Detail);

            var blocked = await _fx.Control.EvaluateCircuitAsync(_fx.CircuitId, DateTime.UtcNow);
            Assert.Equal(ControlEngine.Codes.SensorFault, blocked.Code);
            Assert.Equal(SensorFaultKinds.Salto, blocked.SensorFaultKind);

            var view = await _fx.Control.GetViewAsync(_fx.CircuitId);
            Assert.Equal(ControlEngine.Codes.SensorFault, view!.Status.Code);
            var shown = Assert.Single(await Faults().RecentAsync(_fx.CircuitId, 20));
            Assert.True(shown.BlocksControl);
            Assert.True(shown.ResolvedByUser);
            Assert.Null(shown.ResolvedAtUtc);

            Assert.True(await Faults().ResolveAsync(fault.Id, "tester"));
            await using (var context = _fx.NewContext())
            {
                var resolved = await context.SensorFaults.SingleAsync(f => f.Id == fault.Id);
                Assert.NotNull(resolved.ResolvedAtUtc);
                Assert.Equal("tester", resolved.ResolvedBy);
            }
            // Sigue en la lista de Episodios, como resuelto.
            var listed = Assert.Single(await Faults().RecentAsync(_fx.CircuitId, 20));
            Assert.Equal("tester", listed.ResolvedBy);
            var resumed = await _fx.Control.EvaluateCircuitAsync(_fx.CircuitId, DateTime.UtcNow);
            Assert.NotEqual(ControlEngine.Codes.SensorFault, resumed.Code);
        }
        finally
        {
            await CleanupAsync(prefix);
        }
    }

    [Fact]
    public async Task Fuera_de_lo_normal_no_vuelve_a_frenar_si_ya_se_resolvio_y_las_lecturas_siguen_afuera()
    {
        var prefix = await StartAsync();
        try
        {
            var now = DateTime.UtcNow;
            await AddReadingAsync(_fx.NodeId, "ph-solucion", 6.0m, now.AddSeconds(-60), prefix);
            await IngestAsync(ControlSqlFixture.NodeIdentifier, "ph-solucion", 3.5m, now.AddSeconds(-50), "pH", prefix);

            var first = await SingleOpenAsync(SensorFaultKinds.FueraDeRango);
            Assert.Equal(3.5m, first.Value);
            Assert.Equal("rango normal 4–8", first.Detail);
            Assert.True(await Faults().ResolveAsync(first.Id, "tester"));

            // Sigue afuera: alguien ya lo revisó, se toma como real.
            await IngestAsync(ControlSqlFixture.NodeIdentifier, "ph-solucion", 3.4m, now.AddSeconds(-40), "pH", prefix);
            // Vuelve a lo normal (volver no es un salto) y sale otra vez: error nuevo.
            await IngestAsync(ControlSqlFixture.NodeIdentifier, "ph-solucion", 6.0m, now.AddSeconds(-30), "pH", prefix);
            await IngestAsync(ControlSqlFixture.NodeIdentifier, "ph-solucion", 3.3m, now.AddSeconds(-20), "pH", prefix);

            await using var context = _fx.NewContext();
            var faults = await context.SensorFaults.Where(f => f.NodeId == _fx.NodeId).OrderBy(f => f.Id).ToListAsync();
            Assert.Equal(2, faults.Count);
            Assert.All(faults, f => Assert.Equal(SensorFaultKinds.FueraDeRango, f.Kind));
            Assert.Equal(3.3m, faults[1].Value);
            Assert.Null(faults[1].ResolvedAtUtc);
        }
        finally
        {
            await CleanupAsync(prefix);
        }
    }

    [Fact]
    public async Task Lectura_fuera_del_rango_fisico_tambien_abre_el_error()
    {
        var prefix = await StartAsync();
        try
        {
            await IngestAsync(ControlSqlFixture.NodeIdentifier, "ph-solucion", 15m, DateTime.UtcNow.AddSeconds(-5), "pH", prefix);

            var fault = await SingleOpenAsync(SensorFaultKinds.FueraDeRango);
            Assert.Equal(15m, fault.Value);
        }
        finally
        {
            await CleanupAsync(prefix);
        }
    }

    [Fact]
    public async Task Sin_lecturas_recientes_de_pH_frena_aunque_el_barrido_no_haya_corrido()
    {
        var prefix = await StartAsync();
        try
        {
            // Intervalo de 15 s: a los 30 s sin lecturas el sensor ya está sin datos (las del seed tienen más de un minuto).
            await using (var context = _fx.NewContext())
                await context.IotNodes.Where(n => n.Id == _fx.NodeId).ExecuteUpdateAsync(s => s.SetProperty(n => n.ExpectedIntervalSeconds, 15));

            var decision = await _fx.Control.EvaluateCircuitAsync(_fx.CircuitId, DateTime.UtcNow);
            Assert.Equal(ControlEngine.Codes.SensorFault, decision.Code);
            Assert.Equal(SensorFaultKinds.SinDatos, decision.SensorFaultKind);
        }
        finally
        {
            await using (var context = _fx.NewContext())
                await context.IotNodes.Where(n => n.Id == _fx.NodeId).ExecuteUpdateAsync(s => s.SetProperty(n => n.ExpectedIntervalSeconds, 300));
            await CleanupAsync(prefix);
        }
    }

    [Fact]
    public async Task Barrido_abre_sin_datos_con_su_alerta_y_la_vuelta_de_las_lecturas_lo_cierra()
    {
        var prefix = await StartAsync();
        try
        {
            // El nodo de repuesto manda (está vivo) pero su sonda de pH no manda hace media hora. Su circuito tiene lote
            // (un circuito sin lote no se vigila).
            var now = DateTime.UtcNow;
            await AddReadingAsync(_fx.SpareNodeId, "ph-solucion", 6.0m, now.AddMinutes(-30), prefix);
            await using (var context = _fx.NewContext())
            {
                await context.IotNodes.Where(n => n.Id == _fx.SpareNodeId).ExecuteUpdateAsync(s => s.SetProperty(n => n.LastAcceptedAt, now));
                context.CircuitAssignments.Add(new CircuitAssignment
                {
                    CircuitId = _fx.SpareCircuitId, LotId = _fx.LotId, ValidFromUtc = now.AddHours(-1), Source = "test-sin-datos", CreatedAt = now,
                });
                await context.SaveChangesAsync();
            }

            var faults = Faults(withAlerts: true);
            await faults.SweepAsync(DateTime.UtcNow);

            SensorFault silent;
            await using (var context = _fx.NewContext())
            {
                silent = await context.SensorFaults.SingleAsync(f => f.SensorId == _fx.SparePhSensorId && f.ResolvedAtUtc == null);
                Assert.Equal(SensorFaultKinds.SinDatos, silent.Kind);
                Assert.NotNull(silent.LastReadingAtUtc);

                var alert = await context.NotificationAlerts.SingleAsync(a => a.Fingerprint == SensorFaultAlerts.Fingerprint(_fx.SpareNodeId, silent.Id));
                Assert.Equal(NotificationTypes.ErrorSensor, alert.Type);
                Assert.Equal(NotificationSeverities.Alta, alert.Severity);
                Assert.Equal("Circuito Repuesto: sensor de pH sin datos", alert.Title);
                Assert.False(alert.IsResolved);
            }

            // No se resuelve a mano.
            Assert.False(await faults.ResolveAsync(silent.Id, "tester"));

            await IngestAsync("ctl-node-02", "ph-solucion", 6.1m, DateTime.UtcNow.AddSeconds(-5), "pH", prefix);
            await faults.SweepAsync(DateTime.UtcNow);

            await using (var context = _fx.NewContext())
            {
                var closed = await context.SensorFaults.SingleAsync(f => f.Id == silent.Id);
                Assert.NotNull(closed.ResolvedAtUtc);
                Assert.Null(closed.ResolvedBy);
                Assert.True(await context.NotificationAlerts.AnyAsync(a => a.Fingerprint == SensorFaultAlerts.Fingerprint(_fx.SpareNodeId, silent.Id) && a.IsResolved));
            }
        }
        finally
        {
            await using (var context = _fx.NewContext())
            {
                await context.IotNodes.Where(n => n.Id == _fx.SpareNodeId).ExecuteUpdateAsync(s => s.SetProperty(n => n.LastAcceptedAt, (DateTime?)null));
                await context.CircuitAssignments.Where(a => a.Source == "test-sin-datos").ExecuteDeleteAsync();
            }
            await CleanupAsync(prefix);
        }
    }

    [Fact]
    public async Task La_lista_muestra_los_errores_del_lote_en_curso_y_todos_los_abiertos()
    {
        var prefix = await StartAsync();
        try
        {
            var since = DateTime.UtcNow.AddHours(-1);
            await using (var context = _fx.NewContext())
            {
                var ph = await context.Sensors.SingleAsync(s => s.NodeId == _fx.NodeId && s.TechnicalKey == "ph-solucion");
                var ec = await context.Sensors.SingleAsync(s => s.NodeId == _fx.NodeId && s.TechnicalKey == "ec-solucion");
                SensorFault Fault(Sensor sensor, string type, string kind, DateTime detected, DateTime? resolved) => new()
                {
                    NodeId = _fx.NodeId, SensorId = sensor.Id, SensorType = type, Kind = kind,
                    DetectedAtUtc = detected, ResolvedAtUtc = resolved, ResolvedBy = resolved is null ? null : "tester",
                };
                context.SensorFaults.AddRange(
                    Fault(ph, AnomalyContract.SensorTypePh, SensorFaultKinds.Salto, since.AddHours(-5), since.AddHours(-4)),        // lote anterior
                    Fault(ph, AnomalyContract.SensorTypePh, SensorFaultKinds.FueraDeRango, since.AddMinutes(10), since.AddMinutes(20)), // de este lote
                    Fault(ec, AnomalyContract.SensorTypeCe, SensorFaultKinds.Salto, since.AddHours(-2), null));                    // abierto
                await context.SaveChangesAsync();
            }

            var listed = await Faults().RecentAsync(_fx.CircuitId, 20, since);

            Assert.Equal(2, listed.Count);
            Assert.Contains(listed, f => f.Kind == SensorFaultKinds.FueraDeRango);
            Assert.Contains(listed, f => f.ResolvedAtUtc is null && f.SensorType == AnomalyContract.SensorTypeCe);
            Assert.Equal(3, (await Faults().RecentAsync(_fx.CircuitId, 20)).Count);
        }
        finally
        {
            await CleanupAsync(prefix);
        }
    }

    [Fact]
    public async Task Temperatura_inusual_avisa_sin_frenar_el_control()
    {
        var prefix = await StartAsync();
        int sensorId;
        await using (var context = _fx.NewContext())
        {
            var type = await context.SensorTypes.FirstOrDefaultAsync(t => t.Name == AnomalyContract.SensorTypeTemperatura)
                       ?? context.SensorTypes.Add(new SensorType { Name = AnomalyContract.SensorTypeTemperatura }).Entity;
            var sensor = new Sensor { NodeId = _fx.NodeId, SensorType = type, Name = $"sf-temp-{prefix}", TechnicalKey = "temp-ambiente", IsActive = true, CreatedAt = DateTime.UtcNow };
            context.Sensors.Add(sensor);
            await context.SaveChangesAsync();
            sensorId = sensor.Id;
        }
        try
        {
            await IngestAsync(ControlSqlFixture.NodeIdentifier, "temp-ambiente", 45m, DateTime.UtcNow.AddSeconds(-5), null, prefix);

            var fault = await SingleOpenAsync(SensorFaultKinds.FueraDeRango);
            Assert.Equal(AnomalyContract.SensorTypeTemperatura, fault.SensorType);
            Assert.Equal("rango normal 10–40 °C", fault.Detail);

            var decision = await _fx.Control.EvaluateCircuitAsync(_fx.CircuitId, DateTime.UtcNow);
            Assert.NotEqual(ControlEngine.Codes.SensorFault, decision.Code);

            Assert.False(Assert.Single(await Faults().RecentAsync(_fx.CircuitId, 20)).BlocksControl);
        }
        finally
        {
            await CleanupAsync(prefix);
            await using var context = _fx.NewContext();
            await context.SensorReadings.Where(r => r.SensorId == sensorId).ExecuteDeleteAsync();
            await context.Sensors.Where(s => s.Id == sensorId).ExecuteDeleteAsync();
        }
    }

    // ------------------------------------------------------------------

    /// <summary>Sin comandos, pausas ni errores; la Pi consultando y el nodo en automático. Devuelve el prefijo de las lecturas del test.</summary>
    private async Task<string> StartAsync()
    {
        await _fx.ResetCommandsAsync();
        await _fx.Control.PollAsync(ControlSqlFixture.NodeIdentifier, DateTime.UtcNow);
        await using var context = _fx.NewContext();
        await context.Circuits.Where(c => c.Id == _fx.CircuitId).ExecuteUpdateAsync(s => s.SetProperty(c => c.ControlMode, ControlContract.ModeAuto));
        return $"sf-{Guid.NewGuid():N}"[..11];
    }

    private async Task CleanupAsync(string prefix)
    {
        await _fx.ResetCommandsAsync();
        await using var context = _fx.NewContext();
        await context.SensorReadings.Where(r => r.ExternalReadingId.StartsWith(prefix)).ExecuteDeleteAsync();
        await context.Circuits.Where(c => c.Id == _fx.CircuitId).ExecuteUpdateAsync(s => s.SetProperty(c => c.ControlMode, ControlContract.ModeManual));
    }

    private SensorFaultService Faults(bool withAlerts = false) =>
        new(_fx.Factory, Options.Create(new TelemetryOptions()), NullLogger<SensorFaultService>.Instance,
            withAlerts ? new NotificationService(_fx.Factory, NullLogger<NotificationService>.Instance) : null);

    private async Task<SensorFault> SingleOpenAsync(string kind)
    {
        await using var context = _fx.NewContext();
        return await context.SensorFaults.SingleAsync(f => f.Kind == kind && f.ResolvedAtUtc == null);
    }

    /// <summary>Lectura guardada directo (sin pasar por la ingesta): la "anterior" con la que se compara.</summary>
    private async Task AddReadingAsync(int nodeId, string sensorKey, decimal value, DateTime observedAt, string prefix)
    {
        await using var context = _fx.NewContext();
        var sensor = await context.Sensors.SingleAsync(s => s.NodeId == nodeId && s.TechnicalKey == sensorKey);
        context.SensorReadings.Add(new SensorReading
        {
            SensorId = sensor.Id, NodeId = nodeId, MeasurementUnitId = sensor.MeasurementUnitId, Value = value,
            Timestamp = observedAt, ObservedAtUtc = observedAt, ReceivedAtUtc = observedAt,
            ExternalReadingId = $"{prefix}-{Guid.NewGuid():N}"[..24], Quality = TelemetryContract.QualityValid,
            IngestionResult = TelemetryContract.ResultAceptada, CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private async Task IngestAsync(string node, string sensorRef, decimal value, DateTime observedAt, string? unit, string prefix)
    {
        var telemetry = Options.Create(new TelemetryOptions());
        var ingestion = new TelemetryIngestionService(_fx.Factory, new TelemetryValidationService(telemetry), telemetry,
            NullLogger<TelemetryIngestionService>.Instance);
        var batchId = $"{prefix}-{Guid.NewGuid():N}"[..24];
        var observed = observedAt.ToString("o");
        var unitJson = unit is null ? string.Empty : $", \"unit\": \"{unit}\"";
        var body = JsonDocument.Parse($$"""
            {
              "batchId": "{{batchId}}",
              "nodeId": "{{node}}",
              "schemaVersion": "2",
              "sentAt": "{{observed}}",
              "readings": [
                { "readingId": "{{batchId}}-r", "sensorRef": "{{sensorRef}}", "value": {{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}{{unitJson}}, "observedAt": "{{observed}}" }
              ]
            }
            """).RootElement;
        var outcome = await ingestion.IngestAsync(TelemetryContractParser.Parse(body).Envelope!, CancellationToken.None);
        Assert.Equal(IngestBatchStatus.Processed, outcome.Status);
    }
}
