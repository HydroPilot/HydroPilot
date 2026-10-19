using System.Threading.Channels;

namespace HydroPilotWeb.Services.Control;

/// <summary>
/// Aviso de "llegaron lecturas de pH/CE de este nodo" de la ingesta al motor. La ingesta no espera al motor: si
/// la cola se llena se descartan los avisos más viejos (el barrido de cada minuto cubre lo que falte).
/// </summary>
public sealed class ControlTrigger
{
    private readonly Channel<int> _channel = Channel.CreateBounded<int>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    public ChannelReader<int> Reader => _channel.Reader;

    public void ReadingsArrived(int nodeId) => _channel.Writer.TryWrite(nodeId);
}

/// <summary>Evalúa el tanque de cada nodo que recibió lecturas, fuera del request de la ingesta.</summary>
public sealed class ControlWorker : BackgroundService
{
    private readonly ControlTrigger _trigger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ControlWorker> _logger;

    public ControlWorker(ControlTrigger trigger, IServiceScopeFactory scopeFactory, ILogger<ControlWorker> logger)
    {
        _trigger = trigger;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var first in _trigger.Reader.ReadAllAsync(stoppingToken))
            {
                // Varios batches del mismo nodo seguidos → una sola evaluación.
                var nodes = new HashSet<int> { first };
                while (_trigger.Reader.TryRead(out var next))
                    nodes.Add(next);

                foreach (var nodeId in nodes)
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<ControlService>()
                            .EvaluateNodeAsync(nodeId, DateTime.UtcNow, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Falló el control automático del nodo {NodeId}.", nodeId);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
