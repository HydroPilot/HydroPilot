using HydroPilotWeb.Services.SimulationEnvironment;
using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Services.Control;

/// <summary>
/// Barrido del control de pH/CE: al arrancar y cada <see cref="ControlOptions.SweepIntervalSeconds"/> vence los
/// comandos que nadie tomó y registra el nivel vigente de cada lote en curso.
/// </summary>
public sealed class ControlSweepHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ControlOptions _options;
    private readonly ILogger<ControlSweepHostedService> _logger;

    public ControlSweepHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<ControlOptions> options,
        ILogger<ControlSweepHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunSafelyAsync(stoppingToken);

        // Nunca menos de 5 s, aunque se configure 0 por error.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _options.SweepIntervalSeconds)));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await RunSafelyAsync(stoppingToken);
        }
    }

    private async Task RunSafelyAsync(CancellationToken ct)
    {
        using var lease = RegenerationPause.TryEnter();
        if (lease is null)
            return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            // Receta aprendida: una vez por día por lote (las vueltas siguientes del día no recalculan).
            await scope.ServiceProvider.GetRequiredService<RecipeService>().RefreshAsync(DateTime.UtcNow, ct);
            await scope.ServiceProvider.GetRequiredService<ControlService>().SweepAsync(DateTime.UtcNow, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falló el barrido de control.");
        }
    }
}
