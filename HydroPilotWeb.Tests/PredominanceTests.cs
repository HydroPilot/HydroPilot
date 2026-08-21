using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Tests;

/// <summary>Predominancia por conteo con "Mixto" ante empate (decisión confirmada).</summary>
public class PredominanceTests
{
    [Fact]
    public void Unico_Maximo_Es_El_Predominante()
    {
        var counts = new Dictionary<string, int>
        {
            ["En desarrollo"] = 3,
            ["Baby Leaf apta"] = 7,
            ["Candidata Baby Leaf"] = 2
        };

        var (winner, isMixed) = Predominance.Resolve(counts);

        Assert.False(isMixed);
        Assert.Equal("Baby Leaf apta", winner);
    }

    [Fact]
    public void Empate_Muestra_Mixto()
    {
        var counts = new Dictionary<string, int>
        {
            ["En desarrollo"] = 4,
            ["Baby Leaf apta"] = 4,
            ["Riesgo / fuera de ventana"] = 1
        };

        var (winner, isMixed) = Predominance.Resolve(counts);

        Assert.True(isMixed);
        Assert.Null(winner);
    }

    [Fact]
    public void Empate_Triple_Tambien_Es_Mixto()
    {
        var counts = new Dictionary<string, int>
        {
            ["En desarrollo"] = 2,
            ["Baby Leaf apta"] = 2,
            ["Candidata Baby Leaf"] = 2
        };

        var (_, isMixed) = Predominance.Resolve(counts);
        Assert.True(isMixed);
    }

    [Fact]
    public void Sin_Conteos_No_Es_Mixto()
    {
        var (winner, isMixed) = Predominance.Resolve(new Dictionary<string, int>());
        Assert.False(isMixed);
        Assert.Null(winner);
    }

    [Fact]
    public void Conteos_Cero_No_Producen_Ganador()
    {
        var (winner, isMixed) = Predominance.Resolve(new Dictionary<string, int>
        {
            ["En desarrollo"] = 0,
            ["Baby Leaf apta"] = 0
        });

        Assert.False(isMixed);
        Assert.Null(winner);
    }
}