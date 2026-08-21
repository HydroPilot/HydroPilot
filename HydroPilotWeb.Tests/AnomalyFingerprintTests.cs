using HydroPilotWeb.Services.Anomalies;

namespace HydroPilotWeb.Tests;

/// <summary>Pruebas del fingerprint de deduplicación (ANO-03): estable y discriminante.</summary>
public class AnomalyFingerprintTests
{
    private static readonly DateTime FirstObs = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SameInputs_SameFingerprint()
    {
        var a = AnomalyFingerprint.Compute(1, 7, "PH_FUERA_DE_BANDA_OPERATIVA", FirstObs);
        var b = AnomalyFingerprint.Compute(1, 7, "PH_FUERA_DE_BANDA_OPERATIVA", FirstObs);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ChangeLot_ChangesFingerprint()
    {
        Assert.NotEqual(
            AnomalyFingerprint.Compute(1, 7, "PH_FUERA_DE_BANDA_OPERATIVA", FirstObs),
            AnomalyFingerprint.Compute(2, 7, "PH_FUERA_DE_BANDA_OPERATIVA", FirstObs));
    }

    [Fact]
    public void ChangeSensor_ChangesFingerprint()
    {
        Assert.NotEqual(
            AnomalyFingerprint.Compute(1, 7, "PH_FUERA_DE_BANDA_OPERATIVA", FirstObs),
            AnomalyFingerprint.Compute(1, 8, "PH_FUERA_DE_BANDA_OPERATIVA", FirstObs));
    }

    [Fact]
    public void ChangeRule_ChangesFingerprint()
    {
        Assert.NotEqual(
            AnomalyFingerprint.Compute(1, 7, "PH_FUERA_DE_BANDA_OPERATIVA", FirstObs),
            AnomalyFingerprint.Compute(1, 7, "CE_FUERA_DE_BANDA_OPERATIVA", FirstObs));
    }

    [Fact]
    public void DifferentRunStart_ChangesFingerprint()
    {
        Assert.NotEqual(
            AnomalyFingerprint.Compute(1, 7, "PH_FUERA_DE_BANDA_OPERATIVA", FirstObs),
            AnomalyFingerprint.Compute(1, 7, "PH_FUERA_DE_BANDA_OPERATIVA", FirstObs.AddMinutes(5)));
    }
}