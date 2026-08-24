using HydroPilotWeb.Models.Optimization;
using HydroPilotWeb.Services.Forecasting;

namespace HydroPilotWeb.Services.Optimization;

/// <summary>
/// Lectura individual incluida en el snapshot de optimización (OPT-01):
/// magnitud, calidad, valor y momento de observación. Solo lecturas
/// operativamente usables participan; la antigüedad se conserva para explicar
/// bloqueos por frescura.
/// </summary>
public sealed record SnapshotReading(
    string SensorType,       // pH | CE
    string Quality,          // TelemetryContract.Quality*
    decimal Value,
    DateTime ObservedAtUtc);

/// <summary>Ítem de catálogo de costos/precios capturado en el snapshot.</summary>
public sealed record SnapshotPriceCost(
    string Destination,      // baby_leaf | conventional
    string Item,             // OptimizationContractCostItem.*
    decimal Value,
    string Currency,
    string Source,           // manual | demo | fixture
    DateOnly ValidFrom,
    DateOnly? ValidUntil);

/// <summary>
/// Snapshot completo de entradas de una recomendación (OPT-01): lote y cultivo,
/// superficie, lecturas válidas con antigüedad/calidad, GDD, fecha estimada,
/// rendimiento y ciclos, costos/precios si hay, fuente y versión de reglas.
/// Se persiste como JSON + hash canónico para reconstruir la razón sin recalcular
/// con datos futuros. El hash NO incluye GeneratedAtUtc: repetir el cálculo con
/// el mismo snapshot es reproducible.
/// </summary>
public sealed record OptimizationSnapshot(
    string RuleVersion,
    // Lote y cultivo
    int LotId,
    string? LotName,
    string CropTypeName,
    decimal AreaM2,
    string? StatusName,
    DateOnly SowingDate,
    // Lecturas utilizadas (pH/CE)
    IReadOnlyList<SnapshotReading> Readings,
    decimal? CurrentPh,
    decimal? CurrentEc,
    int PhUsableCount,
    int EcUsableCount,
    int? PhAgeMinutes,
    int? EcAgeMinutes,
    // GDD y forecast (consumidos, nunca recalculados)
    decimal GddAccumulated,
    decimal? GddTarget,
    string? PhenologicalStageName,
    decimal? EcObjective,
    DateOnly? EstimatedHarvestDate,
    int? DaysRemaining,
    decimal? YieldBaseKgM2,
    int AccuracyCycles,
    string DataSourceSummary,
    string SourceKind,
    // Comercial del lote (agregado, no una planta seleccionada)
    int ActivePlants,
    int RiskPlantCount,
    int AptaPlantCount,
    decimal AptaPercent,
    bool IsCommercialStageMixed,
    // Económico (catálogo vigente al momento del cálculo)
    IReadOnlyList<SnapshotPriceCost> PriceCosts);

/// <summary>Punto de lectura considerado por la evidencia química.</summary>
public sealed record ReadingPoint(
    string SensorType,
    string Quality,
    decimal Value,
    DateTime ObservedAtUtc,
    int AgeMinutes);

/// <summary>
/// Resultado de evaluar pH o CE contra su objetivo (OPT-02): dirección,
/// explicación de la regla, valor objetivo y confianza según cantidad y
/// consistencia de lecturas (OPT-02: desbalance de 1 lectura vs dos
/// consecutivas consistentes).
/// </summary>
public sealed record ChemicalAssessment(
    bool Evaluable,
    string Direction,
    decimal? CurrentValue,
    decimal? TargetValue,
    string? TargetLabel,
    string Explanation,
    int ConfidencePercent,
    IReadOnlyList<string> Warnings);

/// <summary>Cuenta resultante del motor químico para una magnitud.</summary>
public sealed record ChemicalAssessmentDto(
    string SensorType,
    ChemicalAssessment Assessment);

/// <summary>Escenario económico resuelto para un destino (OPT-03).</summary>
public sealed record EconomicScenarioDto(
    string Destination,
    bool Calculable,
    string? NotCalculableReason,
    decimal YieldKgM2,
    decimal? PricePerKg,
    decimal? SeedCostM2,
    decimal? NutrientCostM2,
    decimal? EnergyCostM2,
    decimal? TransplantCostM2,
    decimal? RevenueM2,
    decimal? TotalCostM2,
    decimal? ResultM2,
    decimal? MarginPercent,
    IReadOnlyList<string> Warnings);

