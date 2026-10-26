using HydroPilotWeb.Models;

namespace HydroPilotWeb.Services.Anomalies;

/// <summary>Lectura de pH o CE del lote (solo las operativamente usables).</summary>
public sealed record SolutionReading(string SensorType, string Quality, decimal Value, DateTime ObservedAtUtc);

/// <summary>
/// Evidencia de lecturas de una magnitud (pH/CE): frescura, cantidad de lecturas usables y
/// consistencia entre las últimas. Firme = lectura fresca y las últimas
/// <c>consistentCount</c> lecturas piden lo mismo (subir, bajar o nada). Una sola lectura o lecturas
/// que se contradicen no son firmes. Lógica pura, testeable sin I/O.
/// </summary>
public static class ReadingEvidence
{
    /// <summary>Resultado de evaluar la evidencia de una magnitud.</summary>
    public sealed record Result(
        bool HasFreshReading,
        decimal? LatestValue,
        DateTime? LatestObservedAtUtc,
        int UsableCount,
        bool IsSingle,
        bool IsConsistent)
    {
        /// <summary>Fresca y con las últimas lecturas de acuerdo: se puede actuar.</summary>
        public bool IsFirm => HasFreshReading && !IsSingle && IsConsistent;
    }

    /// <summary>
    /// Evalúa las lecturas de una magnitud. <paramref name="directionOf"/> traduce cada lectura a su
    /// dirección (RAISE/LOWER/MAINTAIN) para comparar las consecutivas.
    /// </summary>
    public static Result Evaluate(
        IReadOnlyList<SolutionReading> readings,
        string sensorType,
        Func<decimal, string> directionOf,
        DateTime nowUtc,
        int maxAgeMinutes,
        int consistentCount)
    {
        var usable = readings
            .Where(r => r.SensorType.Equals(sensorType, StringComparison.OrdinalIgnoreCase)
                        && TelemetryQualityPolicy.IsOperationallyUsable(r.Quality))
            .OrderByDescending(r => r.ObservedAtUtc)
            .ToList();

        if (usable.Count == 0)
            return new Result(false, null, null, 0, IsSingle: false, IsConsistent: false);

        var latest = usable[0];
        var fresh = (nowUtc - latest.ObservedAtUtc).TotalMinutes <= maxAgeMinutes;
        var directions = usable.Take(Math.Max(2, consistentCount)).Select(r => directionOf(r.Value)).ToList();
        var isSingle = directions.Count == 1;
        var isConsistent = !isSingle && directions.Distinct().Count() == 1;

        return new Result(fresh, latest.Value, latest.ObservedAtUtc, usable.Count, isSingle, isConsistent);
    }
}
