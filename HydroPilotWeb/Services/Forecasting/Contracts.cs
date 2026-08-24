using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Services.Forecasting;

/// <summary>
/// Fuente del dato de un día de GDD (plan 10, F-02/F-03):
/// Observed = sensor ambiental del invernadero; Forecast = pronóstico climático
/// persistido; Fallback = promedio de días observados recientes.
/// </summary>
public enum GddPointSource
{
    Observed,
    Forecast,
    Fallback
}

/// <summary>
/// Punto diario de GDD con su fuente. Reemplaza los DailyGddPoint duplicados
/// (Controllers.ForecastingDtos y Services.GddService) por un único contrato
/// compartido por API y UI (F-01).
/// </summary>
public sealed record DailyGddPoint(DateOnly Date, decimal Gdd, GddPointSource Source);

/// <summary>
/// Resultado del cálculo de GDD histórico de un lote (F-02): valores diarios,
/// cobertura del período y advertencias. Los días sin lectura NO se convierten
/// en cero silenciosamente: se informan como faltantes.
/// </summary>
public sealed record GddHistoryResult(
    IReadOnlyList<DailyGddPoint> Daily,
    int PeriodDays,
    int CoverageDays,
    int MissingDays,
    decimal? CoveragePercent,
    IReadOnlyList<string> Warnings);

