using HydroPilotWeb.Services.SimulationEnvironment;
using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Services.SensorFaults;

/// <summary>
/// Cada <see cref="TelemetryOptions.MonitorIntervalSeconds"/>: sensores sin datos y alertas de los errores de
/// sensores (<see cref="SensorFaultService.SweepAsync"/>).
/// </summary>
public sealed class SensorFaultMonitorHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<TelemetryOptions> _options;
    private readonly ILogger<SensorFaultMonitorHostedService> _logger;

    public SensorFaultMonitorHostedService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<TelemetryOptions> options,
        ILogger<SensorFaultMonitorHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var lease = RegenerationPause.TryEnter();
                if (lease is not null)
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var result = await scope.ServiceProvider.GetRequiredService<SensorFaultService>().SweepAsync(DateTime.UtcNow, stoppingToken);
                    if (result.Opened > 0 || result.AlertsCreated > 0 || result.AlertsResolved > 0)
                        _logger.LogInformation("Errores de sensores: {Opened} sin datos, {Created} alertas nuevas, {Resolved} alertas resueltas.",
                            result.Opened, result.AlertsCreated, result.AlertsResolved);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falló el barrido de errores de sensores.");
            }

            var interval = TimeSpan.FromSeconds(Math.Max(_options.CurrentValue.MonitorIntervalSeconds, 10));
            await Task.Delay(interval, stoppingToken);
        }
    }
}
