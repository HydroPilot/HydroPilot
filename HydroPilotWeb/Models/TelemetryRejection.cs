using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

/// <summary>
/// Cuarentena de diagnóstico para lecturas que no pueden persistirse como lectura
/// (sensor desconocido, error de unidad, timestamp inválido/futuro, valor faltante,
/// o estados reportados por el dispositivo NO_DATA / SENSOR_ERROR).
/// Las lecturas con calidad INVALID por rango físico imposible se persisten en
/// SensorReadings (cuarentena consultable) y no entran aquí.
/// </summary>
public class TelemetryRejection
{
    public int Id { get; set; }

    public int NodeId { get; set; }

    [Required]
    [MaxLength(64)]
    public string BatchId { get; set; } = string.Empty;

    /// <summary>readingId externo reportado (o sintetizado para payloads legados).</summary>
    [Required]
    [MaxLength(64)]
    public string ReadingId { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string SensorRef { get; set; } = string.Empty;

    public DateTime? ObservedAtUtc { get; set; }

    public DateTime ReceivedAtUtc { get; set; }

    public decimal? Value { get; set; }

    [MaxLength(20)]
    public string? Unit { get; set; }

    /// <summary>Motivo del rechazo: ver constantes en TelemetryContract.</summary>
    [Required]
    [MaxLength(50)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>Evaluación de calidad asociada (INVALID, FUTURE, NO_DATA, SENSOR_ERROR).</summary>
    [MaxLength(20)]
    public string? Quality { get; set; }

    /// <summary>Resultado por lectura asociado: desconocida | invalida | error_de_unidad.</summary>
    [Required]
    [MaxLength(20)]
    public string Result { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public IotNode? Node { get; set; }
}