/// <summary>Proyección futura de GDD (F-03): puntos por fecha + estado del pronóstico.</summary>
public sealed record FutureProjectionResult(
    IReadOnlyList<DailyGddPoint> Points,
    bool HasForecastData,
    bool ForecastIsStale,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Clasificación gruesa del resultado (F-01): Observed (solo sensor), Forecast
/// (proyección con pronóstico), Fallback (hubo días sin dato cubiertos con
/// promedio) o Mock (lecturas sintéticas del script demo detectadas).
/// </summary>
public enum ForecastSourceKind
{
    Observed,
    Forecast,
    Fallback,
    Mock
}

/// <summary>
/// Ventana GDD + regla híbrida de cosecha del lote (plan 10, F-10):
/// Baby Leaf = ventana GDD configurada MÁS porcentaje de plantas aptas;
/// convencional = ventana posterior con criterio de tamaño/madurez (análisis de
/// imagen, fase futura). Si el porcentaje objetivo no está configurado,
/// IsReady = null (decisión pendiente), NUNCA una constante oculta.
/// </summary>
public sealed record HarvestWindowInfo(
    bool IsBabyLeaf,
    decimal WindowMin,
    decimal WindowMax,
    bool GddInWindow,
    int AptaCount,
    int TotalActivePlants,
    decimal AptaPercent,
    decimal? RequiredPercent,
    bool? IsReady,
    DateOnly? WindowEntryDate,
    string? NotReadyReason,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Resultado común de forecast a nivel lote (F-01): lo consumen el controller
/// y la página Razor (mismos valores), y es la base estable para dashboard,
/// reports y simulation.
/// </summary>
public sealed record ForecastResult(
    int LotId,
    string? LotName,
    string CropTypeName,
    string? StatusName,
    DateOnly SowingDate,
    decimal AreaM2,
    DateOnly AsOfDate,
    bool IsSimulation,
    string ModelVersion,
    // --- GDD (F-01/F-02) ---
    decimal GddAccumulated,
    decimal GddTarget,
    decimal BaseTemperature,
    decimal GddDailyAverage,
    IReadOnlyList<DailyGddPoint> GddHistory,
    IReadOnlyList<DailyGddPoint> FutureProjection,
    int ForecastHorizonDays,
    int CoverageDays,
    int MissingDays,
    decimal? CoveragePercent,
    string DataSourceSummary,
    ForecastSourceKind SourceKind,
    // --- Fechas (F-03/F-10) ---
    DateOnly? GddHarvestDate,
    int? GddDaysRemaining,
    HarvestWindowInfo? HarvestWindow,
    DateOnly? EstimatedHarvestDate,
    int? DaysRemaining,
    // --- Fenología y comercial (F-08/F-09/F-11) ---
    string? PhenologicalStageName,
    int? PhenologicalStageOrder,
    string? CommercialStageName,
    bool IsCommercialStageMixed,
    IReadOnlyList<StageCountDto> CommercialCounts,
    // --- Rendimiento y precisión (F-05) ---
    YieldEstimate Yield,
    decimal? AccuracyMape,
    decimal? AccuracyDaysError,
    int AccuracyCycles,
    // --- Cierre del lote ---
    decimal? ActualYieldKg,
    DateOnly? ActualHarvestDate,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Nombres canónicos de los estados de lote (catálogo LotStatus).
/// Se resuelven por nombre (nunca por ID fijo) — decisión F-04.
/// </summary>
public static class LotStatusNames
{
    public const string Activo = "ACTIVO";
    public const string Cosechado = "COSECHADO";
    public const string Descartado = "DESCARTADO";
    public const string EnPausa = "EN_PAUSA";
}

/// <summary>Resultado de rendimiento estimado (F-05), compartido por YieldService.</summary>
public sealed record YieldEstimate(
    decimal Conservative,
    decimal Base,
    decimal Optimistic,
    int ConfidencePercent,
    int HistoryCycles);

/// <summary>Entrada de un ciclo cerrado para el cálculo de precisión (F-05).</summary>
public sealed record ClosedCycleInput(
    decimal? ActualYieldKg,
    DateOnly? ActualHarvestDate,
    IReadOnlyList<PredictionInput> Predictions);

/// <summary>Predicción candidata para precisión (F-05).</summary>
public sealed record PredictionInput(
    DateOnly? AsOfDate,
    DateTime GeneratedAt,
    decimal? EstimatedYield,
    DateOnly? EstimatedHarvestDate);

/// <summary>Resultado de precisión del modelo contra ciclos cerrados (F-05).</summary>
public sealed record AccuracyResult(
    decimal? Mape,
    decimal? DaysError,
    int Cycles);

/// <summary>
/// Lógica pura de precisión (F-05): centralizada para que API y UI no diverjan.
/// Reglas: solo la última predicción válida ANTERIOR a la cosecha; MAPE no se
/// calcula con rendimiento real cero; error de días separado del de rendimiento;
/// se informa la cantidad de ciclos elegibles.
/// </summary>
public static class ForecastAccuracy
{
    /// <summary>Fecha efectiva de cálculo de una predicción: AsOfDate o su fecha de generación.</summary>
    public static DateOnly EffectiveDate(PredictionInput p) =>
        p.AsOfDate ?? DateOnly.FromDateTime(p.GeneratedAt);

    public static AccuracyResult Compute(IReadOnlyList<ClosedCycleInput> cycles)
    {
        var mape = new List<decimal>();
        var dayErrors = new List<decimal>();
        var eligible = 0;

        foreach (var cycle in cycles)
        {
            // Fecha real de cierre: sin fecha no hay ciclo elegible para días.
            if (cycle.ActualHarvestDate is not { } harvestDate)
                continue;

            // Última predicción válida anterior a la cosecha (puede ser el mismo día).
            var prediction = cycle.Predictions
                .Where(p => EffectiveDate(p) <= harvestDate)
                .OrderByDescending(p => EffectiveDate(p))
                .ThenByDescending(p => p.GeneratedAt)
                .FirstOrDefault();

            if (prediction is null)
                continue;

            eligible++;

            // MAPE: nunca con rendimiento real cero ni estimado no positivo.
            if (cycle.ActualYieldKg is { } actual && actual > 0
                && prediction.EstimatedYield is { } estimated && estimated > 0)
            {
                mape.Add(Math.Abs(estimated - actual) / actual * 100m);
            }

            // Error de días (separado del rendimiento).
            if (prediction.EstimatedHarvestDate is { } estimatedDate)
            {
                dayErrors.Add(Math.Abs(
                    (estimatedDate.ToDateTime(TimeOnly.MinValue)
                     - harvestDate.ToDateTime(TimeOnly.MinValue)).Days));
            }
        }

        return new AccuracyResult(
            mape.Count > 0 ? Math.Round(mape.Average(), 1) : null,
            dayErrors.Count > 0 ? Math.Round(dayErrors.Average(), 1) : null,
            eligible);
    }
}

/// <summary>Lógica pura de fechas de proyección de GDD (F-03/F-10), testeable sin I/O.</summary>
public static class GddDateLogic
{
    /// <summary>Fecha en que el GDD acumulado cruza un umbral usando la proyección día a día.</summary>
    public static DateOnly? EstimateCrossDate(
        decimal accumulated,
        IReadOnlyList<DailyGddPoint> projection,
        decimal threshold)
    {
        if (accumulated >= threshold)
            return projection.Count > 0 ? projection[0].Date.AddDays(-1) : null;

        var remaining = threshold - accumulated;
        foreach (var point in projection)
        {
            remaining -= point.Gdd;
            if (remaining <= 0)
                return point.Date;
        }

        // No alcanzó dentro del horizonte: extrapolar con el último GDD diario.
        if (projection.Count > 0)
        {
            var last = projection[^1].Gdd;
            if (last > 0)
            {
                var extraDays = (int)Math.Ceiling(remaining / last);
                return projection[^1].Date.AddDays(extraDays);
            }
        }

        return null;
    }

    /// <summary>Días desde asOfDate hasta la fecha destino (null si falta fecha destino).</summary>
    public static int? DaysRemaining(DateOnly asOfDate, DateOnly? target) =>
        target is { } t ? (t.ToDateTime(TimeOnly.MinValue) - asOfDate.ToDateTime(TimeOnly.MinValue)).Days : null;
}