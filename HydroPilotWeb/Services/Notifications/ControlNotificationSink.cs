using HydroPilotWeb.Models.Notifications;
using HydroPilotWeb.Services.Control;

namespace HydroPilotWeb.Services.Notifications;

/// <summary>
/// Una pausa del control automático (dosis sin efecto) es una alerta Alta: alguien tiene que revisar bidón,
/// bomba y sonda. Se resuelve sola al reanudar.
/// </summary>
public class ControlNotificationSink : IControlEventSink
{
    private readonly NotificationService _notifications;

    public ControlNotificationSink(NotificationService notifications)
    {
        _notifications = notifications;
    }

    /// <summary>Huella común a todas las pausas de una magnitud de un circuito (lo que se resuelve al reanudar).</summary>
    public static string FingerprintPrefix(int circuitId, string parameter) => $"control-pause-{circuitId}-{parameter}";

    /// <summary>
    /// Huella de una pausa: incluye cuándo se pausó, porque la huella de las alertas es única aunque estén
    /// resueltas y la misma magnitud se puede pausar más de una vez.
    /// </summary>
    public static string Fingerprint(int circuitId, string parameter, DateTime pausedAtUtc) =>
        $"{FingerprintPrefix(circuitId, parameter)}-{pausedAtUtc:yyyyMMddHHmmss}";

    public Task OnPausedAsync(ControlPauseEvent pause, CancellationToken ct = default) =>
        _notifications.CreateAlertAsync(new CreateAlertDto(
            NotificationTypes.FallaDosificacion,
            NotificationSeverities.Alta,
            $"{pause.CircuitName}: control de {pause.Parameter} pausado",
            $"{pause.Reason}. Revisá bidón, bomba y sonda; después reanudá en Control.",
            Fingerprint(pause.CircuitId, pause.Parameter, pause.PausedAtUtc),
            pause.LotId), ct);

    public Task OnResumedAsync(int circuitId, string parameter, CancellationToken ct = default) =>
        _notifications.ResolveByFingerprintPrefixAsync(FingerprintPrefix(circuitId, parameter), ct);
}
