using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models.Optimization;

/// <summary>
/// Detalle de una recomendación (plan 16 OPT-04): itemes legibles que explican
/// la recomendación (escenarios económicos, métricas por etapa, conteos
/// comerciales, plantas en riesgo...). Es de solo lectura y acompaña al
/// snapshot para que Reports/Notifications no recalcule nada.
/// </summary>
public class RecommendationDetail
{
    public int Id { get; set; }

    public int RecommendationId { get; set; }

    /// <summary>Etiqueta del ítem (ej: "Escenario Baby Leaf", "pH", "Plantas en riesgo").</summary>
    [Required]
    [MaxLength(120)]
    public string Label { get; set; } = string.Empty;

    /// <summary>Clasificación: scenario | metric | count | assumption | risk.</summary>
    [Required]
    [MaxLength(20)]
    public string Kind { get; set; } = "metric";

    public decimal? Value { get; set; }

    public decimal? Target { get; set; }

    [MaxLength(30)]
    public string? Unit { get; set; }

    [MaxLength(300)]
    public string? Note { get; set; }

    /// <summary>Orden de presentación dentro de la recomendación.</summary>
    public int Order { get; set; }

    public OptimizationRecommendation? Recommendation { get; set; }
}