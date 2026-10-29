using HydroPilotWeb.Services.SimulationEnvironment;
using Microsoft.Azure.Devices;
using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Services.Control.IotHub;

/// <summary>
/// Acuses de los comandos C2D: cuando la Pi completa el mensaje el comando pasa a ENTREGADO; si lo rechaza, a
/// RECHAZADO. Lo vencido o purgado lo cierra el barrido de siempre.
/// </summary>
public sealed class IotHubFeedbackHostedService : BackgroundService
{
    private readonly IotHubOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IotHubFeedbackHostedService> _logger;

    public IotHubFeedbackHostedService(
        IOptions<IotHubOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<IotHubFeedbackHostedService> logger)
    {
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var client = ServiceClient.CreateFromConnectionString(_options.ServiceConnectionString, TransportType.Amqp);
        var receiver = client.GetFeedbackReceiver();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batch = await receiver.ReceiveAsync(stoppingToken);
                if (batch is null)
                    continue;

                // Mientras se regenera el historial, el acuse espera (no se puede saltear).
                using (await RegenerationPause.EnterAsync(stoppingToken))
                using (var scope = _scopeFactory.CreateScope())
                {
                    var control = scope.ServiceProvider.GetRequiredService<ControlService>();
                    foreach (var record in batch.Records)
                    {
                        if (!Guid.TryParse(record.OriginalMessageId, out var commandId))
                            continue;
                        var feedback = record.StatusCode switch
                        {
                            FeedbackStatusCode.Success => DeliveryFeedback.Delivered,
                            FeedbackStatusCode.Rejected => DeliveryFeedback.Rejected,
                            _ => DeliveryFeedback.NotDelivered,
                        };
                        await control.ApplyDeliveryFeedbackAsync(commandId, feedback, record.EnqueuedTimeUtc, stoppingToken);
                    }
                }

                await receiver.CompleteAsync(batch, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IoT Hub: falló la lectura de acuses de comandos; se reintenta en 10 s.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ContinueWith(_ => { });
            }
        }
    }
}
