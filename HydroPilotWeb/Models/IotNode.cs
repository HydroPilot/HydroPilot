using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

public class IotNode
{
    public int Id { get; set; }

    public int GreenhouseId { get; set; }

    [Required]
    [MaxLength(100)]
    public string Identifier { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? FirmwareVersion { get; set; }

    [MaxLength(30)]
    public string Status { get; set; } = "ACTIVO";

    public DateTime? LastConnection { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Greenhouse? Greenhouse { get; set; }
    public ICollection<Sensor> Sensors { get; set; } = [];
}
