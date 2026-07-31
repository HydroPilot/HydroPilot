using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

public class MeasurementUnit
{
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(10)]
    public string? Symbol { get; set; }

    public ICollection<Sensor> Sensors { get; set; } = [];
    public ICollection<SensorReading> Readings { get; set; } = [];
}
