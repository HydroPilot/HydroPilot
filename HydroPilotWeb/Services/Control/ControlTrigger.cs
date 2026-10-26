using System.Threading.Channels;
using HydroPilotWeb.Services.SimulationEnvironment;

namespace HydroPilotWeb.Services.Control;

/// <summary>
/// Aviso de "llegaron lecturas de pH/CE de este circuito" de la ingesta al motor. La ingesta no espera al motor: si
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

    public void ReadingsArrived(int circuitId) => _channel.Writer.TryWrite(circuitId);
}

/// <summary>Evalúa el tanque de cada circuito que recibió lecturas, fuera del request de la ingesta.</summary>
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
                // Varios batches del mismo circuito seguidos → una sola evaluación.
                var circuits = new HashSet<int> { first };
                while (_trigger.Reader.TryRead(out var next))
                    circuits.Add(next);

                foreach (var circuitId in circuits)
                {
                    try
                    {
                        using var lease = RegenerationPause.TryEnter();
                        if (lease is null)
                            continue;
                        using var scope = _scopeFactory.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<ControlService>()
                            .EvaluateCircuitAsync(circuitId, DateTime.UtcNow, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Falló el control automático del circuito {CircuitId}.", circuitId);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
