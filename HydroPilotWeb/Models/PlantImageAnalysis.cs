using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

/// <summary>
/// Métricas extraídas de una imagen de planta mediante visión artificial
/// (plan 09 / LOT-05). Fase futura: el pipeline de análisis aún no existe;
/// estas columnas definen el contrato persistido que consumirá el flujo diario.
/// </summary>
public class PlantImageAnalysis
{
    public int Id { get; set; }

    public int PlantImageId { get; set; }

    public decimal? PlantArea { get; set; }

    public decimal? FoliarArea { get; set; }

    public decimal? LeafCount { get; set; }

    public decimal? PlantWidth { get; set; }

    public decimal? PlantHeight { get; set; }

    public decimal? RosetteDiameter { get; set; }

    public decimal? LeafLength { get; set; }

    [MaxLength(50)]
    public string? AverageColor { get; set; }

    public decimal? GreennessIndex { get; set; }

    /// <summary>Porcentaje de daño detectado (0-100).</summary>
    public decimal? DamagePercent { get; set; }

    /// <summary>Tasa de crecimiento (%/día).</summary>
    public decimal? GrowthRate { get; set; }

    public decimal? ImageQuality { get; set; }

    [MaxLength(50)]
    public string? PredominantClass { get; set; }

    public decimal? Confidence { get; set; }

    public string? ProbabilitiesJson { get; set; }

    [MaxLength(50)]
    public string? HealthClass { get; set; }

    public decimal? HealthConfidence { get; set; }

    public string? HealthProbabilitiesJson { get; set; }

    public decimal? LaplacianVariance { get; set; }

    public decimal? VegetationCoverageRatio { get; set; }

    public decimal? CanopySolidity { get; set; }

    public decimal? SizeRatio { get; set; }

    public decimal? BabyLeafScore { get; set; }

    [MaxLength(50)]
    public string? BabyLeafStatus { get; set; }

    [Required]
    [MaxLength(50)]
    public string ModelVersion { get; set; } = "CV-pending";

    public DateTime AnalyzedAtUtc { get; set; } = DateTime.UtcNow;

    public PlantImage? PlantImage { get; set; }
}