using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

/// <summary>
/// Asignación temporal de un nodo a un lote. La lectura se asigna por su fecha de
/// observación (ObservedAtUtc), no por el momento de recepción. Un nodo puede tener
/// una línea de tiempo de asignaciones (abierta o cerradas) sin solapamientos.
/// </summary>
public class NodeLotAssignment
{
    public int Id { get; set; }

    public int NodeId { get; set; }

    public int LotId { get; set; }

    public DateTime ValidFromUtc { get; set; }

    /// <summary>null = asignación abierta (vigente hasta nuevo aviso).</summary>
    public DateTime? ValidUntilUtc { get; set; }

    /// <summary>Origen: manual | auto | fixture (TelemetryContract.AssignmentSource*).</summary>
    [Required]
    [MaxLength(20)]
    public string Source { get; set; } = TelemetryContract.AssignmentSourceManual;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public IotNode? Node { get; set; }
    public Lot? Lot { get; set; }
}