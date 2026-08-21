using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Anomalies;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Pruebas del evaluador puro (ANO-02): segmentación por corridas consecutivas,
/// semántica de banda y recuperación. La apertura/promoción del episodio se prueba
/// a nivel de integración (AnomaliesSqlServerTests) porque depende de la persistencia.
/// </summary>
public class AnomalyEvaluatorTests
{
    private static readonly AnomalyBand PhBand = new(0m, 14m, 5.8m, 6.2m, 6.0m);

    private static AnomalyReadingPoint P(int sensorId, DateTime t, decimal v) => new(sensorId, t, v);

    private static DateTime T(int minutesAgo) => DateTime.UtcNow.AddMinutes(-minutesAgo);

    [Fact]
    public void IsOutOfBand_DetectsBellowAndAboveOperativeBand()
    {
        Assert.True(AnomalyEvaluator.IsOutOfBand(5.0m, PhBand));
        Assert.True(AnomalyEvaluator.IsOutOfBand(6.5m, PhBand));
        Assert.False(AnomalyEvaluator.IsOutOfBand(5.8m, PhBand));
        Assert.False(AnomalyEvaluator.IsOutOfBand(6.2m, PhBand));
        Assert.False(AnomalyEvaluator.IsOutOfBand(6.0m, PhBand));
    }

    [Fact]
    public void IsOutOfBand_WithoutBand_NeverFires()
    {
        // Banda null = regla pendiente (sin umbral aprobado): ningún valor es "fuera de banda".
        Assert.False(AnomalyEvaluator.IsOutOfBand(-50m, null));
        Assert.False(AnomalyEvaluator.IsOutOfBand(999m, null));
    }

    [Fact]
    public void SingleReading_SegmentIsOutOfBandCountOne()
    {
        var segments = AnomalyEvaluator.SegmentRuns([P(1, T(10), 3.0m)], PhBand);
        var seg = Assert.Single(segments);
        Assert.Equal(SegmentKind.OutOfBand, seg.Kind);
        Assert.Equal(1, seg.Count);
    }

    [Fact]
    public void TwoConsecutive_OneRunCountTwo()
    {
        var segments = AnomalyEvaluator.SegmentRuns(
            [P(1, T(10), 3.0m), P(1, T(9), 3.1m)], PhBand);
        var seg = Assert.Single(segments);
        Assert.Equal(SegmentKind.OutOfBand, seg.Kind);
        Assert.Equal(2, seg.Count);
    }

    [Fact]
    public void NormalReading_BreaksTheRun()
    {
        // Fuera de banda, normal, fuera de banda → DOS corridas separadas (fingerprints distintos).
        var segments = AnomalyEvaluator.SegmentRuns(
            [P(1, T(12), 3.0m), P(1, T(10), 6.0m), P(1, T(8), 3.2m)], PhBand);

        Assert.Equal(3, segments.Count);
        Assert.Equal(SegmentKind.OutOfBand, segments[0].Kind);
        Assert.Equal(1, segments[0].Count);
        Assert.Equal(SegmentKind.Normal, segments[1].Kind);
        Assert.Equal(SegmentKind.OutOfBand, segments[2].Kind);
    }

    [Fact]
    public void NormalReadings_Only_OneSegmentNormal()
    {
        var segments = AnomalyEvaluator.SegmentRuns(
            [P(1, T(10), 6.0m), P(1, T(9), 6.1m)], PhBand);
        var seg = Assert.Single(segments);
        Assert.Equal(SegmentKind.Normal, seg.Kind);
        Assert.Equal(2, seg.Count);
    }

    [Fact]
    public void Empty_NoSegments()
    {
        Assert.Empty(AnomalyEvaluator.SegmentRuns([], PhBand));
    }

    [Fact]
    public void SegmentTracksMinMaxFirstLastValues()
    {
        var segments = AnomalyEvaluator.SegmentRuns(
            [P(1, T(10), 3.0m), P(1, T(9), 5.0m)], PhBand);
        var seg = Assert.Single(segments);
        Assert.Equal(3.0m, seg.FirstValue);
        Assert.Equal(5.0m, seg.LastValue);
        Assert.Equal(3.0m, seg.MinValue);
        Assert.Equal(5.0m, seg.MaxValue);
    }

    [Fact]
    public void CountConsecutiveNormalsAfter_CountsOnlyTrueNormals()
    {
        var points = new List<AnomalyReadingPoint>
        {
            P(1, T(10), 3.0m),  // anómala: corta la cuenta si viene antes de las normales
            P(1, T(9), 6.0m),
            P(1, T(8), 6.1m),
        };

        // Después de la anómala de hace 10 min: 2 normales consecutivas.
        Assert.Equal(2, AnomalyEvaluator.CountConsecutiveNormalsAfter(points, T(10), PhBand));

        // Después de hace 15 min (antes de la anómala): la primera lectura es anómala → 0.
        Assert.Equal(0, AnomalyEvaluator.CountConsecutiveNormalsAfter(points, T(15), PhBand));
    }

    [Fact]
    public void CountConsecutiveNormalsAfter_StopsAtNewOutOfBand()
    {
        var points = new List<AnomalyReadingPoint>
        {
            P(1, T(9), 6.0m),  // normal
            P(1, T(8), 5.5m),  // vuelve a salir de banda
            P(1, T(7), 6.1m),
        };

        Assert.Equal(1, AnomalyEvaluator.CountConsecutiveNormalsAfter(points, T(10), PhBand));
    }

    [Fact]
    public void RecoveryRequires_ActualNormalReadings_GapsDoNotCount()
    {
        // Solo hay un hueco (sin lecturas) después de la anómala: NO es normalidad.
        var points = new List<AnomalyReadingPoint> { P(1, T(10), 3.0m) };
        Assert.Equal(0, AnomalyEvaluator.CountConsecutiveNormalsAfter(points, T(10), PhBand));
    }
}