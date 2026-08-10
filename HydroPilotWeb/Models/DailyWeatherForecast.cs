namespace HydroPilotWeb.Models;

public class DailyWeatherForecast
{
    public int Id { get; set; }

    public DateOnly Date { get; set; }

    public decimal TempMin { get; set; }

    public decimal TempMax { get; set; }

    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
}
