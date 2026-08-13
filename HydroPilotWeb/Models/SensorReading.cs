using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

public class SensorReading
{
    public int Id { get; set; }

    public int SensorId { get; set; }

    public int? LotId { get; set; }

    public decimal Value { get; set; }

    public int? MeasurementUnitId { get; set; }

    public DateTime Timestamp { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Sensor? Sensor { get; set; }
    public MeasurementUnit? MeasurementUnit { get; set; }
    public Lot? Lot { get; set; }
}
