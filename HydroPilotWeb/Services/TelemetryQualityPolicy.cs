using HydroPilotWeb.Models;
using System.Linq.Expressions;

namespace HydroPilotWeb.Services;

/// <summary>
/// Política de calidad compartida del contrato de telemetría.
/// Es el ÚNICO lugar donde se define qué lecturas son utilizables operativamente
/// (GDD, anomalías, comparaciones de dashboard). Forecasting, dashboard, anomalías
/// y reportes deben usar <see cref="IsOperationallyUsable"/> y no repetir filtros.
/// </summary>
public static class TelemetryQualityPolicy
{
    /// <summary>
    /// Lecturas válidas para cálculos operativos. STALE se incluye: es un valor real
    /// (retransmisión o histórico) y puede alimentar promedios/GDD, aunque la frescura
    /// debe tratarse aparte según la ventana del consumo (ej: anomalías en tiempo real).
    /// INVALID, FUTURE, NO_DATA y SENSOR_ERROR quedan excluidos de GDD y anomalías.
    /// </summary>
    public static bool IsOperationallyUsable(string quality) =>
        quality is TelemetryContract.QualityValid
            or TelemetryContract.QualitySuspect
            or TelemetryContract.QualityStale;

    /// <summary>
    /// MISMA regla que <see cref="IsOperationallyUsable"/> pero como expression tree,
    /// para poder usarla dentro de consultas EF Core (que no traduce el método estático).
    /// Es el filtro común que deben usar las consultas de dashboard/GDD/anomalías:
    /// no se repite la lista de estados en cada pantalla.
    /// </summary>
    public static readonly Expression<Func<SensorReading, bool>> OperationallyUsableReading =
        r => r.Quality == TelemetryContract.QualityValid
          || r.Quality == TelemetryContract.QualitySuspect
          || r.Quality == TelemetryContract.QualityStale;

    /// <summary>Lecturas frescas y sin sospecha (para ventanas en tiempo real).</summary>
    public static bool IsFresh(string quality) =>
        quality is TelemetryContract.QualityValid or TelemetryContract.QualitySuspect;

    /// <summary>Calidades que indican que la lectura no debe usarse como medición.</summary>
    public static bool IsUsableQuality(string quality) =>
        IsOperationallyUsable(quality) || quality == TelemetryContract.QualityInvalid;
}