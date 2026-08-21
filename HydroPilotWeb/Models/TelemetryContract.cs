namespace HydroPilotWeb.Models;

/// <summary>
/// Constantes del contrato de telemetría v2 (versión estable del envelope).
/// Los códigos aquí definidos son los que se persisten y se devuelven por API,
/// y son los mismos términos que consumen forecasting, dashboard, anomalías y reportes.
/// </summary>
public static class TelemetryContract
{
    /// <summary>Versión del envelope soportada como contrato primario.</summary>
    public const string SchemaVersionV2 = "2";

    /// <summary>Versión legada del payload (nodoId/timestamp/lecturas), aceptada por adaptador.</summary>
    public const string SchemaVersionV1 = "1";

    // --- Estados de calidad de una lectura (plan 11-iot) ---
    public const string QualityValid = "VALID";
    public const string QualitySuspect = "SUSPECT";
    public const string QualityInvalid = "INVALID";
    public const string QualityStale = "STALE";
    public const string QualityFuture = "FUTURE";
    public const string QualityNoData = "NO_DATA";
    public const string QualitySensorError = "SENSOR_ERROR";

    // --- Resultado por lectura (plan 11-iot) ---
    public const string ResultAceptada = "aceptada";
    public const string ResultDuplicada = "duplicada";
    public const string ResultDesconocida = "desconocida";
    public const string ResultInvalida = "invalida";
    public const string ResultFueraDeRango = "fuera_de_rango";
    public const string ResultSinLote = "sin_lote";
    public const string ResultErrorUnidad = "error_de_unidad";
    public const string ResultConflicto = "conflicto";

    // --- Resultado a nivel batch ---
    public const string BatchResultProcesado = "procesado";
    public const string BatchResultDuplicado = "duplicado";
    public const string BatchResultConflicto = "conflicto";

    // --- Motivos de rechazo / cuarentena (TelemetryRejection.Reason) ---
    public const string ReasonSensorDesconocido = "sensor_desconocido";
    public const string ReasonErrorUnidad = "error_unidad";
    public const string ReasonTimestampFuturo = "timestamp_futuro";
    public const string ReasonTimestampAnterior = "timestamp_anterior";
    public const string ReasonValorFaltante = "valor_faltante";
    public const string ReasonObservedAtFaltante = "observed_at_faltante";
    public const string ReasonReadingIdFaltante = "reading_id_faltante";
    public const string ReasonBatchIdFaltante = "batch_id_faltante";
    public const string ReasonNodeIdFaltante = "node_id_faltante";
    public const string ReasonLecturasVacio = "lecturas_vacio";
    public const string ReasonLimiteBatch = "limite_batch";
    public const string ReasonNoDataReportado = "no_data_reportado";
    public const string ReasonSensorErrorReportado = "sensor_error_reportado";
    public const string ReasonFueraDeRango = "fuera_de_rango";
    public const string ReasonFueraDeBanda = "fuera_de_banda_operativa";

    // --- Origen de la asignación nodo → lote ---
    public const string AssignmentSourceManual = "manual";
    public const string AssignmentSourceAuto = "auto";
    public const string AssignmentSourceFixture = "fixture";

    // --- Estados de conexión del nodo (plan 11-iot) ---
    public const string ConnectionNeverConnected = "NEVER_CONNECTED";
    public const string ConnectionOnline = "ONLINE";
    public const string ConnectionDegraded = "DEGRADED";
    public const string ConnectionOffline = "OFFLINE";

    // --- Estado de asignación de una lectura ---
    public const string AssignmentStateAsignado = "ASIGNADO";
    public const string AssignmentStateSinAsignacion = "SIN_ASIGNACION";
}