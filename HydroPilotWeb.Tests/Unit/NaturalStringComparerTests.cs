using HydroPilotWeb.Services;

namespace HydroPilotWeb.Tests.Unit;

/// <summary>Orden natural de nombres con números (circuitos, lugares de los sensores).</summary>
public class NaturalStringComparerTests
{
    [Fact]
    public void Ordena_Los_Numeros_Por_Valor()
    {
        string[] names = ["Circuito 10", "Circuito 2", "Adultas 5", "Circuito 1", "adultas 4", "Circuito 12"];

        Assert.Equal(
            ["adultas 4", "Adultas 5", "Circuito 1", "Circuito 2", "Circuito 10", "Circuito 12"],
            names.Order(NaturalStringComparer.Instance));
    }
}
