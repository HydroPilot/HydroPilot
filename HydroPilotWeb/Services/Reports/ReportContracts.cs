using HydroPilotWeb.Models;

namespace HydroPilotWeb.Services.Reports;

/// <summary>
/// Límites globales del módulo de reportes (REP-03, plan 13): ningún reporte
/// intenta cargar todo el histórico sin control. Los rangos demasiado grandes se
/// truncan con advertencia explícita y las tablas detalle van paginadas.
/// </summary>
public static class ReportLimits
{
    /// <summary>Rango máximo de fechas para el reporte de telemetría (días corridos UTC).</summary>
    public const int MaxRangeDays = 92;

    /// <summary>Filas por página por defecto en tablas detalle.</summary>
    public const int DefaultPageSize = 20;

    /// <summary>Tope de filas por página (validación de paginación).</summary>
    public const int MaxPageSize = 50;

    /// <summary>
    /// Si la cantidad de lecturas utilizables del rango supera este valor, la
    /// mediana por sensor no se calcula (REP-02: "mediana si el volumen lo permite").
    /// </summary>
    public const int MedianSampleCap = 5000;

    /// <summary>Tope duro de filas del reporte de cambios de estado.</summary>
    public const int MaxHistoryRows = 1000;
}

/// <summary>
/// Filtros validados del reporte de telemetría (REP-03). El rango es obligatorio;
/// FromUtc y ToUtc son instantes UTC (inclusive). Los demás filtros son opcionales.
/// </summary>
public sealed record TelemetryReportRequest(
    DateTime FromUtc,
    DateTime ToUtc,
    int? LotId = null,
    int? NodeId = null,
    int? SensorId = null,
    int? SensorTypeId = null,
    bool IncludeNonUsable = false);

/// <summary>Parámetros efectivamente usados al generar un reporte (para mostrar y exportar).</summary>
public sealed record ReportAppliedParameters(
    string GeneratedAtUtc,
    IReadOnlyList<string> Lines);

/// <summary>Respuesta del reporte de telemetría: estadísticas por sensor + página de detalle.</summary>
public sealed record TelemetryReportResult(
    ReportAppliedParameters Parameters,
    int RangeDays,
    int TotalReadings,
    int UsableReadings,
    int NonUsableReadings,
    int MockReadings,
    IReadOnlyList<TelemetrySensorStatsDto> Stats,
    int TotalRows,
    IReadOnlyList<TelemetryReadingRowDto> Rows,
    int Page,
    int PageSize,
    bool IncludeNonUsable,
    IReadOnlyList<string> Warnings);

/// <summary>Conteo de lecturas por estado de calidad del contrato IoT v2.</summary>
public sealed record QualityCountDto(string Quality, int Count);

/// <summary>
/// Estadísticas por sensor dentro del rango (REP-02). Min/max/promedio/mediana se
/// calculan SOLO sobre lecturas operacionalmente utilizables
/// (TelemetryQualityPolicy.IsOperationallyUsable): las inválidas nunca contaminan
/// el promedio. Los conteos por calidad cubren todas las lecturas del rango.
/// </summary>
public sealed record TelemetrySensorStatsDto(
    int SensorId,
    string SensorName,
    string SensorType,
    string? Unit,
    int NodeId,
    string NodeIdentifier,
    decimal? Min,
    decimal? Max,
    decimal? Average,
    decimal? Median,
    bool MedianComputed,
    int UsableCount,
    int SuspectCount,
    int InvalidCount,
    int DaysWithData,
    decimal CoveragePercent,
    DateTime? FirstObservedAtUtc,
    DateTime? LastObservedAtUtc,
    decimal? LastValue,
    string? LastQuality,
    IReadOnlyList<QualityCountDto> QualityCounts);

/// <summary>Fila de detalle de una lectura (misma proyección que GET /api/telemetria/lecturas).</summary>
public sealed record TelemetryReadingRowDto(
    long Id,
    DateTime ObservedAtUtc,
    string NodeIdentifier,
    string SensorName,
    string SensorType,
    decimal Value,
    string? Unit,
    string Quality,
    string? QualityReason,
    string IngestionResult,
    int? LotId,
    string? LotLabel);

/// <summary>Filtros del reporte de ciclo de cultivo: lote opcional y fecha de corte.</summary>
public sealed record CycleReportRequest(int? LotId = null, DateOnly? AsOfDate = null);

/// <summary>
/// Fila del reporte de ciclo (REP-02). Las métricas se toman de los servicios
/// públicos existentes (GddService, YieldService) y de las predicciones persistidas
/// por forecasting (Prediction); Reports no recalcula GDD ni rendimiento.
/// </summary>
public sealed record CycleReportRowDto(
    int LotId,
    string? LotName,
    string CropTypeName,
    string? StatusName,
    DateOnly SowingDate,
    decimal AreaM2,
    decimal GddAccumulated,
    decimal GddTarget,
    DateOnly? EstimatedHarvestDate,
    string EstimatedHarvestSource,
    DateOnly? ActualHarvestDate,
    decimal? EstimatedYieldKg,
    decimal? ActualYieldKg,
    int? DaysError,
    decimal? YieldErrorPercent,
    bool HasPrediction,
    DateTime? PredictionGeneratedAt,
    string? PredictionModelVersion,
    int TemperatureReadings,
    int UsableTemperatureReadings,
    IReadOnlyList<string> Warnings);

