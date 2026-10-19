using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Services.Optimization;

/// <summary>
/// Recalcula las recomendaciones de los lotes en curso cada <see cref="OptimizationOptions.SweepIntervalMinutes"/>
/// (además de al abrir la vista), para que los problemas nuevos aparezcan y se avisen aunque nadie abra
/// Optimización, y para verificar solas las aceptadas.
/// </summary>
public sealed class OptimizationSweepHostedService : BackgroundService
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<OptimizationOptions> _options;
    private readonly ILogger<OptimizationSweepHostedService> _logger;

    public OptimizationSweepHostedService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<OptimizationOptions> options,
        ILogger<OptimizationSweepHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_options.CurrentValue.SweepEnabled)
            {
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var service = scope.ServiceProvider.GetRequiredService<OptimizationService>();
                    var created = await service.EvaluateOpenLotsAsync(stoppingToken);
                    if (created > 0)
                        _logger.LogInformation("Optimización: {Count} recomendaciones nuevas.", created);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Optimización: falló el barrido de recomendaciones.");
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, _options.CurrentValue.SweepIntervalMinutes)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
