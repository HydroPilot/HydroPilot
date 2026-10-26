using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Control;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Services.SensorFaults;

/// <summary>
/// Errores de sensores que se ven al llegar las lecturas (dentro de la misma transacción de la ingesta, así el
/// control ya los ve cuando evalúa el tanque): lectura fuera de lo normal, salto entre lecturas seguidas y la
/// vuelta de las lecturas de un sensor que estaba sin datos. Solo mira lecturas en vivo: las retransmisiones
/// viejas (STALE o anteriores a la última guardada del sensor) no abren errores.
/// </summary>
public static class SensorFaultDetector
{
    public sealed record IncomingReading(Sensor Sensor, SensorReading Reading);

    public static async Task DetectAsync(
        HydroPilotDbContext context,
        IotNode node,
        IReadOnlyList<IncomingReading> readings,
        DateTime nowUtc,
        int mixingMinutes,
        int silenceFactor,
        CancellationToken ct)
    {
        if (readings.Count == 0)
            return;

        // Abiertos del nodo (con seguimiento: la vuelta de las lecturas resuelve la falta de datos).
        var open = await context.SensorFaults.Where(f => f.NodeId == node.Id && f.ResolvedAtUtc == null).ToListAsync(ct);
        var limits = await SensorFaultRules.LoadLimitsAsync(context, ct);
        var maxGap = SensorFaultRules.SilenceAfter(node.ExpectedIntervalSeconds, silenceFactor);

        foreach (var group in readings.GroupBy(r => r.Sensor.Id))
        {
            var sensor = group.First().Sensor;
            var type = sensor.SensorType?.Name ?? string.Empty;
            var sensorLimits = limits.GetValueOrDefault(type);
            var previous = await context.SensorReadings.AsNoTracking()
                .Where(r => r.SensorId == sensor.Id)
                .OrderByDescending(r => r.ObservedAtUtc)
                .Select(r => new { r.Value, r.ObservedAtUtc, r.Quality })
                .FirstOrDefaultAsync(ct) is { } last
                ? new PreviousReading(last.Value, last.ObservedAtUtc, TelemetryQualityPolicy.IsOperationallyUsable(last.Quality))
                : null;

            foreach (var reading in group.Select(r => r.Reading).OrderBy(r => r.ObservedAtUtc))
            {
                if (previous is not null && reading.ObservedAtUtc <= previous.ObservedAtUtc)
                    continue;

                var usable = TelemetryQualityPolicy.IsOperationallyUsable(reading.Quality);
                var current = new PreviousReading(reading.Value, reading.ObservedAtUtc, usable);
                if (reading.Quality == TelemetryContract.QualityStale)
                {
                    previous = current;
                    continue;
                }

                // Volvieron las lecturas: la falta de datos se resuelve sola.
                foreach (var silent in open.Where(f => f.SensorId == sensor.Id && f.Kind == SensorFaultKinds.SinDatos && f.ResolvedAtUtc == null))
                    silent.ResolvedAtUtc = nowUtc;

                if (sensorLimits is not null)
                {
                    if (SensorFaultRules.IsOutOfRange(reading.Value, sensorLimits))
                    {
                        if (!IsOpen(open, sensor.Id, SensorFaultKinds.FueraDeRango)
                            && SensorFaultRules.ShouldOpenRange(
                                previous is not null && SensorFaultRules.IsOutOfRange(previous.Value, sensorLimits),
                                previous?.ObservedAtUtc,
                                await LastDetectedAsync(context, sensor.Id, SensorFaultKinds.FueraDeRango, ct)))
                        {
                            open.Add(Add(context, node, sensor, type, SensorFaultKinds.FueraDeRango, reading, previous,
                                SensorFaultRules.RangeDetail(sensorLimits)));
                        }
                    }
                    else if (previous is not null
                             && !IsOpen(open, sensor.Id, SensorFaultKinds.Salto)
                             && SensorFaultRules.IsJump(previous, reading.Value, reading.ObservedAtUtc, sensorLimits, maxGap, doseInBetween: false)
                             && !await DoseInBetweenAsync(context, sensor.CircuitId, type, previous.ObservedAtUtc.AddMinutes(-mixingMinutes), reading.ObservedAtUtc, ct))
                    {
                        open.Add(Add(context, node, sensor, type, SensorFaultKinds.Salto, reading, previous,
                            SensorFaultRules.JumpDetail(sensorLimits)));
                    }
                }

                previous = current;
            }
        }
    }

    private static bool IsOpen(List<SensorFault> open, int sensorId, string kind) =>
        open.Any(f => f.SensorId == sensorId && f.Kind == kind && f.ResolvedAtUtc == null);

    private static SensorFault Add(
        HydroPilotDbContext context, IotNode node, Sensor sensor, string type, string kind,
        SensorReading reading, PreviousReading? previous, string detail)
    {
        var fault = new SensorFault
        {
            NodeId = node.Id,
            SensorId = sensor.Id,
            SensorType = type,
            Kind = kind,
            Value = reading.Value,
            PreviousValue = previous?.Value,
            Detail = detail,
            DetectedAtUtc = reading.ObservedAtUtc,
        };
        context.SensorFaults.Add(fault);
        return fault;
    }

    private static Task<DateTime?> LastDetectedAsync(HydroPilotDbContext context, int sensorId, string kind, CancellationToken ct) =>
        context.SensorFaults.AsNoTracking()
            .Where(f => f.SensorId == sensorId && f.Kind == kind)
            .OrderByDescending(f => f.DetectedAtUtc)
            .Select(f => (DateTime?)f.DetectedAtUtc)
            .FirstOrDefaultAsync(ct);

    /// <summary>Dosis aplicadas en el circuito en el lapso (solo pH y CE: las dosis mueven la solución, no el ambiente).</summary>
    private static async Task<bool> DoseInBetweenAsync(
        HydroPilotDbContext context, int? circuitId, string sensorType, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        if (!SensorFaultRules.BlocksControl(sensorType) || circuitId is null)
            return false;
        return await context.ActuatorCommands.AnyAsync(c =>
            c.CircuitId == circuitId
            && c.CompletedAtUtc != null && c.CompletedAtUtc >= fromUtc && c.CompletedAtUtc <= toUtc
            && (c.Status == ControlContract.StatusExecuted || (c.Status == ControlContract.StatusFailed && c.ExecutedAmount > 0)), ct);
    }
}
