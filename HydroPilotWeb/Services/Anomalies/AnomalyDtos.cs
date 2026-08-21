using HydroPilotWeb.Models;

namespace HydroPilotWeb.Services.Anomalies;

/// <summary>
/// Contrato compartido del evento de anomalía (plan 02, "Evento de anomalía").
/// Notifications consume este DTO para entregar alertas SIN decidir si la lectura
/// es anómala (esa decisión es del motor de anomalías). Incluye tipo, lote/
/// invernadero/nodo/sensor, severidad, valor observado y objetivo, regla, primera
/// y última observación, cantidad de lecturas, estado, fingerprint y origen.
/// </summary>
public sealed record AnomalyEventDto(
    int Id,
    string Type,                       // código estable (AnomalyContract.Type*)
    string TypeName,                   // nombre de la regla (explícito)
    int LotId,
    string? LotName,
    int? GreenhouseId,
    string? GreenhouseName,
    int? NodeId,
    string? NodeName,
    int? SensorId,
    string? SensorName,
    string Severity,                   // Advertencia | Crítica
    decimal ObservedValue,
    decimal? TargetValue,
    decimal? OperationalMin,
    decimal? OperationalMax,
    string RuleCode,
    string? RuleDescription,
    DateTime FirstObservedAtUtc,
    DateTime LastObservedAtUtc,
    int ReadingCount,
    string Status,                     // estado interno (Seguimiento/Abierta/Reconocida/Resuelta)
    string ContractStatus,             // abierta | reconocida | resuelta (contrato plan 02)
    string Fingerprint,
    string Origin,
    DateTime? AcknowledgedAtUtc,
    DateTime? ResolvedAtUtc,
    string? ResolutionReason);

/// <summary>
/// Resumen del módulo (consumible por dashboard/reports): contadores en vez de la lista completa.
/// HasOperationalData false = sin lecturas usables en la ventana: la UI NO puede afirmar normalidad.
/// </summary>
public sealed record AnomalySummaryDto(
    int OpenTotal,
    int OpenCritical,
    int OpenAdvertencias,
    int ResolvedLast7Days,
    int SeguimientoActive,
    int TotalEvents,
    bool HasOperationalData);

/// <summary>Regla del catálogo con su banda resuelta (para la UI y para documentar pendientes).</summary>
public sealed record AnomalyRuleDto(
    int Id,
    string Code,
    string Name,
    string SensorTypeName,
    int ConsecutiveToOpen,
    int CooldownMinutes,
    bool IsActive,
    string Source,
    string? Notes,
    decimal? PhysicalMin,
    decimal? PhysicalMax,
    decimal? OperationalMin,
    decimal? OperationalMax,
    decimal? TargetValue,
    bool Pending); // true = sin valor aprobado / inactiva → nunca dispara (pendiente visible)

/// <summary>Filtros de la timeline de la UI (ANO-05).</summary>
public sealed record AnomalyFilter(
    string? Severity,
    string? Type,
    int? LotId,
    string? Status,
    DateOnly? From,
    DateOnly? To);

/// <summary>Riesgo por lote (ANO-08): conteo de plantas en riesgo, SIN umbral inventado de evento de lote.</summary>
public sealed record LotRiskSummaryDto(
    int LotId,
    string? LotName,
    int ActivePlants,
    int PlantsAtRisk,
    int OpenCriticalEpisodes,
    string? RiskReason);

/// <summary>Resumen del carril futuro de visión (ANO-06): no hay detector hoy; los eventos quedan pendientes.</summary>
public sealed record VisionAnomalyAdapterNote(
    string State,        // "pendiente"
    string Reason);      // por qué no hay detección por imágenes todavía