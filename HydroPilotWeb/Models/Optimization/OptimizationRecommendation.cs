using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models.Optimization;

/// <summary>
/// Recomendación de optimización persistida: una por problema del lote (Fingerprint), no por cálculo.
/// Mientras el problema sigue, se actualizan sus números en el lugar; cuando desaparece, se cierra
/// (cumplida, no cumplida o vencida). Es informativa: nunca controla actuadores.
///
/// Estados: PENDING, ACCEPTED (en curso), DONE (cumplida), MISSED (no cumplida), DISCARDED y EXPIRED
/// (ya no aplica). BLOCKED solo queda en el historial de la lógica anterior.
/// </summary>
public class OptimizationRecommendation
{
    public int Id { get; set; }

    public int LotId { get; set; }

    /// <summary>Tipo de recomendación (OptimizationContractType).</summary>
    [Required]
    [MaxLength(40)]
    public string RecommendationType { get; set; } = string.Empty;

    /// <summary>Estado (OptimizationContractStatus).</summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = OptimizationContractStatus.Pending;

    /// <summary>Dirección sugerida (OptimizationContractDirection).</summary>
    [Required]
    [MaxLength(20)]
    public string Direction { get; set; } = OptimizationContractDirection.Verify;

    /// <summary>Prioridad: HIGH | MEDIUM | LOW.</summary>
    [Required]
    [MaxLength(20)]
    public string Priority { get; set; } = OptimizationContractPriority.Medium;

    /// <summary>Valor actual observado (pH, CE, etc.). Null si no aplica.</summary>
    public decimal? CurrentValue { get; set; }

    /// <summary>Valor objetivo (pH objetivo, CE de la etapa...).</summary>
    public decimal? TargetValue { get; set; }

    /// <summary>Etiqueta legible del objetivo.</summary>
    [MaxLength(200)]
    public string? TargetLabel { get; set; }

    /// <summary>Qué disparó la recomendación (texto corto).</summary>
    [Required]
    public string Explanation { get; set; } = string.Empty;

    /// <summary>Impacto estimado en la moneda de los precios (null si falta un peso o un precio).</summary>
    public decimal? EstimatedImpact { get; set; }

    /// <summary>Qué es el impacto: "capture" (a capturar), "saving" (ahorro por semana) o "risk" (en riesgo).</summary>
    [MaxLength(30)]
    public string? ImpactUnit { get; set; }

    /// <summary>Lógica anterior (porcentaje fijo por regla). La lógica actual no lo usa: queda en 0.</summary>
    public int ConfidencePercent { get; set; }

    /// <summary>De dónde salen los datos (texto corto).</summary>
    [MaxLength(200)]
    public string DataSourceSummary { get; set; } = string.Empty;

    /// <summary>El impacto es una aproximación en duda (≈).</summary>
    public bool IsMockData { get; set; }

    /// <summary>Versión de las reglas que produjeron la recomendación.</summary>
    [Required]
    [MaxLength(40)]
    public string RuleVersion { get; set; } = string.Empty;

    /// <summary>Momento en que apareció el problema (UTC).</summary>
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Momento de la decisión (aceptar/descartar), UTC.</summary>
    public DateTime? DecidedAtUtc { get; set; }

    /// <summary>Motivo de descarte, o cómo se cerró.</summary>
    public string? DecisionNote { get; set; }

    /// <summary>Datos de la última evaluación en JSON (para reconstruir el porqué sin recalcular).</summary>
    public string SnapshotJson { get; set; } = string.Empty;

    /// <summary>SHA-256 hex de <see cref="SnapshotJson"/>.</summary>
    [Required]
    [MaxLength(64)]
    public string SnapshotHash { get; set; } = string.Empty;

    /// <summary>Problema al que responde ("BABY_LEAF_CUT:bl-cut"): una recomendación abierta por problema y lote.</summary>
    [MaxLength(120)]
    public string? Fingerprint { get; set; }

    /// <summary>Acción en pocas palabras ("Cortar Baby Leaf ahora").</summary>
    [MaxLength(200)]
    public string? Title { get; set; }

    /// <summary>Hasta cuándo conviene hacerlo. Aceptada y vencida sin resolverse → no cumplida.</summary>
    public DateOnly? DueDate { get; set; }

    /// <summary>Lo logrado al cumplirse (cosecha real o el impacto estimado), en la moneda de los precios.</summary>
    public decimal? RealizedAmount { get; set; }

    /// <summary>Última evaluación del problema (UTC).</summary>
    public DateTime? LastEvaluatedAtUtc { get; set; }

    /// <summary>Cierre: el problema desapareció o se resolvió (UTC). Una descartada sin cierre silencia el problema.</summary>
    public DateTime? ClosedAtUtc { get; set; }

    public Lot? Lot { get; set; }

    public ICollection<RecommendationDetail> Details { get; set; } = [];
    public ICollection<RecommendationAction> Actions { get; set; } = [];
}
