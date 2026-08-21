using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

/// <summary>
/// Histórico de cambios de estado de una planta (plan 09 / LOT-09).
/// Una entrada por transición: guarda el estado anterior y el nuevo.
/// El flujo diario registra las transiciones de etapa fenológica/comercial de
/// plantas ACTIVAS; las operaciones de cosecha/descarte registran la transición
/// operativa. Nunca se reescriben entradas históricas.
/// </summary>
public class PlantStageHistory
{
    public int Id { get; set; }

    public int PlantId { get; set; }

    public int? PreviousPhenologicalStageId { get; set; }
    public int? PreviousCommercialStageId { get; set; }
    public string? PreviousOperationalState { get; set; }

    public int? NewPhenologicalStageId { get; set; }
    public int? NewCommercialStageId { get; set; }
    public string? NewOperationalState { get; set; }

    /// <summary>Origen de la transición: seed | daily-flow | harvest | discard.</summary>
    [Required]
    [MaxLength(30)]
    public string Source { get; set; } = "daily-flow";

    /// <summary>Motivo legible (ej. motivo de descarte, umbral superado).</summary>
    [MaxLength(300)]
    public string? Reason { get; set; }

    public DateTime ChangedAtUtc { get; set; } = DateTime.UtcNow;

    public Plant? Plant { get; set; }
}