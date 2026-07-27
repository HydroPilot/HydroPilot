namespace HydroPilotWeb.Services;

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
                using var scope = _serviceProvider.CreateScope();
                var weatherService = scope.ServiceProvider.GetRequiredService<WeatherService>();
                await weatherService.FetchAndStoreAsync();
                _logger.LogInformation("Weather data fetched successfully at {Time}", DateTimeOffset.Now);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch weather data");
            }

            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}
