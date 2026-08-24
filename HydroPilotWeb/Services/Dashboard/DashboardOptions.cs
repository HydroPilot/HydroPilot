namespace HydroPilotWeb.Services.Dashboard;

/// <summary>
/// Opciones del dashboard (sección "Dashboard" de configuración; los valores por
/// defecto son los del plan 12 y no requieren configuración para funcionar).
/// </summary>
public sealed class DashboardOptions
{
    public const string SectionName = "Dashboard";

    /// <summary>Ventana de frescura de un KPI: a partir de esta antigüedad el valor se muestra como desactualizado.</summary>
    public int KpiFreshnessMinutes { get; set; } = 30;

    /// <summary>Máximo de puntos de la serie temporal (agrupación de buckets en SQL).</summary>
    public int SeriesMaxPoints { get; set; } = 60;

    /// <summary>Intervalo del refresco periódico del dashboard, en segundos (30-60 s según plan).</summary>
    public int RefreshIntervalSeconds { get; set; } = 30;
}