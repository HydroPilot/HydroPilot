using System.Text;
using System.Text.Json;
using Microsoft.Azure.Devices.Client;

namespace HydroPilot.PiSimulator;

/// <summary>
/// Transporte Azure IoT Hub (fase 3), como lo haría la Pi real con el SDK de dispositivo sobre MQTT/TLS:
/// - lecturas: mensaje D2C con propiedad type=telemetry (el mismo JSON v2);
/// - comandos: mensajes C2D (dose-command); se completan después de ejecutarlos y se rechazan si no se pueden leer;
/// - resultados: mensaje D2C con type=command-result y el commandId en el cuerpo.
/// El nodo es el DeviceId de la cadena de conexión.
/// </summary>
public sealed class IotHubPiTransport : IPiTransport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly DeviceClient _client;

    public IotHubPiTransport(string deviceConnectionString)
    {
        _client = DeviceClient.CreateFromConnectionString(deviceConnectionString, TransportType.Mqtt);
    }

    /// <summary>DeviceId de una cadena "HostName=…;DeviceId=…;SharedAccessKey=…".</summary>
    public static string? DeviceIdOf(string connectionString) =>
        connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .FirstOrDefault(kv => kv.Length == 2 && kv[0].Trim().Equals("DeviceId", StringComparison.OrdinalIgnoreCase))?[1].Trim();

    public string Name => "IoT Hub";

    public async Task<int?> SendReadingsAsync(object batch, CancellationToken ct)
    {
        using var message = Message(batch, "telemetry");
        await _client.SendEventAsync(message, ct);
        return null; // por IoT Hub no hay respuesta: la web procesa el mensaje después
    }

    public async Task<IReadOnlyList<ReceivedCommand>> ReceiveCommandsAsync(CancellationToken ct)
    {
        var received = new List<ReceivedCommand>();
        // Se toma lo que haya en la cola (espera corta; el hub entrega de a un mensaje).
        while (received.Count < 10)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(received.Count == 0 ? 2 : 0.2));
            Microsoft.Azure.Devices.Client.Message? message;
            try
            {
                message = await _client.ReceiveAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break;
            }
            if (message is null)
                break;

            DeviceCommand? command = null;
            try
            {
                command = JsonSerializer.Deserialize<DeviceCommand>(message.GetBytes(), Json);
            }
            catch (JsonException)
            {
            }

            if (command is null || command.CommandId == Guid.Empty)
            {
                await _client.RejectAsync(message, ct); // no se puede leer: no se reintenta
                message.Dispose();
                continue;
            }
            received.Add(new ReceivedCommand(command, message));
        }
        return received;
    }

    public async Task AcknowledgeAsync(ReceivedCommand command, bool accepted, CancellationToken ct)
    {
        if (command.Handle is not Microsoft.Azure.Devices.Client.Message message)
            return;
        if (accepted)
            await _client.CompleteAsync(message, ct);
        else
            await _client.RejectAsync(message, ct);
        message.Dispose();
    }

    public async Task<(ReportOutcome Outcome, string? Detail)> ReportResultAsync(Guid commandId, CommandResult result, CancellationToken ct)
    {
        using var message = Message(new
        {
            commandId,
            result.Status,
            result.ExecutedAmount,
            result.StartedAt,
            result.FinishedAt,
            result.Error,
        }, "command-result");
        await _client.SendEventAsync(message, ct);
        return (ReportOutcome.Accepted, null);
    }

    /// <summary>Por IoT Hub no se pregunta: la Pi usa sus claves de siempre (--circuitos).</summary>
    public Task<DeviceConfig?> GetConfigAsync(CancellationToken ct) => Task.FromResult<DeviceConfig?>(null);

    public async ValueTask DisposeAsync()
    {
        await _client.CloseAsync();
        _client.Dispose();
    }

    private static Microsoft.Azure.Devices.Client.Message Message(object body, string type)
    {
        var message = new Microsoft.Azure.Devices.Client.Message(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, Json)))
        {
            ContentType = "application/json",
            ContentEncoding = "utf-8",
        };
        message.Properties["type"] = type;
        return message;
    }
}
