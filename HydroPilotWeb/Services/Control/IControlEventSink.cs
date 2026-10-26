namespace HydroPilotWeb.Services.Control;

/// <summary>Se pausó el control automático de una magnitud de un circuito.</summary>
public sealed record ControlPauseEvent(int CircuitId, string CircuitName, int? LotId, string Parameter, string Reason, DateTime PausedAtUtc);

/// <summary>Avisos del control a otros módulos (Notificaciones). En tests se reemplaza por uno que graba.</summary>
public interface IControlEventSink
{
    Task OnPausedAsync(ControlPauseEvent pause, CancellationToken ct = default);

    Task OnResumedAsync(int circuitId, string parameter, CancellationToken ct = default);
}

public sealed class NoopControlEventSink : IControlEventSink
{
    public Task OnPausedAsync(ControlPauseEvent pause, CancellationToken ct = default) => Task.CompletedTask;

    public Task OnResumedAsync(int circuitId, string parameter, CancellationToken ct = default) => Task.CompletedTask;
}