/// <summary>Comparativa Baby Leaf vs convencional (OPT-03/OPT-08).</summary>
public sealed record EconomicComparisonDto(
    bool Calculable,
    string? NotCalculableReason,
    EconomicScenarioDto? BabyLeaf,
    EconomicScenarioDto? Conventional,
    decimal? ProfitabilityDifferencePct,
    decimal ThresholdPercent,
    string? RecommendedDestination,
    bool ExceedsThreshold,
    IReadOnlyList<string> Assumptions);

/// <summary>Dato de planta en riesgo expuesto por el módulo de anomalías (OPT-09).</summary>
public sealed record PlantRiskDto(int PlantId, string Reason);

/// <summary>
/// Estado completo que consume la UI/API del módulo: contexto del lote,
/// lecturas recientes, conteos, riesgos, evaluaciones químicas en vivo,
/// comparativa económica y las recomendaciones vigentes persistidas.
/// </summary>
public sealed record OptimizationStateDto(
    int LotId,
    string? LotName,
    string CropTypeName,
    string? StatusName,
    DateOnly SowingDate,
    decimal AreaM2,
    decimal GddAccumulated,
    decimal? GddTarget,
    string? PhenologicalStageName,
    decimal? EcObjective,
    DateOnly? EstimatedHarvestDate,
    int? DaysRemaining,
    decimal? CurrentPh,
    decimal? CurrentEc,
    int? PhAgeMinutes,
    int? EcAgeMinutes,
    int? PhUsableCount,
    int? EcUsableCount,
    string DataSourceSummary,
    ForecastSourceKind SourceKind,
    bool IsMockData,
    int ActivePlants,
    int AptaPlantCount,
    decimal AptaPercent,
    decimal? AptaTargetPercent,
    bool IsCommercialStageMixed,
    IReadOnlyList<PlantRiskDto> PlantRisks,
    IReadOnlyList<ChemicalAssessmentDto> ChemicalAssessments,
    EconomicComparisonDto? EconomicComparison,
    IReadOnlyList<RecommendationDto> ActiveRecommendations,
    IReadOnlyList<string> Warnings);

/// <summary>DTO público de una recomendación persistida (contrato plan 02 "Recomendación").</summary>
public sealed record RecommendationDto(
    int Id,
    int LotId,
    string RecommendationType,
    string Status,
    string Direction,
    string Priority,
    decimal? CurrentValue,
    decimal? TargetValue,
    string? TargetLabel,
    string Explanation,
    decimal? EstimatedImpact,
    string? ImpactUnit,
    int ConfidencePercent,
    string DataSourceSummary,
    bool IsMockData,
    string RuleVersion,
    DateTime GeneratedAtUtc,
    DateTime? DecidedAtUtc,
    string? DecisionNote,
    string SnapshotHash,
    IReadOnlyList<RecommendationDetailDto> Details,
    IReadOnlyList<RecommendationActionDto> Actions);

public sealed record RecommendationDetailDto(
    int Id,
    string Label,
    string Kind,
    decimal? Value,
    decimal? Target,
    string? Unit,
    string? Note,
    int Order);

public sealed record RecommendationActionDto(
    int Id,
    string Action,
    string? Note,
    string? PerformedBy,
    DateTime ActionedAtUtc);

/// <summary>Resultado de la generación de recomendaciones (idempotente por snapshot).</summary>
public sealed record OptimizationGenerationResult(
    OptimizationStateDto State,
    IReadOnlyList<RecommendationDto> Recommendations,
    int ExpiredPendingCount,
    bool DuplicatedSnapshotsSkipped,
    IReadOnlyList<string> Warnings);

/// <summary>Resultado de aceptar/descartar (idempotente).</summary>
public sealed record DecisionResult(
    bool Succeeded,
    bool Repeated,
    string Message,
    RecommendationDto? Recommendation);

/// <summary>Evento emitido a Notifications cuando una recomendación estratégica nueva supera el umbral (OPT-06).</summary>
public sealed record RecommendationEventDto(
    int RecommendationId,
    int LotId,
    string RecommendationType,
    string Priority,
    string Status,
    string Direction,
    string Explanation,
    decimal? EstimatedImpact,
    string? ImpactUnit,
    int ConfidencePercent,
    DateTime GeneratedAtUtc,
    string SnapshotHash);