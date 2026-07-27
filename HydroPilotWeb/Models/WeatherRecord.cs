using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

public class WeatherRecord
{
    public int Id { get; set; }

    public DateTime Timestamp { get; set; }

    public double Temp { get; set; }

    public double FeelsLike { get; set; }

    public double Humidity { get; set; }

    public double Pressure { get; set; }

    public double WindSpeed { get; set; }

    public int Clouds { get; set; }

    public int Visibility { get; set; }

    [MaxLength(128)]
    public string Description { get; set; } = string.Empty;
}
