using HydroPilotWeb.Services;

namespace HydroPilotWeb.Controllers;

// DTOs de la API de telemetría.
//
// Contrato v2 (envelope versionado). El payload legado v1 (nodoId/timestamp/lecturas
// con sensorRef/valor) sigue aceptándose a través del adaptador del parser; el
// formato de respuesta cambia a resultado por lectura (decisión documentada).

/// <summary>Error estructurado de la API (400/404/409).</summary>
public record TelemetryErrorDto(string Error);

/// <summary>Respuesta de ingesta de un batch (resultado por lectura).</summary>
public record TelemetryIngestResponse(
    string BatchId,
    string BatchResult,
    int Inserted,
    int Duplicated,
    int Rejected,
    DateTime ReceivedAtUtc,
    IReadOnlyList<ReadingIngestionRecord> Readings
);

/// <summary>
/// Lectura persistida tal como se consulta históricamente.
/// EXTENDIDO (aditivo) respecto del formato original: se conservan los campos
/// previos (Id, SensorId, SensorName, SensorType, Value, Unit, Timestamp, CreatedAt)
/// y se agregan calidad, fechas explícitas y contexto de lote/asignación.
/// </summary>
public record ReadingQueryResponse(
    int Id,
    int SensorId,
    string SensorName,
    string SensorType,
    decimal Value,
    string? Unit,
    DateTime Timestamp,
    DateTime CreatedAt,
    string Quality,
    string? QualityReason,
    string IngestionResult,
    DateTime ObservedAtUtc,
    DateTime ReceivedAtUtc,
    int NodeId,
    string NodeIdentifier,
    int? LotId,
    string? LotLabel,
    string AssignmentState,
    int? NodeLotAssignmentId
);

/// <summary>Lectura rechazada (cuarentena de diagnóstico): TelemetryRejection.</summary>
public record RejectionQueryResponse(
    int Id,
    int NodeId,
    string NodeIdentifier,
    string BatchId,
    string ReadingId,
    string SensorRef,
    DateTime? ObservedAtUtc,
    DateTime ReceivedAtUtc,
    decimal? Value,
    string? Unit,
    string Reason,
    string? Quality,
    string Result
);

/// <summary>Estado y frescura de un nodo (consumido por el dashboard).</summary>
public record NodeStatusResponse(
    int Id,
    string Identifier,
    int GreenhouseId,
    string AdminStatus,
    string ConnectionState,
    DateTime? LastConnection,
    DateTime? LastAcceptedAt,
    DateTime? LastRejectedAt,
    int ExpectedIntervalSeconds,
    int SensorCount,
    int RejectionsLast24h
);

public record NodeLotAssignmentCreateRequest(
    int NodeId,
    int LotId,
    DateTime ValidFromUtc,
    DateTime? ValidUntilUtc,
    string? Source
);