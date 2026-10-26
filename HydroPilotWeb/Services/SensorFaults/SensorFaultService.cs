using System.Globalization;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Notifications;
using HydroPilotWeb.Services.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Services.SensorFaults;

/// <summary>Error de sensor (abierto o resuelto), como lo muestra la lista de Episodios de Optimización.</summary>
public sealed record SensorFaultDto(
    int Id,
    int SensorId,
    string SensorType,
    string Kind,
    decimal? Value,
    decimal? PreviousValue,
    string? Detail,
    DateTime DetectedAtUtc,
    DateTime? LastReadingAtUtc,
    bool BlocksControl,
    bool ResolvedByUser,
    DateTime? ResolvedAtUtc = null,
    string? ResolvedBy = null);

public sealed record SensorFaultSweepResult(int Opened, int AlertsCreated, int AlertsResolved);

/// <summary>
/// Errores de sensores (<see cref="SensorFaultRules"/>): la falta de datos (barrido periódico), las alertas de
/// cada error y la resolución manual de los errores por valor. Los errores por valor se detectan al llegar las
/// lecturas (<see cref="SensorFaultDetector"/>).
/// </summary>
public sealed class SensorFaultService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly NotificationService? _notifications;
    private readonly TelemetryOptions _telemetry;
    private readonly ILogger<SensorFaultService> _logger;

    public SensorFaultService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        IOptions<TelemetryOptions> telemetry,
        ILogger<SensorFaultService> logger,
        NotificationService? notifications = null)
    {
        _dbFactory = dbFactory;
        _telemetry = telemetry.Value;
        _logger = logger;
        _notifications = notifications;
    }

    /// <summary>
    /// Errores para la lista de Episodios de un circuito: los de sus sensores (pH, CE) y los del invernadero
    /// (temperatura, humedad). Todos los abiertos y los últimos resueltos (como mucho <paramref name="take"/>); con
    /// <paramref name="sinceUtc"/> (inicio del lote en curso), los resueltos de antes no.
    /// </summary>
    public async Task<IReadOnlyList<SensorFaultDto>> RecentAsync(int circuitId, int take, DateTime? sinceUtc = null, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var greenhouseId = await context.Circuits.AsNoTracking().Where(c => c.Id == circuitId).Select(c => c.GreenhouseId).FirstOrDefaultAsync(ct);
        var faults = await context.SensorFaults.AsNoTracking()
            .Where(f => (f.Sensor!.CircuitId == circuitId
                         || (f.Sensor.CircuitId == null && f.Sensor.Node!.GreenhouseId == greenhouseId))
                        && (f.ResolvedAtUtc == null || sinceUtc == null || f.DetectedAtUtc >= sinceUtc))
            .OrderBy(f => f.ResolvedAtUtc == null ? 0 : 1)
            .ThenByDescending(f => f.DetectedAtUtc)
            .Take(Math.Max(1, take))
            .ToListAsync(ct);
        return faults.Select(ToDto).ToList();
    }

    public static SensorFaultDto ToDto(SensorFault f) =>
        new(f.Id, f.SensorId, f.SensorType, f.Kind, f.Value, f.PreviousValue, f.Detail,
            Utc(f.DetectedAtUtc), f.LastReadingAtUtc is { } last ? Utc(last) : null,
            SensorFaultRules.BlocksControl(f.SensorType), SensorFaultRules.ResolvedByUser(f.Kind),
            f.ResolvedAtUtc is { } resolved ? Utc(resolved) : null, f.ResolvedBy);

    /// <summary>
    /// Una persona marca como resuelto un error por valor (fuera de lo normal o salto): el control vuelve a actuar
    /// y la alerta se cierra. La falta de datos no se resuelve a mano: se cierra sola cuando vuelven las lecturas.
    /// </summary>
    public async Task<bool> ResolveAsync(int faultId, string? user, CancellationToken ct = default)
    {
        int? circuitId;
        await using (var context = await _dbFactory.CreateDbContextAsync(ct))
        {
            var fault = await context.SensorFaults.Include(f => f.Sensor).FirstOrDefaultAsync(f => f.Id == faultId, ct);
            if (fault is null || fault.ResolvedAtUtc is not null || !SensorFaultRules.ResolvedByUser(fault.Kind))
                return false;
            fault.ResolvedAtUtc = DateTime.UtcNow;
            fault.ResolvedBy = string.IsNullOrWhiteSpace(user) ? "usuario" : user;
            await context.SaveChangesAsync(ct);
            circuitId = fault.Sensor?.CircuitId;
        }

        if (_notifications is not null)
            await _notifications.ResolveByFingerprintAsync(SensorFaultAlerts.Fingerprint(circuitId, faultId), ct);
        _logger.LogInformation("Error de sensor {FaultId} (circuito {CircuitId}) marcado como resuelto por {User}.", faultId, circuitId, user);
        return true;
    }

    /// <summary>
    /// Barrido: abre "sin datos" para los sensores activos que no mandan lecturas en un nodo que sí está mandando (si
    /// el nodo entero se cortó, avisa la alerta de nodo desconectado), crea la alerta de cada error abierto y cierra
    /// las de los errores ya resueltos.
    /// </summary>
    public async Task<SensorFaultSweepResult> SweepAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var factor = Math.Max(1, _telemetry.DegradedIntervalFactor);
        var opened = 0;

        var nodes = await context.IotNodes.AsNoTracking().Where(n => n.LastAcceptedAt != null).ToListAsync(ct);
        foreach (var node in nodes)
        {
            var silenceAfter = SensorFaultRules.SilenceAfter(node.ExpectedIntervalSeconds, factor);
            if (SensorFaultRules.IsSilent(node.LastAcceptedAt, nowUtc, silenceAfter))
                continue;

            var sensors = await context.Sensors.AsNoTracking()
                .Where(s => s.NodeId == node.Id && s.IsActive)
                .Select(s => new
                {
                    s.Id,
                    Type = s.SensorType!.Name,
                    Last = context.SensorReadings.Where(r => r.SensorId == s.Id).Max(r => (DateTime?)r.ObservedAtUtc),
                })
                .ToListAsync(ct);
            var silentOpen = await context.SensorFaults.AsNoTracking()
                .Where(f => f.NodeId == node.Id && f.Kind == SensorFaultKinds.SinDatos && f.ResolvedAtUtc == null)
                .Select(f => f.SensorId)
                .ToListAsync(ct);

            foreach (var sensor in sensors.Where(s => SensorFaultRules.IsSilent(s.Last, nowUtc, silenceAfter) && !silentOpen.Contains(s.Id)))
            {
                context.SensorFaults.Add(new SensorFault
                {
                    NodeId = node.Id,
                    SensorId = sensor.Id,
                    SensorType = sensor.Type,
                    Kind = SensorFaultKinds.SinDatos,
                    DetectedAtUtc = nowUtc,
                    LastReadingAtUtc = sensor.Last,
                });
                opened++;
            }
        }

        if (opened > 0)
        {
            try
            {
                await context.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                // Otra vuelta (o la ingesta) lo abrió primero: queda para la próxima.
                _logger.LogWarning(ex, "No se pudieron guardar los sensores sin datos.");
                opened = 0;
            }
        }

        var (created, resolved) = _notifications is null ? (0, 0) : await SyncAlertsAsync(nowUtc, ct);
        return new SensorFaultSweepResult(opened, created, resolved);
    }

    private async Task<(int Created, int Resolved)> SyncAlertsAsync(DateTime nowUtc, CancellationToken ct)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var created = 0;
        var resolved = 0;

        var open = await context.SensorFaults.AsNoTracking()
            .Where(f => f.ResolvedAtUtc == null)
            .Select(f => new
            {
                Fault = f,
                f.Sensor!.CircuitId,
                Circuit = f.Sensor.Circuit != null ? f.Sensor.Circuit.Name : null,
                Greenhouse = f.Node!.Greenhouse!.Name,
                TimeZone = f.Node.Greenhouse.TimeZoneId,
            })
            .ToListAsync(ct);
        foreach (var row in open)
        {
            var fingerprint = SensorFaultAlerts.Fingerprint(row.CircuitId, row.Fault.Id);
            if (await context.NotificationAlerts.AnyAsync(a => a.Fingerprint == fingerprint, ct))
                continue;

            // pH/CE: va con el lote en curso del circuito; temperatura y humedad son del invernadero (sin lote).
            var lotId = row.CircuitId is { } circuitId
                ? await context.CircuitAssignments
                    .Where(a => a.CircuitId == circuitId && a.ValidFromUtc <= nowUtc && (a.ValidUntilUtc == null || a.ValidUntilUtc > nowUtc))
                    .Select(a => (int?)a.LotId)
                    .FirstOrDefaultAsync(ct)
                : null;
            var zone = GddService.ResolveGreenhouseTimeZone(row.TimeZone, []);
            var (title, message) = SensorFaultAlerts.Text(row.Fault, row.Circuit ?? row.Greenhouse, zone);
            await _notifications!.CreateAlertAsync(new CreateAlertDto(
                NotificationTypes.ErrorSensor,
                SensorFaultRules.BlocksControl(row.Fault.SensorType) ? NotificationSeverities.Alta : NotificationSeverities.Media,
                title,
                message,
                fingerprint,
                lotId), ct);
            created++;
        }

        // Alertas abiertas de errores ya resueltos (se resolvieron solos o desde otra sesión).
        var active = await context.NotificationAlerts.AsNoTracking()
            .Where(a => !a.IsResolved && a.Fingerprint.StartsWith(SensorFaultAlerts.Start))
            .Select(a => a.Fingerprint)
            .ToListAsync(ct);
        var faultIds = active.Select(SensorFaultAlerts.FaultId).OfType<int>().ToList();
        var closed = await context.SensorFaults.AsNoTracking()
            .Where(f => faultIds.Contains(f.Id) && f.ResolvedAtUtc != null)
            .Select(f => new { f.Id, f.Sensor!.CircuitId })
            .ToListAsync(ct);
        foreach (var fault in closed)
        {
            await _notifications!.ResolveByFingerprintAsync(SensorFaultAlerts.Fingerprint(fault.CircuitId, fault.Id), ct);
            resolved++;
        }
        return (created, resolved);
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

