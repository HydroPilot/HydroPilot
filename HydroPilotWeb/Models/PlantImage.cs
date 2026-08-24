using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

/// <summary>
/// Imagen capturada de una planta (histórico; plan 09 / LOT-05).
/// El procesamiento de imágenes (visión computacional) es fase futura:
/// la entidad se persiste desde ahora, pero el pipeline de captura/análisis
/// queda fuera de esta tanda.
/// </summary>
public class PlantImage
{
    public int Id { get; set; }

    public int PlantId { get; set; }

    /// <summary>Momento de captura (UTC).</summary>
    public DateTime CapturedAtUtc { get; set; }

    [Required]
    [MaxLength(500)]
    public string Path { get; set; } = string.Empty;

    [MaxLength(255)]
    public string FileName { get; set; } = string.Empty;

    public int Width { get; set; }

    public int Height { get; set; }

    public bool Processed { get; set; }

    /// <summary>Versión del modelo de visión que la procesó (null = sin procesar).</summary>
    [MaxLength(50)]
    public string? ModelVersion { get; set; }

    public DateTime? ProcessedAtUtc { get; set; }

    public Plant? Plant { get; set; }
    public PlantImageAnalysis? Analysis { get; set; }
    public ICollection<BabyLeafEvaluation> Evaluations { get; set; } = [];
}