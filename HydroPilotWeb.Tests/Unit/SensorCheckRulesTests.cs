using HydroPilotWeb.Services.Sensors;

namespace HydroPilotWeb.Tests.Unit;

/// <summary>Infraestructura &gt; Sensores: el filtro de chequeos (vencidos, vencen en 7 días, al día, sin programar).</summary>
public class SensorCheckRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 28);

    [Fact]
    public void Clasifica_El_Chequeo_Por_Su_Vencimiento()
    {
        Assert.Equal(SensorCheckState.Overdue, SensorCheckRules.State(Today.AddDays(-1), Today));
        Assert.Equal(SensorCheckState.Overdue, SensorCheckRules.State(Today, Today));          // vence hoy
        Assert.Equal(SensorCheckState.DueSoon, SensorCheckRules.State(Today.AddDays(1), Today));
        Assert.Equal(SensorCheckState.DueSoon, SensorCheckRules.State(Today.AddDays(7), Today));
        Assert.Equal(SensorCheckState.UpToDate, SensorCheckRules.State(Today.AddDays(8), Today));
        Assert.Equal(SensorCheckState.NotScheduled, SensorCheckRules.State(null, Today));       // tipo sin intervalo o inactivo
    }
}
