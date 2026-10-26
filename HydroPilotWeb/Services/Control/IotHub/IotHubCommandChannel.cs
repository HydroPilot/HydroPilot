using Microsoft.Azure.Devices;
using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Services.Control.IotHub;

/// <summary>
/// Manda cada comando a la cola del dispositivo en Azure IoT Hub (C2D). MessageId = commandId (la Pi no repite un
/// comando), vencimiento = expiresAt (si la Pi no se conecta a tiempo el hub lo descarta: nunca se ejecuta
/// tarde) y acuse completo, que llega por <see cref="IotHubFeedbackHostedService"/>.
/// </summary>
public sealed class IotHubCommandChannel : ICommandChannel, IAsyncDisposable
{
    private readonly ServiceClient _client;

    public IotHubCommandChannel(IOptions<IotHubOptions> options)
    {
        _client = ServiceClient.CreateFromConnectionString(options.Value.ServiceConnectionString, TransportType.Amqp);
    }

    public bool PushesCommands => true;

    public async Task DispatchAsync(DeviceCommandDto command, CancellationToken ct = default)
    {
        using var message = new Message(IotHubMessages.CommandBody(command))
        {
            MessageId = command.CommandId.ToString(),
            ExpiryTimeUtc = command.ExpiresAt.ToUniversalTime(),
            Ack = DeliveryAcknowledgement.Full,
            ContentType = "application/json",
            ContentEncoding = "utf-8",
        };
        message.Properties[IotHubMessages.TypeProperty] = DeviceContract.TypeDoseCommand;
        await _client.SendAsync(command.NodeId, message, TimeSpan.FromSeconds(30));
    }

    public async ValueTask DisposeAsync()
    {
        await _client.CloseAsync();
        _client.Dispose();
    }
}
