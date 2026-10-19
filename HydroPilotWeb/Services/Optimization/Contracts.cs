namespace HydroPilotWeb.Services.Optimization;

/// <summary>Una recomendación tal como la muestra la vista (y la API).</summary>
public sealed record RecommendationDto(
    int Id,
    int LotId,
    string Type,
    string Title,
    string Status,
    string Priority,
    string Direction,
    decimal? Impact,
    string? ImpactKind,     // OptimizationImpactKind; null en las de la lógica anterior
    bool IsEstimate,        // ≈ (supuesto en duda)
    DateOnly? DueDate,
    IReadOnlyList<RecommendationFigure> Figures,
    string Summary,
    DateTime GeneratedAtUtc,
    DateTime? DecidedAtUtc,
    string? DecisionNote,
    string? DecidedBy,
    decimal? RealizedAmount,
    DateTime? ClosedAtUtc);

/// <summary>Números de arriba de la vista: pendientes, plata a capturar, ahorro por semana y lo logrado en el ciclo.</summary>
public sealed record OptimizationKpis(
    int Pending,
    int HighPriorityPending,
    decimal? ToCapture,
    decimal? WeeklySaving,
    decimal? Achieved,
    int Done);

/// <summary>Vista de Optimización de un lote.</summary>
public sealed record OptimizationViewDto(
    int LotId,
    string LotName,
    bool IsClosed,
    string Currency,
    OptimizationKpis Kpis,
    IReadOnlyList<RecommendationDto> Active,   // pendientes y aceptadas (en curso)
    IReadOnlyList<RecommendationDto> History,  // cerradas y descartadas
    IReadOnlyList<string> Missing);            // parámetros sin definir que dejan impactos sin calcular

/// <summary>Resultado de aceptar/descartar (idempotente).</summary>
public sealed record DecisionResult(
    bool Succeeded,
    bool Repeated,
    string Message,
    RecommendationDto? Recommendation);

/// <summary>Evento hacia Notificaciones cuando aparece una recomendación nueva de prioridad alta.</summary>
public sealed record RecommendationEventDto(
    int RecommendationId,
    int LotId,
    string? LotName,
    string RecommendationType,
    string Title,
    string Summary,
    string Priority,
    decimal? Impact,
    string? ImpactKind,
    DateTime GeneratedAtUtc);
