using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

/// <summary>
/// Lote plantado en un circuito durante un lapso (antes "asignación nodo → lote"). Se abre al crear el lote en Lotes
/// y se cierra al cosecharlo o descartarlo: un circuito tiene un solo lote en curso por vez. Las lecturas de pH y CE
/// del circuito van al lote de su asignación según la fecha de observación (no la de recepción).
/// </summary>
public class CircuitAssignment
{
    public int Id { get; set; }

    public int CircuitId { get; set; }

    public int LotId { get; set; }

    public DateTime ValidFromUtc { get; set; }

    /// <summary>null = el lote sigue en el circuito.</summary>
    public DateTime? ValidUntilUtc { get; set; }

    /// <summary>Origen: manual | auto | fixture (TelemetryContract.AssignmentSource*).</summary>
    [Required]
    [MaxLength(20)]
    public string Source { get; set; } = TelemetryContract.AssignmentSourceManual;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Circuit? Circuit { get; set; }
    public Lot? Lot { get; set; }
}
