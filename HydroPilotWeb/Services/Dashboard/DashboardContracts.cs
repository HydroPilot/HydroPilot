namespace HydroPilotWeb.Services.Dashboard;

/// <summary>
/// Contrato del dashboard (plan 12, DASH-01). Snapshot inmutable por consulta:
/// contexto de invernadero/lote, KPIs por tipo de sensor, estado de nodos por
/// frescura, anomalías y recomendaciones (solo si tienen fuente) y la serie
/// temporal seleccionada.
///
/// Reglas de ética de datos del contrato:
/// - Un valor nulo NUNCA se convierte en cero: el KPI queda en "Sin datos".
/// - La frescura es un estado calculado (reciente / desactualizado / sin datos),
///   distinto de la calidad de la lectura (VALID/SUSPECT/STALE/...).
/// - El delta es un cálculo real contra la lectura anterior del mismo tipo.
/// - Anomalías y recomendaciones solo se muestran cuando el módulo que las
///   produce está integrado y devuelve datos; mientras tanto, listas vacías.
/// </summary>
public static class DashboardVariableKeys
{
    public const string Ph = "ph";
    public const string Ce = "ce";
    public const string Temperatura = "temperatura";
    public const string Humedad = "humedad";

    /// <summary>
    /// Normaliza el nombre del tipo de sensor del catálogo a una clave canónica de
    /// dashboard. Los nombres vienen del catálogo configurado (no hardcodeados en la
    /// vista); esta función solo agrupa sinónimos conocidos.
    /// </summary>
    public static string Normalize(string? sensorTypeName)
    {
        if (string.IsNullOrWhiteSpace(sensorTypeName))
            return string.Empty;

        return sensorTypeName.Trim().ToLowerInvariant() switch
        {
            "ph" => Ph,
            "ce" or "ec" or "conductividad" => Ce,
            "temperatura" or "temp" => Temperatura,
            "humedad" or "hum" => Humedad,
            var other => other
        };
    }

    /// <summary>Orden de presentación de los KPIs canónicos (pH, CE, temp, humedad).</summary>
    public static int DisplayOrder(string key) => key switch
    {
        Ph => 0,
        Ce => 1,
        Temperatura => 2,
        Humedad => 3,
        _ => 100
    };

    /// <summary>Icono de presentación por clave canónica (solo visual; el nombre y la unidad vienen del catálogo).</summary>
    public static string IconFor(string key) => key switch
    {
        Ph => "💧",
        Ce => "◌",
        Temperatura => "🌡",
        Humedad => "🍃",
        _ => "📊"
    };
}

/// <summary>Rangos de la serie temporal del dashboard (DASH-04).</summary>
public enum DashboardTimeRange
{
    Hour1 = 1,
    Day1 = 2,
    Days7 = 3,
    Days30 = 4,
}

public static class DashboardTimeRangeExtensions
{
    public static TimeSpan ToDuration(this DashboardTimeRange range) => range switch
    {
        DashboardTimeRange.Hour1 => TimeSpan.FromHours(1),
        DashboardTimeRange.Day1 => TimeSpan.FromDays(1),
        DashboardTimeRange.Days7 => TimeSpan.FromDays(7),
        DashboardTimeRange.Days30 => TimeSpan.FromDays(30),
        _ => TimeSpan.FromHours(1)
    };

    public static string ToLabel(this DashboardTimeRange range) => range switch
    {
        DashboardTimeRange.Hour1 => "1 hora",
        DashboardTimeRange.Day1 => "1 día",
        DashboardTimeRange.Days7 => "7 días",
        DashboardTimeRange.Days30 => "30 días",
        _ => string.Empty
    };
}

/// <summary>
/// Agrupación de la serie temporal (DASH-04): el rango se parte en a lo sumo
/// <c>maxPoints</c> buckets. Lógica pura, testeable sin base de datos.
/// </summary>
public static class DashboardSeriesAggregation
{
    public static int BucketSeconds(DashboardTimeRange range, int maxPoints)
    {
        var max = Math.Max(maxPoints, 1);
        var durationSeconds = range.ToDuration().TotalSeconds;
        return (int)Math.Max(1, Math.Ceiling(durationSeconds / max));
    }
}

/// <summary>
/// Estados de frescura de un valor del dashboard y cálculo de delta real.
/// Lógica pura, testeable sin base de datos.
/// </summary>
public static class DashboardFreshness
{
    public const string Reciente = "reciente";
    public const string Desactualizado = "desactualizado";
    public const string SinDatos = "sin_datos";

