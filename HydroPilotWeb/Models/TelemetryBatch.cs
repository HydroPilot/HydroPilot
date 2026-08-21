using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

/// <summary>
/// Registro de un batch recibido por el servidor (idempotencia a nivel batch).
/// La pareja (NodeId, BatchId) es única: un reintento del mismo batch con el mismo
/// hash devuelve el resultado anterior; con contenido distinto devuelve conflicto.
/// </summary>
public class TelemetryBatch
{
    public int Id { get; set; }

    public int NodeId { get; set; }

    /// <summary>Identificador externo del batch, generado por el nodo.</summary>
    [Required]
    [MaxLength(64)]
    public string BatchId { get; set; } = string.Empty;

    [Required]
    [MaxLength(10)]
    public string SchemaVersion { get; set; } = TelemetryContract.SchemaVersionV2;

    public long? Sequence { get; set; }

    public DateTime? SentAtUtc { get; set; }

    /// <summary>Momento UTC en que el servidor recibió el batch.</summary>
    public DateTime ReceivedAtUtc { get; set; }

    [MaxLength(50)]
    public string? FirmwareVersion { get; set; }

    /// <summary>SHA-256 en hexa del payload canónico (mismo batch + mismo hash = mismo resultado).</summary>
    [Required]
    [MaxLength(64)]
    public string PayloadHash { get; set; } = string.Empty;

    /// <summary>Resultado a nivel batch: procesado | duplicado | conflicto.</summary>
    [Required]
    [MaxLength(20)]
    public string Result { get; set; } = TelemetryContract.BatchResultProcesado;

    /// <summary>Resultado por lectura del primer procesamiento, para repetir en reintentos idénticos.</summary>
    public string? ResponseJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public IotNode? Node { get; set; }
}