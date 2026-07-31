using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

public class Greenhouse
{
    public int Id { get; set; }

    public int? UserId { get; set; }

    [Required]
    [MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? Location { get; set; }

    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User? User { get; set; }
    public ICollection<IotNode> IotNodes { get; set; } = [];
}
