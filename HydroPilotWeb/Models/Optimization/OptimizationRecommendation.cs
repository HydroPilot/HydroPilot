using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models.Optimization;

/// <summary>
/// Recomendación de optimización persistida (plan 16, OPT-04).
///
/// La recomendación es INFORMATIVA: no equivale a una orden física ni controla
/// actuadores. Conserva el snapshot completo de entradas (JSON + hash canónico)
/// para reconstruir la razón sin recalcular con datos futuros (OPT-01/OPT-04).
///
/// Estados (plan 16 / contrato compartido 02): PENDING, ACCEPTED, DISCARDED,
/// EXPIRED, BLOCKED.
/// </summary>
public class OptimizationRecommendation
{
    public int Id { get; set; }

    public int LotId { get; set; }

    /// <summary>
    /// Tipo de recomendación (ver OptimizationContract): CHEMICAL_SOLUTION,
    /// ECONOMIC_ALTERNATIVE o COMMERCIAL_GROWOUT.
    /// </summary>
    [Required]
    [MaxLength(40)]
    public string RecommendationType { get; set; } = string.Empty;

    /// <summary>Estado: PENDING | ACCEPTED | DISCARDED | EXPIRED | BLOCKED.</summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = OptimizationContractStatus.Pending;

    /// <summary>
    /// Dirección sugerida (química): RAISE | LOWER | MAINTAIN | VERIFY.
    /// Económica/comercial: RAISE/MAINTAIN según contexto (ver Explanation).
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string Direction { get; set; } = OptimizationContractDirection.Verify;

    /// <summary>Prioridad: HIGH | MEDIUM | LOW.</summary>
    [Required]
    [MaxLength(20)]
    public string Priority { get; set; } = OptimizationContractPriority.Medium;

    /// <summary>Valor actual observado (pH, CE, margen %, etc.). Null si no aplica.</summary>
    public decimal? CurrentValue { get; set; }

    /// <summary>Valor objetivo (pH objetivo, EC de la etapa, margen de la alternativa...).</summary>
    public decimal? TargetValue { get; set; }

    /// <summary>Etiqueta legible del objetivo (ej: "pH 5,8–6,2", "EC 1,5 (etapa ...)").</summary>
    [MaxLength(200)]
    public string? TargetLabel { get; set; }

    /// <summary>Motivo/regla aplicada: qué dato disparó la recomendación y qué se sugiere.</summary>
    [Required]
    public string Explanation { get; set; } = string.Empty;

    /// <summary>Impacto estimado (porcentual, económico o null según tipo).</summary>
    public decimal? EstimatedImpact { get; set; }

    /// <summary>Unidad del impacto ("%", moneda, "puntos de margen", null).</summary>
    [MaxLength(30)]
    public string? ImpactUnit { get; set; }

    /// <summary>Confianza 0-100 según cantidad/consistencia de lecturas y fuente.</summary>
    public int ConfidencePercent { get; set; }

    /// <summary>Resumen de la fuente de datos (sensor/pronóstico/mock/manual).</summary>
    [MaxLength(200)]
    public string DataSourceSummary { get; set; } = string.Empty;

    /// <summary>El hallazgo proviene de datos demo etiquetados (nunca se oculta).</summary>
    public bool IsMockData { get; set; }

    /// <summary>Versión de las reglas que produjeron la recomendación (OPT-01).</summary>
    [Required]
    [MaxLength(40)]
    public string RuleVersion { get; set; } = string.Empty;

    /// <summary>Momento de cálculo (UTC).</summary>
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Momento de la decisión (aceptar/descartar), UTC.</summary>
    public DateTime? DecidedAtUtc { get; set; }

    /// <summary>Nota de la decisión (motivo de descarte/aceptación, registrado por acción).</summary>
    public string? DecisionNote { get; set; }

    /// <summary>Snapshot completo de entradas en JSON canónico (OPT-01).</summary>
    public string SnapshotJson { get; set; } = string.Empty;

    /// <summary>SHA-256 hex del snapshot canónico: idempotencia (mismo snapshot ⇒ misma recomendación).</summary>
    [Required]
    [MaxLength(64)]
    public string SnapshotHash { get; set; } = string.Empty;

    public Lot? Lot { get; set; }

    public ICollection<RecommendationDetail> Details { get; set; } = [];
    public ICollection<RecommendationAction> Actions { get; set; } = [];
}