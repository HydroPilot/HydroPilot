namespace HydroPilotWeb.Services;

/// <summary>
/// Obtiene datos climáticos de OpenWeather una vez por día (06:00 UTC).
/// Respeta el toggle WeatherFetchEnabled: si está apagado, no consulta la API.
/// El guard de 1 vez/día es por fecha de FetchedAt, sobrevive reinicios.
/// </summary>
public class WeatherFetcherHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WeatherFetcherHostedService> _logger;

    public WeatherFetcherHostedService(
        IServiceProvider serviceProvider,
        ILogger<WeatherFetcherHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceIfNeededAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallo al obtener datos climáticos");
            }

            // Esperar hasta la próxima ejecución diaria (06:00 UTC)
            var delay = GetDelayUntilNextRun();
            _logger.LogInformation("Próxima ejecución de clima en {Hours:N1}h", delay.TotalHours);
            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task RunOnceIfNeededAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var weather = scope.ServiceProvider.GetRequiredService<WeatherService>();

        var enabled = await settings.GetBoolAsync(SettingsService.WeatherFetchEnabledKey, false, ct);
        if (!enabled)
        {
            _logger.LogInformation("Sincronización climática deshabilitada por configuración. Skip.");
            return;
        }

        var fetchedToday = await weather.WasFetchedTodayAsync(ct);
        if (fetchedToday)
        {
            _logger.LogInformation("Clima ya obtenido hoy. Skip.");
            return;
        }

        await weather.FetchAndStoreAsync();
        await weather.FetchAndStoreForecastAsync();
        _logger.LogInformation("Datos climáticos obtenidos a las {Time}", DateTimeOffset.UtcNow);
    }

    private static TimeSpan GetDelayUntilNextRun()
    {
        var now = DateTime.UtcNow;
        var next = now.Date.AddHours(6); // 06:00 UTC de hoy
        if (next <= now)
        {
            next = next.AddDays(1); // ya pasó: mañana
        }
        return next - now;
    }
}
