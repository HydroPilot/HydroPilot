using HydroPilotWeb.Services;
using Xunit;

namespace HydroPilotWeb.Tests.Unit;

public class NodeConnectionEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Sin_telemetria_aceptada_es_NEVER_CONNECTED() =>
        Assert.Equal("NEVER_CONNECTED", NodeConnectionEvaluator.Evaluate(null, Now, 300, 2));

    [Theory]
    [InlineData(0, "ONLINE")]       // recién aceptada
    [InlineData(299, "ONLINE")]     // dentro del intervalo esperado
    [InlineData(301, "DEGRADED")]   // pasado el intervalo, dentro del factor 2
    [InlineData(600, "DEGRADED")]
    [InlineData(601, "OFFLINE")]    // más allá del factor de degradación
    [InlineData(7200, "OFFLINE")]
    public void Estados_segun_edad_de_la_ultima_aceptacion(int ageSeconds, string expected)
    {
        var lastAccepted = Now.AddSeconds(-ageSeconds);
        Assert.Equal(expected, NodeConnectionEvaluator.Evaluate(lastAccepted, Now, 300, 2));
    }

    [Fact]
    public void Estado_administrativo_no_equivale_a_online()
    {
        // Un nodo "ACTIVO" pero que nunca aceptó telemetría NO es ONLINE.
        Assert.Equal("NEVER_CONNECTED", NodeConnectionEvaluator.Evaluate(null, Now, 300, 2));
    }
}