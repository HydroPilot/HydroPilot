using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

/// <summary>
/// Etapa fenológica (biológica) de un cultivo. Deriva del GDD acumulado del lote.
/// Cada etapa define su ventana GDD y la receta de EC administrada a nivel lote.
/// Valores de arranque calibrables (plan 09 / LOT-02):
/// Establecimiento 0-150 (EC 1,0), Crecimiento vegetativo 150-450 (EC 1,5),
/// Formación y madurez 450-750 (EC 1,7).
/// </summary>
public class PhenologicalStage
{
    public int Id { get; set; }

    public int CropTypeId { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? Description { get; set; }

    /// <summary>Orden de la etapa dentro del ciclo del cultivo (1 = primera).</summary>
    public int Order { get; set; }

    public decimal GddMin { get; set; }

    public decimal GddMax { get; set; }

    /// <summary>EC mínima recomendada para la solución nutritiva en esta etapa.</summary>
    public decimal EcMin { get; set; }

    /// <summary>EC objetivo (receta) de esta etapa.</summary>
    public decimal EcObjective { get; set; }

    public decimal EcMax { get; set; }

    public bool IsActive { get; set; } = true;

    public CropType? CropType { get; set; }
}