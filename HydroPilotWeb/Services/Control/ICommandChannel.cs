namespace HydroPilotWeb.Services.Control;

/// <summary>
/// Cómo llega un comando a la Pi. Con HTTP la Pi lo pide (GET /api/nodos/{id}/comandos): no hay que mandar
/// nada. Con Azure IoT Hub se manda a la cola del dispositivo (C2D) apenas se crea.
/// </summary>
public interface ICommandChannel
{
    /// <summary>True si el comando hay que mandarlo (IoT Hub); false si la Pi lo pide (HTTP).</summary>
    bool PushesCommands { get; }

    /// <summary>Manda el comando a la Pi. Una excepción deja el comando PENDING para reintentar en el barrido.</summary>
    Task DispatchAsync(DeviceCommandDto command, CancellationToken ct = default);
}

/// <summary>Transporte HTTP: la Pi consulta sus comandos.</summary>
public sealed class HttpPollingCommandChannel : ICommandChannel
{
    public bool PushesCommands => false;

    public Task DispatchAsync(DeviceCommandDto command, CancellationToken ct = default) => Task.CompletedTask;
}
