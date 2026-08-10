using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

public class Lot
{
    public int Id { get; set; }

    public int GreenhouseId { get; set; }

    public int CropTypeId { get; set; }

    public int StatusId { get; set; }

    public DateOnly SowingDate { get; set; }

    public decimal PlantedAreaM2 { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Greenhouse? Greenhouse { get; set; }
    public CropType? CropType { get; set; }
    public LotStatus? Status { get; set; }
    public ICollection<Prediction> Predictions { get; set; } = [];
}
