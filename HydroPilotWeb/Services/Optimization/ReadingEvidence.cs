using HydroPilotWeb.Models;

namespace HydroPilotWeb.Services.Optimization;

/// <summary>
/// Evidencia de lecturas para una magnitud química (pH/CE): calidad operativa,
/// frescura (OPT-02: lecturas antiguas o inválidas bloquean), cantidad de
/// lecturas usables y consistencia entre lecturas consecutivas (desbalance de 1
/// lectura → confianza baja y "verificar"; 2+ consistentes → confianza
/// operativa). Lógica pura, testeable sin I/O.
/// </summary>
public static class ReadingEvidence
{
    /// <summary>Resultado de evaluar la evidencia de una magnitud.</summary>
    public sealed record Result(
        bool HasFreshReading,
        string? BlockReason,
        IReadOnlyList<ReadingPoint> UsableReadings,   // ordenadas de más reciente a más antigua
        decimal? LatestValue,
        int? LatestAgeMinutes,
        bool IsSingle,
        bool IsConsistent,
        IReadOnlyList<string> PerReadingDirections,   // dirección por lectura (índice de UsableReadings)
        int ConfidencePercent);

    /// <summary>
    /// Evalúa las lecturas de una magnitud. <paramref name="directionOf"/> traduce
    /// cada lectura a su dirección (RAISE/LOWER/MAINTAIN/VERIFY) para comparar
    /// consistencia entre lecturas consecutivas.
    /// </summary>
    public static Result Evaluate(
        IReadOnlyList<SnapshotReading> readings,
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
            .Select(r => new ReadingPoint(r.SensorType, r.Quality, r.Value, r.ObservedAtUtc, 0))
            .ToList();

        if (usable.Count == 0)
        {
            return new Result(
                HasFreshReading: false,
                BlockReason: $"Sin lecturas de {sensorType} utilizables (la lectura debe tener calidad operativa: válida, sospechosa o stale dentro de la ventana).",
                usable, null, null, IsSingle: false, IsConsistent: false, [], 0);
        }

        // Edad de la lectura más reciente.
        var latest = usable[0];
        var ageMinutes = (int)(nowUtc - latest.ObservedAtUtc).TotalMinutes;
        if (ageMinutes > maxAgeMinutes)
        {
            return new Result(
                HasFreshReading: false,
                BlockReason: $"La lectura de {sensorType} más reciente tiene {ageMinutes} min ({latest.ObservedAtUtc:u}): supera la ventana de frescura de {maxAgeMinutes} min. Dato viejo ⇒ no se recomienda hasta una lectura fresca.",
                usable, latest.Value, ageMinutes, usable.Count == 1, false, [], 0);
        }

        // Dirección por lectura (para consistencia).
        var directions = usable.Select(r => directionOf(r.Value)).ToList();
        var isSingle = directions.Count == 1;
        var window = directions.Take(consistentCount);
        var isConsistent = !isSingle && window.Distinct().Count() == 1;

        var confidence = isSingle
            ? 45                                   // desbalance: una sola lectura → verificar
            : isConsistent
                ? 80                               // dos o más consistentes → operativa
                : 50;                              // lecturas contradictorias → verificar discrepancia

        return new Result(
            HasFreshReading: true,
            null, usable, latest.Value, ageMinutes, isSingle, isConsistent, directions, confidence);
    }
}