using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

public class CropType
{
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public decimal GddTarget { get; set; }

    public decimal BaseTemperature { get; set; }

    public decimal? OptimalPhMin { get; set; }

    public decimal? OptimalPhMax { get; set; }

    public decimal? OptimalEcMin { get; set; }

    public decimal? OptimalEcMax { get; set; }

    public int? EstimatedDaysToHarvest { get; set; }

    public decimal? YieldPerM2 { get; set; }

    public string? Description { get; set; }

    public ICollection<Lot> Lots { get; set; } = [];
}
