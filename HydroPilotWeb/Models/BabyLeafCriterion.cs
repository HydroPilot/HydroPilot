using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

/// <summary>
/// Variable y peso que componen el BabyLeafScore (plan 09 / LOT-02).
/// El procesamiento de imágenes es fase futura: estos criterios definen QUÉ
/// alimenta el score, no la fórmula de normalización de métricas (pendiente de
/// calibración con datos reales de visión computacional).
/// </summary>
public class BabyLeafCriterion
{
    public int Id { get; set; }

    public int BabyLeafConfigId { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Tipo de dato evaluado: GDD | MORFOLOGIA | CRECIMIENTO | VISUAL.</summary>
    [Required]
    [MaxLength(30)]
    public string DataType { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? Unit { get; set; }

    /// <summary>Umbral mínimo (opcional; se calibra con datos reales).</summary>
    public decimal? ValueMin { get; set; }

    /// <summary>Umbral máximo (opcional; se calibra con datos reales).</summary>
    public decimal? ValueMax { get; set; }

    /// <summary>Peso porcentual dentro del score (la suma de criterios activos debe ser 100).</summary>
    public decimal Weight { get; set; }

    /// <summary>Si es excluyente: no aprobado impide "Baby Leaf apta".</summary>
    public bool IsMandatory { get; set; }

    public bool IsActive { get; set; } = true;

    public BabyLeafConfig? BabyLeafConfig { get; set; }
}