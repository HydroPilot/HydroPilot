using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Services.Anomalies;

/// <summary>
/// Worker de barrido de episodios de anomalía (ANO-04): ejecuta el sweep al arranque
/// (para poblar episodios pendientes de un reinicio previo) y luego en intervalos
/// configurables. El servicio subyacente es idempotente y serializa las corridas
/// (el disparo manual desde la UI comparte el mismo gate).
/// </summary>
public sealed class AnomalySweepHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AnomalyOptions _options;
    private readonly ILogger<AnomalySweepHostedService> _logger;

    public AnomalySweepHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<AnomalyOptions> options,
        ILogger<AnomalySweepHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Barrido inmediato al arranque: un reinicio no deja episodios sin procesar.
        await RunSweepSafelyAsync(stoppingToken);

        // Piso de seguridad anti-polling: un intervalo configurado por error en 0/negativo
        // degeneraría en un bucle apretado — nunca menos de 5 segundos (documentado).
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

            await RunSweepSafelyAsync(stoppingToken);
        }
    }

    private async Task RunSweepSafelyAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<AnomalyEventService>();
            await service.SweepAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // apagado normal
        }
        catch (Exception ex)
        {
            // El módulo degrada de forma visible pero no tumba el host: se reintenta en el próximo tick.
            _logger.LogError(ex, "Error en el barrido de anomalías; se reintentará en el próximo ciclo.");
        }
    }
}