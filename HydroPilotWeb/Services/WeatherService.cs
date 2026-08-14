using System.Text.Json;
using System.Text.Json.Serialization;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Services;

public class WeatherService
{
    private readonly HttpClient _httpClient;
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WeatherService> _logger;

    public WeatherService(
        HttpClient httpClient,
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        IConfiguration configuration,
        ILogger<WeatherService> logger)
    {
        _httpClient = httpClient;
        _dbFactory = dbFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task FetchAndStoreAsync()
    {
        var apiKey = _configuration["Weather:ApiKey"];
        var lat = _configuration["Weather:Lat"];
        var lon = _configuration["Weather:Lon"];

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(lat) || string.IsNullOrWhiteSpace(lon))
            return;

        var url = $"https://api.openweathermap.org/data/4.0/onecall/current?lat={lat}&lon={lon}&units=metric&appid={apiKey}";

        try
        {
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<OneCall4CurrentResponse>(json);

            if (result?.data is not { Length: > 0 })
                return;

            var current = result.data[0];
            var timestamp = DateTimeOffset.FromUnixTimeSeconds(current.dt).UtcDateTime;

            await using var context = _dbFactory.CreateDbContext();

            var record = new WeatherRecord
            {
                Timestamp = timestamp,
                Temp = current.temp,
                FeelsLike = current.feels_like,
                Humidity = current.humidity,
                Pressure = current.pressure,
                WindSpeed = current.wind_speed,
                Clouds = current.clouds,
                Visibility = current.visibility,
                Description = current.weather is { Length: > 0 } ? current.weather[0].description : ""
            };

            context.WeatherRecords.Add(record);
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // El clima nunca debe romper el resto del sistema: se loguea y se degrada.
            _logger.LogWarning(ex, "Fallo al obtener el clima actual de OpenWeather");
        }
    }

    public async Task<List<WeatherRecord>> GetForecastAsync()
    {
        await using var context = _dbFactory.CreateDbContext();
        return await context.WeatherRecords
            .OrderByDescending(w => w.Timestamp)
            .ToListAsync();
    }

    /// <summary>
    /// Trae el pronóstico diario (~7 días) de OpenWeather y lo guarda en DailyWeatherForecasts.
    /// Reemplaza las fechas existentes (upsert por fecha).
    /// </summary>
    public async Task FetchAndStoreForecastAsync()
    {
        var (lat, lon, apiKey) = GetWeatherConfig();
        if (apiKey is null) return;

        var url = $"https://api.openweathermap.org/data/4.0/onecall/forecast?lat={lat}&lon={lon}&units=metric&appid={apiKey}";

        try
        {
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<OneCallForecastResponse>(json);

            if (result?.data is not { Length: > 0 }) return;

            await using var context = _dbFactory.CreateDbContext();

            var fetchedAt = DateTime.UtcNow;
            var inserted = 0;

            foreach (var day in result.data)
            {
                var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(day.dt).UtcDateTime);
                var existing = await context.DailyWeatherForecasts
                    .FirstOrDefaultAsync(f => f.Date == date);

                if (existing is null)
                {
                    context.DailyWeatherForecasts.Add(new Models.DailyWeatherForecast
                    {
                        Date = date,
                        TempMin = (decimal)day.temp.min,
                        TempMax = (decimal)day.temp.max,
                        FetchedAt = fetchedAt
                    });
                    inserted++;
                }
                else
                {
                    existing.TempMin = (decimal)day.temp.min;
                    existing.TempMax = (decimal)day.temp.max;
                    existing.FetchedAt = fetchedAt;
                }
            }

            await context.SaveChangesAsync();

            _logger.LogInformation("Forecast climático guardado: {Inserted} fechas nuevas (rango {First}..{Last})",
                inserted, result.data[0].dt, result.data[^1].dt);
        }
        catch (Exception ex)
        {
            // El clima nunca debe romper el forecasting: se loguea y se degrada al fallback del sensor.
            _logger.LogWarning(ex, "Fallo al obtener el pronóstico de OpenWeather");
        }
    }

    /// <summary>
    /// Fetch perezoso: si faltan fechas del rango [from, to] en la DB, consulta la API una vez
    /// y guarda toda la ventana devuelta. No respeta el toggle (es bajo demanda del forecasting).
    /// </summary>
    public async Task FetchForecastForDatesAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var checkContext = _dbFactory.CreateDbContext();

        var existing = await checkContext.DailyWeatherForecasts
            .Where(f => f.Date >= from && f.Date <= to)
            .Select(f => f.Date)
            .ToListAsync(ct);

        if (existing.Count >= (to.DayNumber - from.DayNumber + 1))
            return; // no faltan fechas

        await FetchAndStoreForecastAsync();
    }

    /// <summary>
    /// Consulta el pronóstico de un rango de fechas desde la DB (sin tocar la API).
    /// </summary>
    public async Task<List<Models.DailyWeatherForecast>> GetForecastForDatesAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var context = _dbFactory.CreateDbContext();
        return await context.DailyWeatherForecasts
            .Where(f => f.Date >= from && f.Date <= to)
            .OrderBy(f => f.Date)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Indica si el pronóstico para el día actual ya fue obtenido hoy (para el guard de 1 vez/día).
    /// </summary>
    public async Task<bool> WasFetchedTodayAsync(CancellationToken ct = default)
    {
        await using var context = _dbFactory.CreateDbContext();
        var today = DateTime.UtcNow.Date;
        return await context.DailyWeatherForecasts
            .AnyAsync(f => f.FetchedAt.Date == today, ct);
    }

    private (string lat, string lon, string? apiKey) GetWeatherConfig()
    {
        var apiKey = _configuration["Weather:ApiKey"];
        var lat = _configuration["Weather:Lat"] ?? "0";
        var lon = _configuration["Weather:Lon"] ?? "0";

        if (string.IsNullOrWhiteSpace(apiKey))
            return (lat, lon, null);

        return (lat, lon, apiKey);
    }

    private sealed class OneCallForecastResponse
    {
        [JsonPropertyName("data")]
        public ForecastDayData[]? data { get; set; }
    }

    private sealed class ForecastDayData
    {
        public long dt { get; set; }
        public ForecastTempData temp { get; set; } = new();
    }

    private sealed class ForecastTempData
    {
        public double min { get; set; }
        public double max { get; set; }
    }

    private sealed class OneCall4CurrentResponse
    {
        [JsonPropertyName("data")]
        public CurrentWeatherData[]? data { get; set; }
    }

    private sealed class CurrentWeatherData
    {
        public long dt { get; set; }
        public double temp { get; set; }
        public double feels_like { get; set; }
        public double humidity { get; set; }
        public double pressure { get; set; }
        public double wind_speed { get; set; }
        public int clouds { get; set; }
        public int visibility { get; set; }
        public WeatherInfo[]? weather { get; set; }
    }

    private sealed class WeatherInfo
    {
        public string description { get; set; } = string.Empty;
    }
}
