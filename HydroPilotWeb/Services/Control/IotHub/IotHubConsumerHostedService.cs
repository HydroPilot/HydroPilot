using System.Globalization;
using Azure.Messaging.EventHubs.Consumer;
using HydroPilotWeb.Services.SimulationEnvironment;
using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Services.Control.IotHub;

/// <summary>
/// Lee los mensajes de las Pi (D2C) del punto de conexión compatible con Event Hub del IoT Hub, una tarea por
/// partición, y los pasa a <see cref="IotHubMessageHandler"/>. Guarda en AppSettings hasta qué hora de
/// encolado procesó cada partición: al reiniciar sigue desde ahí. Si se reprocesa algo, la ingesta y los
/// resultados son idempotentes (batchId / commandId).
/// </summary>
public sealed class IotHubConsumerHostedService : BackgroundService
{
    private const int CheckpointEveryEvents = 20;
    private static readonly TimeSpan CheckpointEvery = TimeSpan.FromSeconds(30);

    private readonly IotHubOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IotHubConsumerHostedService> _logger;

    public IotHubConsumerHostedService(
        IOptions<IotHubOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<IotHubConsumerHostedService> logger)
    {
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public static string CheckpointKey(string partitionId) => $"IotHub:Checkpoint:{partitionId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var consumer = new EventHubConsumerClient(_options.ConsumerGroup, _options.EventHubConnectionString);
        string[] partitions;
        try
        {
            partitions = await consumer.GetPartitionIdsAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "IoT Hub: no se pudo conectar al punto de conexión de mensajes.");
            return;
        }

        _logger.LogInformation("IoT Hub: leyendo {Count} particiones (grupo {Group}).", partitions.Length, _options.ConsumerGroup);
        await Task.WhenAll(partitions.Select(p => ReadPartitionAsync(consumer, p, stoppingToken)));
    }

    private async Task ReadPartitionAsync(EventHubConsumerClient consumer, string partitionId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var start = await CheckpointAsync(partitionId, ct)
                            ?? DateTimeOffset.UtcNow.AddMinutes(-Math.Max(0, _options.StartBackMinutes));
                var pending = 0;
                var lastSaved = DateTime.UtcNow;
                DateTimeOffset? last = null;

                await foreach (var partitionEvent in consumer.ReadEventsFromPartitionAsync(
                                   partitionId, EventPosition.FromEnqueuedTime(start), ct))
                {
                    var data = partitionEvent.Data;
                    if (data is null)
                        continue;

                    // Mientras se regenera el historial, el mensaje espera (no se puede saltear).
                    using (await RegenerationPause.EnterAsync(ct))
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var handler = scope.ServiceProvider.GetRequiredService<IotHubMessageHandler>();
                        await handler.HandleAsync(
                            IotHubMessages.DeviceIdOf(data.SystemProperties),
                            IotHubMessages.TypeOf(data.Properties),
                            data.EventBody.ToArray(),
                            DateTime.UtcNow,
                            ct);
                    }

                    last = data.EnqueuedTime;
                    if (++pending >= CheckpointEveryEvents || DateTime.UtcNow - lastSaved >= CheckpointEvery)
                    {
                        await SaveCheckpointAsync(partitionId, last.Value, ct);
                        (pending, lastSaved) = (0, DateTime.UtcNow);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IoT Hub: se cortó la lectura de la partición {Partition}; se reintenta en 10 s.", partitionId);
                await Task.Delay(TimeSpan.FromSeconds(10), ct).ContinueWith(_ => { });
            }
        }
    }

    private async Task<DateTimeOffset?> CheckpointAsync(string partitionId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var value = await scope.ServiceProvider.GetRequiredService<SettingsService>().GetStringAsync(CheckpointKey(partitionId), ct);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var checkpoint)
            ? checkpoint
            : null;
    }

    private async Task SaveCheckpointAsync(string partitionId, DateTimeOffset enqueuedTime, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SettingsService>()
            .SetStringAsync(CheckpointKey(partitionId), enqueuedTime.ToString("o", CultureInfo.InvariantCulture), ct);
    }
}
