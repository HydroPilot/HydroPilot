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

    public static string Fingerprint(int nodeId, string parameter) => $"control-pause-{nodeId}-{parameter}";

    public Task OnPausedAsync(ControlPauseEvent pause, CancellationToken ct = default) =>
        _notifications.CreateAlertAsync(new CreateAlertDto(
            NotificationTypes.FallaDosificacion,
            NotificationSeverities.Alta,
            $"{pause.NodeIdentifier}: control de {pause.Parameter} pausado",
            $"{pause.Reason}. Revisá bidón, bomba y sonda; después reanudá en Control.",
            Fingerprint(pause.NodeId, pause.Parameter),
            pause.LotId), ct);

    public Task OnResumedAsync(int nodeId, string parameter, CancellationToken ct = default) =>
        _notifications.ResolveByFingerprintAsync(Fingerprint(nodeId, parameter), ct);
}
