using HydroPilotWeb.Services.Anomalies;

namespace HydroPilotWeb.Services.Control;

// ------------------------------------------------------------------
// Vista Control
// ------------------------------------------------------------------

/// <summary>Circuito del desplegable de Optimización: nombre y su lote en curso.</summary>
public sealed record ControlCircuitOption(int Id, string Name, string? LotName);

public sealed record ControlCommandDto(
    Guid Id,
    int ActuatorId,
    string ActuatorKind,
    string ActuatorName,
    decimal Amount,
    string Unit,
    string Status,
    string Source,
    string? RequestedBy,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? DeliveredAtUtc,
    DateTime? CompletedAtUtc,
    decimal? ExecutedAmount,
    string? ResultError,
    string Parameter,
    decimal? ValueBefore,
    decimal? Target,
    decimal? Strength = null,
    decimal? ExpectedEffect = null,
    decimal? ValueAfter = null,
    decimal? ObservedEffect = null,
    bool? Effective = null,
    DateTime? EffectEvaluatedAtUtc = null,
    DateTime? DispatchedAtUtc = null);

/// <summary>
/// Una bomba del nodo con lo usado en 24 h y su dosis en curso (si hay). Effect: efecto por ml/L vigente
/// (calibrado o de fábrica; null en el agua), para estimar qué hace una dosis manual.
/// </summary>
public sealed record ControlActuatorDto(
    int Id,
    string Kind,
    string Name,
    string Unit,
    decimal MaxPerCommand,
    decimal MaxPerDay,
    bool IsActive,
    decimal UsedLast24h,
    ControlCommandDto? InFlight,
    decimal? Effect = null,
    int EffectSamples = 0);

public sealed record ControlKpis(int Executed24h, int InFlight, int Problems24h);

/// <summary>Pausa del control automático de una magnitud.</summary>
public sealed record ControlPauseDto(int Id, string Parameter, DateTime PausedAtUtc, string Reason, DateTime? ResumedAtUtc, string? ResumedBy);

/// <summary>Qué está haciendo (o haría) el motor ahora: ControlEngine.Decide sobre los datos de la vista.</summary>
public sealed record ControlStatusDto(
    string Outcome,
    string Code,
    string? Parameter,
    string? PumpName,
    decimal Amount,
    string? Unit,
    decimal Strength,
    DateTime? UntilUtc,
    string? SensorFaultKind = null);

public sealed record ControlViewDto(
    int CircuitId,
    string CircuitName,
    string NodeIdentifier,
    string ConnectionState,
    DateTime? LastCommandPollAtUtc,
    string Mode,
    decimal ReservoirLiters,
    decimal SourceWaterEc,
    int? LotId,
    string? LotName,
    string? StageName,
    SolutionParameterStatus Ph,
    SolutionParameterStatus Ec,
    IReadOnlyList<ControlActuatorDto> Actuators,
    ControlKpis Kpis,
    ControlStatusDto Status,
    IReadOnlyList<ControlPauseDto> OpenPauses,
    IReadOnlyList<ControlTimelineEntry> Timeline,
    int PiContactStaleMinutes = 2,
    ControlRecipeDto? Recipe = null,
    ControlAmbientReading? Temperature = null,
    ControlAmbientReading? Humidity = null,
    DateTime? LotSinceUtc = null);

/// <summary>Última lectura usable del ambiente del invernadero (sensor sin circuito): temperatura °C o humedad %.</summary>
public sealed record ControlAmbientReading(decimal Value, DateTime ObservedAtUtc);

/// <summary>Un punto de la tendencia: promedio de las lecturas de un tramo de tiempo.</summary>
public sealed record ControlTrendPoint(DateTime AtUtc, decimal Value);

/// <summary>Una dosis en la tendencia (cuándo llegó al tanque, o cuándo se pidió si no llegó).</summary>
public sealed record ControlTrendDose(
    DateTime AtUtc,
    string Parameter,
    string Kind,
    string ActuatorName,
    decimal Amount,
    string Unit,
    string Status,
    string Source,
    decimal? Strength,
    bool? Effective);

