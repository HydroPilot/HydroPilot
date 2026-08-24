using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models.Optimization;

/// <summary>
/// Acción manual registrada sobre una recomendación (OPT-04): aceptar o
/// descartar con nota. Aplicar = registrar una acción manual; NUNCA envía un
/// comando a hardware. La transición de estado es única por recomendación, por
/// lo que la acción no se duplica (aceptar/descartar repetido es idempotente).
/// </summary>
public class RecommendationAction
{
    public int Id { get; set; }

    public int RecommendationId { get; set; }

    /// <summary>ACCEPT | DISCARD (ver OptimizationContract).</summary>
    [Required]
    [MaxLength(20)]
    public string Action { get; set; } = string.Empty;

    /// <summary>Nota o motivo registrado por el usuario (obligatorio al descartar).</summary>
    [MaxLength(500)]
    public string? Note { get; set; }

    /// <summary>Identificador o nombre del usuario que decidió (opcional).</summary>
    [MaxLength(150)]
    public string? PerformedBy { get; set; }

    public DateTime ActionedAtUtc { get; set; } = DateTime.UtcNow;

    public OptimizationRecommendation? Recommendation { get; set; }
}