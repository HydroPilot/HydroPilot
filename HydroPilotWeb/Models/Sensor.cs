using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

public class Sensor
{
    public int Id { get; set; }

    /// <summary>La Pi que lo lee.</summary>
    public int NodeId { get; set; }

    /// <summary>Circuito del sensor (pH y CE). Null = sensor del invernadero (temperatura y humedad).</summary>
    public int? CircuitId { get; set; }

    public int SensorTypeId { get; set; }

    public int? MeasurementUnitId { get; set; }

    [MaxLength(100)]
    public string? Model { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Identificador técnico estable del sensor dentro del nodo (ej: "ph-solucion").
    /// Es la clave de resolución del contrato v2; Name queda como nombre visible.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string TechnicalKey { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime? LastCalibrationDate { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public IotNode? Node { get; set; }
    public Circuit? Circuit { get; set; }
    public SensorType? SensorType { get; set; }
    public MeasurementUnit? MeasurementUnit { get; set; }
    public ICollection<SensorReading> Readings { get; set; } = [];
}