    /// <summary>
    /// Frescura según la antigüedad de la observación. Una lectura sin datos da
    /// "sin_datos"; si su edad supera la ventana configurada, "desactualizado"
    /// (el valor STALE se muestra pero avisando). El valor nunca se vuelve 0.
    /// </summary>
    public static string ForValue(DateTime? observedAtUtc, DateTime nowUtc, TimeSpan freshnessWindow)
    {
        if (observedAtUtc is null)
            return SinDatos;

        // Lecturas con timestamp futuro (FUTURE) no entran por calidad; por
        // robustez, un timestamp posterior al "ahora" se trata como reciente.
        if (observedAtUtc.Value > nowUtc)
            return Reciente;

        return nowUtc - observedAtUtc.Value <= freshnessWindow
            ? Reciente
            : Desactualizado;
    }

    /// <summary>
    /// Delta porcentual real entre la última lectura y la anterior del mismo tipo.
    /// Null cuando no hay lectura anterior, no hay lectura actual o el anterior es 0
    /// (dividir por cero no es una comparación honesta).
    /// </summary>
    public static double? DeltaPercent(decimal? latest, decimal? previous)
    {
        if (latest is null || previous is null || previous == 0m)
            return null;

        return (double)((latest.Value - previous.Value) / Math.Abs(previous.Value)) * 100d;
    }

    /// <summary>Delta absoluto real (útil para pH, donde el porcentaje confunde).</summary>
    public static decimal? DeltaAbsolute(decimal? latest, decimal? previous)
    {
        if (latest is null || previous is null)
            return null;

        return latest.Value - previous.Value;
    }
}

/// <summary>
/// Un KPI del dashboard: último valor operativamente usable por tipo de sensor,
/// con unidad, timestamp, origen (nodo/sensor), calidad, frescura y delta real.
/// </summary>
public sealed record DashboardKpiDto(
    string Key,
    string Label,
    string Icon,
    decimal? Value,
    string Unit,
    DateTime? ObservedAtUtc,
    string? NodeIdentifier,
    string? SensorName,
    string? Quality,
    string FreshnessState,
    decimal? PreviousValue,
    DateTime? PreviousObservedAtUtc,
    double? DeltaPercent);

/// <summary>Nodo del invernadero con su estado de conexión calculado por frescura (IoT).</summary>
public sealed record DashboardNodeDto(
    int NodeId,
    string Identifier,
    string ConnectionState,
    string Status,
    DateTime? LastAcceptedAt,
    DateTime? LastRejectedAt,
    int ExpectedIntervalSeconds,
    int SensorCount,
    int RejectionsLast24h);

/// <summary>Contexto del invernadero y lote activo que muestra el dashboard.</summary>
public sealed record DashboardContextDto(
    int? GreenhouseId,
    string GreenhouseName,
    string? Location,
    int? ActiveLotId,
    string? ActiveLotName,
    string? ActiveLotCrop);

/// <summary>Punto de la serie temporal (promedio por bucket de tiempo).</summary>
public sealed record DashboardSeriesPointDto(DateTime BucketEndUtc, decimal Value, int ReadingCount);

/// <summary>Serie temporal de una variable en un rango, agregada para no descargar todo el histórico.</summary>
public sealed record DashboardSeriesDto(
    string VariableKey,
    string Label,
    string Unit,
    DashboardTimeRange Range,
    IReadOnlyList<DashboardSeriesPointDto> Points);

/// <summary>Anomalía reciente (fuente: módulo de anomalías; lista vacía mientras no esté integrado).</summary>
public sealed record DashboardAnomalyDto(string Type, string Severity, string? Detail, DateTime? FirstObservedAtUtc);

/// <summary>Recomendación pendiente (fuente: módulo de optimization; lista vacía mientras no esté integrado).</summary>
public sealed record DashboardRecommendationDto(string Type, string Priority, string? Summary);

/// <summary>Snapshot completo del dashboard (DASH-01). Inmutable por consulta.</summary>
public sealed record DashboardSnapshot(
    DashboardContextDto Context,
    IReadOnlyList<DashboardKpiDto> Kpis,
    IReadOnlyList<DashboardNodeDto> Nodes,
    IReadOnlyList<DashboardAnomalyDto> Anomalies,
    IReadOnlyList<DashboardRecommendationDto> Recommendations,
    DashboardSeriesDto Series,
    DateTime GeneratedAtUtc);

/// <summary>Estado resumido para la barra superior (contexto + estado global derivado de nodos).</summary>
public sealed record DashboardHeaderStatus(
    string GreenhouseName,
    string Subtitle,
    string StatusText,
    string StatusCss);