using HydroPilotWeb.Models;

namespace HydroPilotWeb.Services.Anomalies;

/// <summary>Punto de telemetría mínima, ya usable operativamente (calidad VALID/SUSPECT/STALE) y ordenado.</summary>
public sealed record AnomalyReadingPoint(int SensorId, DateTime ObservedAtUtc, decimal Value);

public enum SegmentKind { Normal, OutOfBand }

/// <summary>
/// Segmento contiguo de lecturas del mismo estado (normal o fuera de banda) sobre
/// la secuencia ORDENADA por ObservedAtUtc de un (lote, sensor, regla).
/// "Consecutivo" = adyacente en la secuencia de lecturas usables; un hueco de datos
/// no aporta lecturas normales (la falta de datos nunca resuelve ni "normaliza" por sí sola).
/// </summary>
public sealed record AnomalySegment(SegmentKind Kind, DateTime FirstObservedAtUtc, DateTime LastObservedAtUtc, int Count, decimal FirstValue, decimal LastValue, decimal MinValue, decimal MaxValue);

/// <summary>
/// Evaluador de telemetría PURO y explicable (ANO-02): recibe lecturas válidas
/// ordenadas por sensor + banda operativa + umbrales, y devuelve segmentos
/// normal/fuera-de-banda sin acoplarse a la UI ni a notifications. La persistencia
/// del episodio (ANO-03) y el barrido (ANO-04) orquestan estos segmentos.
/// </summary>
public static class AnomalyEvaluator
{
    public static bool IsOutOfBand(decimal value, AnomalyBand? band) =>
        band is not null
        && ((band.OperationalMin.HasValue && value < band.OperationalMin.Value)
            || (band.OperationalMax.HasValue && value > band.OperationalMax.Value));

    /// <summary>
    /// Segmenta lecturas ya ordenadas (por ObservedAtUtc asc) en corridas consecutivas.
    /// Una corrida fuera de banda de largo &lt; consecutiveToOpen = "registro sin episodio
    /// crítico"; &gt;= consecutiveToOpen = episodio crítico (decisión del caller).
    /// </summary>
    public static IReadOnlyList<AnomalySegment> SegmentRuns(IReadOnlyList<AnomalyReadingPoint> ordered, AnomalyBand? band)
    {
        var segments = new List<AnomalySegment>();
        if (ordered.Count == 0)
            return segments;

        var kind = IsOutOfBand(ordered[0].Value, band) ? SegmentKind.OutOfBand : SegmentKind.Normal;
        var firstT = ordered[0].ObservedAtUtc;
        var lastT = firstT;
        var firstV = ordered[0].Value;
        var lastV = firstV;
        var minV = firstV;
        var maxV = firstV;
        var count = 1;

        void Close()
        {
            segments.Add(new AnomalySegment(kind, firstT, lastT, count, firstV, lastV, minV, maxV));
        }

        for (var i = 1; i < ordered.Count; i++)
        {
            var p = ordered[i];
            var pKind = IsOutOfBand(p.Value, band) ? SegmentKind.OutOfBand : SegmentKind.Normal;

            if (pKind == kind)
            {
                lastT = p.ObservedAtUtc;
                lastV = p.Value;
                count++;
                if (p.Value < minV) minV = p.Value;
                if (p.Value > maxV) maxV = p.Value;
                continue;
            }

            Close();
            kind = pKind;
            firstT = p.ObservedAtUtc;
            lastT = p.ObservedAtUtc;
            firstV = p.Value;
            lastV = p.Value;
            minV = p.Value;
            maxV = p.Value;
            count = 1;
        }

        Close();
        return segments;
    }

    /// <summary>
    /// Cantidad de lecturas normales consecutivas INMEDIATAMENTE posteriores a
    /// <paramref name="afterUtc"/> dentro de <paramref name="points"/> (ordenadas asc).
    /// Determina si un episodio se resuelve (>= recoveryCount) sin depender de bordes
    /// de ventana: un hueco de datos NO cuenta como lectura normal.
    /// </summary>
    public static int CountConsecutiveNormalsAfter(IReadOnlyList<AnomalyReadingPoint> points, DateTime afterUtc, AnomalyBand? band)
    {
        var count = 0;
        foreach (var p in points)
        {
            if (p.ObservedAtUtc <= afterUtc)
                continue;
            if (IsOutOfBand(p.Value, band))
                break; // vuelve a salir de banda: no hay recuperación
            count++;
        }
        return count;
    }
}