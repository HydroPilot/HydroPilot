using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models.Control;

/// <summary>
/// El control automático de una magnitud (pH o CE) de un circuito quedó pausado, por ejemplo después de varias
/// dosis seguidas sin efecto (bidón vacío, bomba trabada, sonda descalibrada). Sigue pausado hasta que una
/// persona lo reanuda: abierta = <see cref="ResumedAtUtc"/> null.
/// </summary>
public class ControlPause
{
    public int Id { get; set; }

    public int CircuitId { get; set; }

    /// <summary>pH | CE.</summary>
    [Required]
    [MaxLength(5)]
    public string Parameter { get; set; } = ControlContract.ParameterPh;

    public DateTime PausedAtUtc { get; set; }

    [Required]
    [MaxLength(200)]
    public string Reason { get; set; } = string.Empty;

    public DateTime? ResumedAtUtc { get; set; }

    [MaxLength(200)]
    public string? ResumedBy { get; set; }

    public Circuit? Circuit { get; set; }
}
