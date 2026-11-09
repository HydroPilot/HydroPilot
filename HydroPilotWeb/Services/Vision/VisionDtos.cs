namespace HydroPilotWeb.Services.Vision;

public enum BabyLeafStageStatus
{
    NoBabyLeaf,
    SinDecision,
    Candidata,
    Apta,
    NoApta
}

public sealed record ImageQualityMetrics(
    bool IsReliable,
    double LaplacianVariance,
    double MeanBrightness,
    double SaturatedPixelRatio,
    double VegetationCoverageRatio,
    string? RejectionReason = null);

public sealed record PlantMorphologyMetrics(
    double LeafAreaCm2,
    double MaxLeafLengthCm,
    double RosetteDiameterCm,
    int EstimatedLeafCount,
    double CanopySolidity,
    double? SizeRatio = null);

public sealed record VisualHealthPrediction(
    string TopNpkClass,
    float ProbFn,
    IReadOnlyDictionary<string, float> NpkProbabilities,
    string TopHealthClass,
    float ProbHealthy,
    IReadOnlyDictionary<string, float> HealthProbabilities)
{
    /// <summary>
    /// Aptitud visual calibrada (0..1) que evita el castigo multiplicativo extremo en datasets reducidos:
    /// - Si la clase dominante es FN y Healthy, aplica una curva suavizada de alta aptitud (0.78..1.00).
    /// - Si hay deficiencia nutricional (-N, -P, -K) pero sin patógenos (Healthy), aplica descuento moderado (0.42..0.72).
    /// - Si se detectan patógenos o plagas activas, aplica penalización sanitaria severa (0.15..0.45).
    /// </summary>
    public float CombinedVisualAptitude
    {
        get
        {
            float npkFactor = TopNpkClass == "FN"
                ? 0.80f + 0.20f * Math.Clamp((ProbFn - 0.25f) / 0.75f, 0f, 1f)
                : 0.45f + 0.30f * Math.Clamp(ProbFn, 0f, 1f);

            float healthFactor = TopHealthClass == "Healthy"
                ? 0.85f + 0.15f * Math.Clamp((ProbHealthy - 0.20f) / 0.80f, 0f, 1f)
                : 0.22f + 0.35f * Math.Clamp(ProbHealthy, 0f, 1f);

            return (float)Math.Round(Math.Clamp(npkFactor * healthFactor, 0f, 1f), 4);
        }
    }
}

public sealed record BabyLeafEvaluationResult(
    BabyLeafStageStatus Status,
    double BabyLeafScore,
    double AccumulatedGdd,
    bool IsInTemporalWindow,
    ImageQualityMetrics? ImageQuality,
    PlantMorphologyMetrics? Morphology,
    double? GrowthRateCm2PerGdd,
    double? GrowthFactor,
    VisualHealthPrediction? VisualState,
    string Recommendation,
    DateTime EvaluatedAtUtc);

/// <summary>
/// DTO de compatibilidad para endpoints y clientes que solo consumen la clasificación NPK.
/// </summary>
public sealed record ClassificationResult(
    string PredominantClass,
    string Description,
    float Confidence,
    IReadOnlyDictionary<string, float> Probabilities,
    string ModelVersion);