/// <summary>
/// Huella y textos de las alertas de errores de sensores: "sensor-fault-{circuitoId}-{errorId}" (circuito 0 = sensor
/// del invernadero).
/// </summary>
public static class SensorFaultAlerts
{
    public const string Start = "sensor-fault-";

    public static string Fingerprint(int? circuitId, int faultId) => $"{Start}{circuitId ?? 0}-{faultId}";

    /// <summary>Circuito de una huella de error de sensor; null si es del invernadero o la huella es de otra cosa.</summary>
    public static int? CircuitId(string? fingerprint) => Part(fingerprint, 0) is { } id and > 0 ? id : null;

    public static int? FaultId(string? fingerprint) => Part(fingerprint, 1);

    private static int? Part(string? fingerprint, int index)
    {
        if (fingerprint is null || !fingerprint.StartsWith(Start, StringComparison.Ordinal))
            return null;
        var parts = fingerprint[Start.Length..].Split('-');
        return parts.Length == 2 && int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    /// <summary>
    /// "Circuito 1: salto de pH (6,02 → 4,1)" / "Invernadero Principal: temperatura inusual (45 °C)" + qué pasó y qué hacer. pH y CE frenan su control (el de ese
    /// parámetro); temperatura y humedad solo avisan.
    /// </summary>
    public static (string Title, string Message) Text(SensorFault fault, string place, TimeZoneInfo zone)
    {
        var name = Name(fault.SensorType);
        var unit = SensorFaultRules.Unit(fault.SensorType);
        var blocks = SensorFaultRules.BlocksControl(fault.SensorType);
        string Value(decimal? v) => v is { } x ? SensorFaultRules.Num(x) + unit : "—";

        var title = fault.Kind switch
        {
            SensorFaultKinds.SinDatos => $"{place}: sensor de {name} sin datos",
            SensorFaultKinds.FueraDeRango when fault.SensorType == AnomalyContract.SensorTypeTemperatura
                => $"{place}: temperatura inusual ({Value(fault.Value)})",
            SensorFaultKinds.FueraDeRango => $"{place}: {name} fuera de lo normal ({Value(fault.Value)})",
            _ => $"{place}: salto de {name} ({Value(fault.PreviousValue)} → {Value(fault.Value)})",
        };

        var what = fault.Kind == SensorFaultKinds.SinDatos
            ? fault.LastReadingAtUtc is { } last
                ? $"Sin lecturas desde el {Local(last, zone)}."
                : "El sensor no mandó lecturas todavía."
            : $"{Capitalize(fault.Detail ?? string.Empty)}.";
        var then = (blocks, fault.Kind) switch
        {
            (true, SensorFaultKinds.SinDatos) => $"El control automático de {name} queda frenado hasta que vuelvan las lecturas.",
            (true, _) => $"El control automático de {name} queda frenado hasta que lo marques como resuelto en Optimización.",
            (false, SensorFaultKinds.SinDatos) => "Revisá el sensor.",
            _ => "Revisá el sensor y el invernadero.",
        };
        return (title, $"{what} {then}");
    }

    public static string Name(string sensorType) => sensorType switch
    {
        AnomalyContract.SensorTypeTemperatura => "temperatura",
        AnomalyContract.SensorTypeHumedad => "humedad",
        _ => sensorType,
    };

    private static string Local(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
