using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

public class SensorType
{
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public ICollection<Sensor> Sensors { get; set; } = [];
}
