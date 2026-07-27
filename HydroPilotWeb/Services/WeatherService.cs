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

    public WeatherService(
        HttpClient httpClient,
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _dbFactory = dbFactory;
        _configuration = configuration;
    }

    public async Task FetchAndStoreAsync()
    {
        var apiKey = _configuration["Weather:ApiKey"];
        var lat = _configuration["Weather:Lat"];
        var lon = _configuration["Weather:Lon"];

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(lat) || string.IsNullOrWhiteSpace(lon))
            return;

        var url = $"https://api.openweathermap.org/data/4.0/onecall/current?lat={lat}&lon={lon}&units=metric&appid={apiKey}";

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

    public async Task<List<WeatherRecord>> GetForecastAsync()
    {
        await using var context = _dbFactory.CreateDbContext();
        return await context.WeatherRecords
            .OrderByDescending(w => w.Timestamp)
            .ToListAsync();
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