/// <summary>Tendencia de una magnitud: sus puntos, la banda del lote, la banda en la que actúa el motor y las dosis.</summary>
public sealed record ControlTrendSeries(
    string Parameter,
    IReadOnlyList<ControlTrendPoint> Points,
    SolutionBand Band,
    SolutionBand ActionBand,
    IReadOnlyList<ControlTrendDose> Doses);

/// <summary>pH y CE del tanque en las últimas horas, con las dosis: para ver cómo corrige el control.</summary>
public sealed record ControlTrendDto(DateTime FromUtc, DateTime ToUtc, ControlTrendSeries Ph, ControlTrendSeries Ec);

/// <summary>Receta aprendida del lote del nodo (fase 5): ajuste aplicado a los objetivos de la etapa y por qué.</summary>
public sealed record ControlRecipeDto(
    decimal PhOffset,
    decimal EcOffset,
    decimal HistoryPhOffset,
    decimal HistoryEcOffset,
    decimal Trust,
    int SimilarLots,
    decimal? ScoreTrend,
    string Reason,
    DateTime UpdatedAtUtc);

/// <summary>Acuse de Azure IoT Hub de un comando C2D.</summary>
public enum DeliveryFeedback { Delivered, Rejected, NotDelivered }

public enum ControlTimelineKind
{
    Readings = 0,
    Requested = 1,
    Delivered = 2,
    Result = 3,
    Expired = 4,
    Unconfirmed = 5,
    Cancelled = 6,
    Effect = 7,
    Paused = 8,
    Resumed = 9,
}

/// <summary>Lecturas seguidas juntadas en una fila: cuántas, entre qué horas y los últimos valores.</summary>
public sealed record TimelineReadings(DateTime FirstUtc, DateTime LastUtc, int Count, decimal? Ph, decimal? Ec);

public sealed record ControlTimelineEntry(
    DateTime AtUtc,
    ControlTimelineKind Kind,
    TimelineReadings? Readings,
    ControlCommandDto? Command,
    ControlPauseDto? Pause = null);

public sealed record ControlActionResult(bool Succeeded, string? Message)
{
    public static ControlActionResult Ok(string? message = null) => new(true, message);
    public static ControlActionResult Fail(string message) => new(false, message);
}

public sealed record ControlSweepResult(int Expired, int Unconfirmed, int SetpointsRecorded, int EffectsMeasured = 0, int Paused = 0, int AutoDoses = 0);

// ------------------------------------------------------------------
// Contrato con la Raspberry Pi (notas/control/plan-control-ph-ce.md, sección 3)
// ------------------------------------------------------------------

public static class DeviceContract
{
    public const string TypeDoseCommand = "dose-command";
}

public sealed record DeviceCommandReason(string Parameter, decimal? Value, decimal? Target, string? ReadingId);

/// <summary>Comando de dosis tal como lo recibe la Pi.</summary>
public sealed record DeviceCommandDto(
    string Type,
    Guid CommandId,
    string NodeId,
    string Actuator,
    decimal Amount,
    string Unit,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    string Source,
    DeviceCommandReason Reason);

public sealed record DeviceCommandsResponse(string NodeId, DateTime ServerTimeUtc, IReadOnlyList<DeviceCommandDto> Commands);

/// <summary>Resultado que informa la Pi al terminar (o rechazar) un comando.</summary>
public sealed record DeviceResultRequest(
    string? Status,
    decimal? ExecutedAmount,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error);

public sealed record DeviceResultResponse(Guid CommandId, string Status, bool Duplicate);

public enum DeviceResultOutcome { Applied, Duplicate, NodeNotFound, CommandNotFound, Invalid, Conflict }

public sealed record DeviceResultReport(DeviceResultOutcome Outcome, string? Status, string? Message);
