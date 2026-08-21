using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

/// <summary>
/// Resultado histórico de evaluar una planta como posible Baby Leaf
/// (plan 09 / LOT-05). Cada evaluación tiene fecha, versión, GDD del lote en
/// ese momento y las métricas visuales utilizadas: la métrica nueva siempre
/// queda asociada a su evaluación, nunca se actualiza en silencio.
/// </summary>
public class BabyLeafEvaluation
{
    public int Id { get; set; }

    public int PlantId { get; set; }

    public int BabyLeafConfigId { get; set; }

    /// <summary>Imagen utilizada (nullable: puede haber evaluación manual/demo sin imagen).</summary>
    public int? PlantImageId { get; set; }

    public DateTime EvaluatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Score final 0-100.</summary>
    public decimal BabyLeafScore { get; set; }

    /// <summary>Resultado de clasificación (nombre del estado comercial resultante).</summary>
    [Required]
    [MaxLength(100)]
    public string Result { get; set; } = string.Empty;

    /// <summary>Confianza del modelo (0-1).</summary>
    public decimal Confidence { get; set; }

    [Required]
    [MaxLength(50)]
    public string ModelVersion { get; set; } = "BL-1.0";

    /// <summary>GDD del lote en el momento de la evaluación (base del lote).</summary>
    public decimal GddAtEvaluation { get; set; }

    /// <summary>Indica si los criterios obligatorios de la configuración fueron aprobados.</summary>
    public bool MandatoryCriteriaMet { get; set; }

    public decimal? FoliarAreaUsed { get; set; }
    public decimal? LeafLengthUsed { get; set; }
    public decimal? GrowthRateUsed { get; set; }

    public Plant? Plant { get; set; }
    public BabyLeafConfig? BabyLeafConfig { get; set; }
    public PlantImage? PlantImage { get; set; }
}