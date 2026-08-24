using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

/// <summary>
/// Configuración general del modelo de clasificación Baby Leaf para un cultivo
/// (plan 09 / LOT-02). Define la ventana GDD potencial y los umbrales de score.
/// Valores de arranque calibrables: ventana GDD 250-450, candidata 60, apta 80.
/// </summary>
public class BabyLeafConfig
{
    public int Id { get; set; }

    public int CropTypeId { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? Description { get; set; }

    /// <summary>Inicio de la ventana GDD potencial Baby Leaf (arranque: 250).</summary>
    public decimal GddMin { get; set; }

    /// <summary>Fin de la ventana GDD potencial Baby Leaf (arranque: 450).</summary>
    public decimal GddMax { get; set; }

    /// <summary>Score mínimo para ser candidata (arranque: 60).</summary>
    public decimal ScoreMinCandidate { get; set; }

    /// <summary>Score mínimo para ser apta (arranque: 80).</summary>
    public decimal ScoreMinReady { get; set; }

    /// <summary>Versión de la configuración (ej. "1.0").</summary>
    [MaxLength(20)]
    public string Version { get; set; } = "1.0";

    public bool IsActive { get; set; } = true;

    public CropType? CropType { get; set; }

    public ICollection<BabyLeafCriterion> Criteria { get; set; } = [];
}