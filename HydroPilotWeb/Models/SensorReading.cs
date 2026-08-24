using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

public class SensorReading
{
    public int Id { get; set; }

    public int SensorId { get; set; }

    /// <summary>Nodo de origen (denormalizado para la clave única Nodo+ReadingId).</summary>
    public int NodeId { get; set; }

    public int? LotId { get; set; }

    /// <summary>Asignación temporal nodo→lote que resolvió esta lectura (por fecha de observación).</summary>
    public int? NodeLotAssignmentId { get; set; }

    public decimal Value { get; set; }

    public int? MeasurementUnitId { get; set; }

    /// <summary>Fecha/hora de observación del dispositivo, normalizada a UTC (alias de compatibilidad con Timestamp).</summary>
    public DateTime ObservedAtUtc { get; set; }

    /// <summary>Momento UTC en que el servidor recibió la lectura.</summary>
    public DateTime ReceivedAtUtc { get; set; }

    /// <summary>Alias histórico: fecha/hora de observación en UTC (se mantiene para no romper consultas existentes).</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Identificador externo estable de la lectura, único por nodo (idempotencia).</summary>
    [Required]
    [MaxLength(64)]
    public string ExternalReadingId { get; set; } = string.Empty;

    /// <summary>Estado de calidad: VALID | SUSPECT | INVALID | STALE | FUTURE | NO_DATA | SENSOR_ERROR.</summary>
    [Required]
    [MaxLength(20)]
    public string Quality { get; set; } = TelemetryContract.QualityValid;

    /// <summary>Motivo de la calidad (fuera_de_rango, fuera_de_banda_operativa, timestamp_anterior, ...).</summary>
    [MaxLength(100)]
    public string? QualityReason { get; set; }

    /// <summary>Resultado de ingesta por lectura: aceptada | sin_lote | (ver TelemetryContract).</summary>
    [Required]
    [MaxLength(20)]
    public string IngestionResult { get; set; } = TelemetryContract.ResultAceptada;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Sensor? Sensor { get; set; }
    public IotNode? Node { get; set; }
    public MeasurementUnit? MeasurementUnit { get; set; }
    public Lot? Lot { get; set; }
    public NodeLotAssignment? LotAssignment { get; set; }
}