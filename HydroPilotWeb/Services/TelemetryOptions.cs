namespace HydroPilotWeb.Services;

/// <summary>
/// Opciones configurables del módulo IoT. Se bindean desde la sección "Telemetry"
/// de la configuración; los valores por defecto son los del contrato.
/// </summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>Máximo de lecturas permitidas por batch.</summary>
    public int MaxReadingsPerBatch { get; set; } = 1000;

    /// <summary>Intervalo del monitor de conexión de nodos, en segundos.</summary>
    public int MonitorIntervalSeconds { get; set; } = 60;

    /// <summary>Tolerancia para considerar un timestamp como futuro.</summary>
    public int FutureToleranceMinutes { get; set; } = 5;

    /// <summary>Edad mínima desde la cual una lectura se marca STALE.</summary>
    public int StaleMinAgeHours { get; set; } = 24;

    /// <summary>Multiplicador del intervalo esperado para la edad STALE (máx(24h, N×intervalo)).</summary>
    public int StaleIntervalFactor { get; set; } = 4;

    /// <summary>Multiplicador del intervalo esperado para considerar el nodo DEGRADED.</summary>
    public int DegradedIntervalFactor { get; set; } = 2;
}