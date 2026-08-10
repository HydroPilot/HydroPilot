using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

public class Prediction
{
    public int Id { get; set; }

    public int LotId { get; set; }

    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    public DateOnly? EstimatedHarvestDate { get; set; }

    public decimal AccumulatedGdd { get; set; }

    public decimal? EstimatedYield { get; set; }

    [MaxLength(50)]
    public string ModelVersion { get; set; } = "gdd-v1";

    public Lot? Lot { get; set; }
}
