using System.Text.Json;
using System.Text.Json.Serialization;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Services;

/// <summary>
/// Clima (F-03): separa clima ACTUAL (WeatherRecords, puntual) del PRONÓSTICO
/// diario (DailyWeatherForecasts, ~7 días). El fetch se serializa con un
/// semáforo para resolver la carrera entre el guard programado (hosted service)
/// y el fetch perezoso del forecasting: nunca dos llamadas simultáneas a la API.
/// Todo fallo de red/API degrada con logging, nunca rompe el forecasting.
/// </summary>
public class WeatherService
{
    /// <summary>Serializa el fetch de pronóstico (carrera fetch programado vs perezoso).</summary>
    private static readonly SemaphoreSlim FetchForecastLock = new(1, 1);

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

    /// <summary>Clima actual puntual: OpenWeather → WeatherRecords (solo observación).</summary>
    public async Task FetchAndStoreAsync(CancellationToken ct = default)
    {
        var apiKey = _configuration["Weather:ApiKey"];
        var lat = _configuration["Weather:Lat"];
        var lon = _configuration["Weather:Lon"];

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(lat) || string.IsNullOrWhiteSpace(lon))
            return;

        var url = $"https://api.openweathermap.org/data/4.0/onecall/current?lat={lat}&lon={lon}&units=metric&appid={apiKey}";

        try
        {
            using var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<OneCall4CurrentResponse>(json);

            if (result?.data is not { Length: > 0 })
                return;

            var current = result.data[0];
            var timestamp = DateTimeOffset.FromUnixTimeSeconds(current.dt).UtcDateTime;

            await using var context = await _dbFactory.CreateDbContextAsync(ct);

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
            await context.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Fetch de clima actual cancelado.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallo al obtener el clima actual de OpenWeather");
        }
    }

    /// <summary>Historial de clima actual (observación puntual, descendente).</summary>
    public async Task<List<WeatherRecord>> GetForecastAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        return await context.WeatherRecords
            .OrderByDescending(w => w.Timestamp)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Trae el pronóstico diario (~7 días) de OpenWeather y lo guarda en
    /// DailyWeatherForecasts (upsert por fecha). Validación F-03: Tmin &gt; Tmax
    /// se intercambia y se loguea (el dato no se descarta en silencio).
    /// </summary>
    public async Task FetchAndStoreForecastAsync(CancellationToken ct = default)
    {
        var (lat, lon, apiKey) = GetWeatherConfig();
        if (apiKey is null)
        {
            _logger.LogInformation("Pronóstico climático saltado: sin Weather:ApiKey configurada.");
            return;
        }

        // One Call 4.0 usa timeline/1day (no existe /onecall/forecast).
        var url = $"https://api.openweathermap.org/data/4.0/onecall/timeline/1day?lat={lat}&lon={lon}&units=metric&appid={apiKey}";

        try
        {
            using var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<OneCallForecastResponse>(json);

            if (result?.data is not { Length: > 0 })
            {
                _logger.LogWarning("Pronóstico sin datos en la respuesta de OpenWeather.");
                return;
            }

            await using var context = await _dbFactory.CreateDbContextAsync(ct);

            var fetchedAt = DateTime.UtcNow;
            var inserted = 0;
            var invalidDays = 0;

            foreach (var day in result.data)
            {
                var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(day.dt).UtcDateTime);

                var tmin = (decimal)day.temp.min;
                var tmax = (decimal)day.temp.max;
                if (tmin > tmax)
                {
                    invalidDays++;
                    _logger.LogWarning("Pronóstico {Date}: Tmin {Tmin} > Tmax {Tmax}; se intercambian.", date, tmin, tmax);
                    (tmin, tmax) = (tmax, tmin);
                }

                var existing = await context.DailyWeatherForecasts
                    .FirstOrDefaultAsync(f => f.Date == date, ct);

                if (existing is null)
                {
                    context.DailyWeatherForecasts.Add(new DailyWeatherForecast
                    {
                        Date = date,
                        TempMin = tmin,
                        TempMax = tmax,
                        FetchedAt = fetchedAt
                    });
                    inserted++;
                }
                else
                {
                    existing.TempMin = tmin;
                    existing.TempMax = tmax;
                    existing.FetchedAt = fetchedAt;
                }
            }

            await context.SaveChangesAsync(ct);

            _logger.LogInformation("Forecast climático guardado: {Inserted} fechas nuevas ({Invalid} con Tmin/Tmax corregidas), rango {First}..{Last}",
                inserted, invalidDays, result.data[0].dt, result.data[^1].dt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Fetch de pronóstico cancelado.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallo al obtener el pronóstico de OpenWeather");
        }
    }

    /// <summary>
    /// Fetch perezoso serializado (F-03, carrera resuelta): dentro del semáforo se
    /// re-verifica la DB — si otra llamada (guard programado u otro lazy) ya trajo
    /// el rango, no consulta la API de nuevo. El guard de 1 vez/día lo decide el
    /// llamador (GddService lee el toggle administrativo); aquí solo se serializa.
    /// </summary>
    public async Task FetchForecastForDatesAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await FetchForecastLock.WaitAsync(ct);
        try
        {
            await using var checkContext = await _dbFactory.CreateDbContextAsync(ct);

            var existing = await checkContext.DailyWeatherForecasts
                .Where(f => f.Date >= from && f.Date <= to)
                .Select(f => f.Date)
                .ToListAsync(ct);

            if (existing.Count >= (to.DayNumber - from.DayNumber + 1))
                return; // el rango ya está cubierto (lo trajo otra llamada)

            await FetchAndStoreForecastAsync(ct);
        }
        finally
        {
            FetchForecastLock.Release();
        }
    }

    /// <summary>
    /// Consulta el pronóstico de un rango de fechas desde la DB (sin tocar la API).
    /// </summary>
    public async Task<List<DailyWeatherForecast>> GetForecastForDatesAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        return await context.DailyWeatherForecasts
            .Where(f => f.Date >= from && f.Date <= to)
            .OrderBy(f => f.Date)
            .ToListAsync(ct);
    }

    /// <summary>Indica si el pronóstico para hoy ya fue obtenido hoy (guard 1 vez/día).</summary>
    public async Task<bool> WasFetchedTodayAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
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