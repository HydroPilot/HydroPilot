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
    string? ResolutionReason,
    decimal? PeakValue = null);

/// <summary>Filtros de los episodios (Optimización usa el nodo).</summary>
public sealed record AnomalyFilter(
    string? Severity,
    string? Type,
    int? LotId,
    string? Status,
    DateOnly? From,
    DateOnly? To,
    int? NodeId = null,
    int? CircuitId = null); // solo los de los sensores del circuito (un lote puede estar en el denso y en Adultas)

/// <summary>
/// Regla de anomalía para Parámetros (solo lectura). Bands: una sola (pH, o temp/humedad con umbral
/// aprobado) o una por etapa (CE); vacía = sin umbral aprobado, no se evalúa.
/// </summary>
public sealed record AnomalyRuleOverview(
    string Name,
    string SensorTypeName,
    IReadOnlyList<AnomalyRuleBand> Bands,
    int ConsecutiveToOpen,
    int CooldownMinutes,
    bool Active);

/// <summary>Banda de una regla; Stage = etapa fenológica (solo CE), null = vale para todo el ciclo.</summary>
public sealed record AnomalyRuleBand(string? Stage, decimal? Min, decimal? Max, decimal? Target);
