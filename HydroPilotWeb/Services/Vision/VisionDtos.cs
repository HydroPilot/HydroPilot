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
    public float CombinedVisualAptitude => Math.Clamp(ProbFn * ProbHealthy, 0f, 1f);
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
