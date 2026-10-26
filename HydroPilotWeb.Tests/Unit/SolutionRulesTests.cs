using HydroPilotWeb.Services.Anomalies;

namespace HydroPilotWeb.Tests.Unit;

/// <summary>Estado de pH/CE con lecturas firmes (lo que muestran las cards de Optimización y usa el control).</summary>
public class SolutionRulesTests
{
    private static SolutionReading Reading(decimal value, int minutesAgo) =>
        new("pH", "VALID", value, DateTime.UtcNow.AddMinutes(-minutesAgo));

    private static readonly SolutionBand PhBand = new(5.5m, 6.5m, 6.0m);

    [Fact]
    public void Solucion_Dos_Lecturas_Frescas_Que_Coinciden_Son_Firmes()
    {
        var status = SolutionRules.Evaluate("pH", "pH", [Reading(7.0m, 2), Reading(6.9m, 12)], null, PhBand, DateTime.UtcNow, 30);
        Assert.Equal(SolutionState.Lower, status.State);
        Assert.True(status.IsFirmDeviation);
    }

    [Fact]
    public void Solucion_Una_Lectura_O_Contradictorias_Piden_Confirmar()
    {
        Assert.Equal(SolutionState.Unconfirmed,
            SolutionRules.Evaluate("pH", "pH", [Reading(7.0m, 2)], null, PhBand, DateTime.UtcNow, 30).State);
        Assert.Equal(SolutionState.Unconfirmed,
            SolutionRules.Evaluate("pH", "pH", [Reading(7.0m, 2), Reading(6.0m, 12)], null, PhBand, DateTime.UtcNow, 30).State);
    }

    [Fact]
    public void Solucion_Lectura_Vieja_Y_Valor_Manual()
    {
        Assert.Equal(SolutionState.Stale,
            SolutionRules.Evaluate("pH", "pH", [Reading(7.0m, 90), Reading(7.1m, 100)], null, PhBand, DateTime.UtcNow, 30).State);

        var manual = SolutionRules.Evaluate("pH", "pH", [], 7.2m, PhBand, DateTime.UtcNow, 30);
        Assert.Equal(SolutionState.Manual, manual.State);
        Assert.Equal(7.2m, manual.Value);
    }
}