public sealed record CycleReportResult(
    ReportAppliedParameters Parameters,
    IReadOnlyList<CycleReportRowDto> Rows,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Reporte forecast vs cosecha real (REP-02). Usa la MISMA métrica de precisión
/// del módulo de forecasting (comparación de la predicción más reciente por lote
/// contra el resultado real: MAPE en rendimiento y error absoluto en días para
/// predicciones generadas antes de la cosecha). Cuando forecasting mergee un
/// contrato público de precisión (ForecastResult), Reports debe consumirlo; ver
/// advertencia en el resultado.
/// </summary>
public sealed record ForecastVsHarvestRowDto(
    int LotId,
    string? LotName,
    string CropTypeName,
    DateOnly SowingDate,
    DateOnly? ActualHarvestDate,
    decimal? ActualYieldKg,
    DateOnly? PredictedHarvestDate,
    int? DaysError,
    decimal? PredictedYieldKg,
    decimal? YieldErrorPercent,
    DateTime PredictedAt,
    string? ModelVersion,
    bool DaysComputed);

public sealed record ForecastVsHarvestSummaryDto(
    int Cycles,
    int CyclesWithYield,
    int CyclesWithDays,
    decimal? AverageMapePercent,
    decimal? AverageDaysError);

public sealed record ForecastVsHarvestResult(
    ReportAppliedParameters Parameters,
    IReadOnlyList<ForecastVsHarvestRowDto> Rows,
    ForecastVsHarvestSummaryDto Summary,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Reporte de estado de lote (REP-06). La predominancia comercial (incluido
/// "Mixto" ante empate) proviene del dominio (LotAggregateService/Predominance);
/// Reports la consume, no la recalcula.
/// </summary>
public sealed record LotStateReportResult(
    ReportAppliedParameters Parameters,
    HydroPilotWeb.Services.Lotes.LotStateDto? State,
    IReadOnlyList<string> Warnings);

/// <summary>Filtros del reporte de plantas (REP-07).</summary>
public sealed record PlantReportRequest(
    int? LotId = null,
    int? Row = null,
    int? Column = null,
    PlantOperationalState? OperationalState = null,
    int? CommercialStageId = null);

/// <summary>Fila del reporte de plantas: posición, estados, última evaluación/imagen.</summary>
public sealed record PlantReportRowDto(
    int PlantId,
    int LotId,
    string? LotName,
    int Row,
    int Column,
    string? CommercialStageName,
    int? CommercialStageId,
    PlantOperationalState OperationalState,
    string? PhenologicalStageName,
    decimal? BabyLeafScore,
    decimal? GrowthRate,
    string? LastEvaluationResult,
    DateTime? LastEvaluationAtUtc,
    bool HasImage,
    DateOnly? HarvestDate,
    DateOnly? DiscardDate,
    string? DiscardReason);

public sealed record PlantReportResult(
    ReportAppliedParameters Parameters,
    int TotalRows,
    IReadOnlyList<PlantReportRowDto> Rows,
    int Page,
    int PageSize,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Fila del reporte de cambios de estado (REP-08): cada transición persistida en
/// PlantStageHistory con nombres resueltos de etapas comerciales.
/// </summary>
public sealed record StageChangeRowDto(
    int HistoryId,
    int PlantId,
    int LotId,
    string? LotName,
    int Row,
    int Column,
    string? PreviousCommercialStage,
    string? NewCommercialStage,
    string? PreviousOperationalState,
    string? NewOperationalState,
    string Source,
    string? Reason,
    DateTime ChangedAtUtc);

public sealed record StageChangeReportResult(
    ReportAppliedParameters Parameters,
    int TotalRows,
    IReadOnlyList<StageChangeRowDto> Rows,
    bool IsTruncated,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Diseño de datos del reporte de anomalías (REP-01). El módulo de anomalías aún
/// no existe en dev; el reporte se declara PENDIENTE y NO se inventan datos.
/// Cuando anomalies mergee su contrato (tipo, severidad, episodios, duración,
/// estado, tiempo a resolución), este DTO se conecta a su consulta.
/// </summary>
public sealed record AnomalyReportDto(
    string Type,
    string Severity,
    int EpisodeCount,
    TimeSpan? AverageDuration,
    string Status,
    int? LotId,
    string? LotName,
    int? SensorId,
    string? SensorName,
    TimeSpan? AverageTimeToResolution);

public sealed record AnomalyReportResult(
    bool IsPending,
    ReportAppliedParameters Parameters,
    IReadOnlyList<AnomalyReportDto> Rows,
    IReadOnlyList<string> Warnings);

/// <summary>Opciones de filtros disponibles para la UI de reportes.</summary>
public sealed record ReportFilterOptions(
    IReadOnlyList<LotOptionDto> Lots,
    IReadOnlyList<NodeOptionDto> Nodes,
    IReadOnlyList<SensorOptionDto> Sensors,
    IReadOnlyList<SensorTypeOptionDto> SensorTypes,
    IReadOnlyList<CommercialStageOptionDto> Stages);

public sealed record LotOptionDto(int Id, string Label);
public sealed record NodeOptionDto(int Id, string Identifier);
public sealed record SensorOptionDto(int Id, string Label, int NodeId, string? TypeName);
public sealed record SensorTypeOptionDto(int Id, string Name);
public sealed record CommercialStageOptionDto(int Id, string Name);

/// <summary>Resultado de exportación CSV: contenido y nombre de archivo determinista.</summary>
public sealed record CsvExportResult(string FileName, byte[] Content);