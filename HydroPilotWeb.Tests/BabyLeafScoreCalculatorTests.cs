using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Tests;

/// <summary>Semántica ponderada del BabyLeafScore (criterios del catálogo, LOT-02).</summary>
public class BabyLeafScoreCalculatorTests
{
    [Fact]
    public void Score_Ponderado_Suma_Pesos_De_Criterios_Evaluados()
    {
        var criteria = new List<BabyLeafScoreCalculator.CriterionInput>
        {
            new("Ventana GDD", "GDD", 20m, true, null, false),          // se auto-evalúa
            new("Morfología / tamaño", "MORFOLOGIA", 45m, true, 80m, true),
            new("GrowthRate", "CRECIMIENTO", 20m, false, 60m, true),
            new("Estado visual", "VISUAL", 15m, true, 100m, true)
        };

        var result = BabyLeafScoreCalculator.Calculate(criteria, lotGdd: 320m, gddWindowMin: 250m, gddWindowMax: 450m);

        // GDD en ventana → 100. Promedio ponderado = (100*20 + 80*45 + 60*20 + 100*15)/100 = 83
        Assert.Equal(83m, result.WeightedScore!.Value);
        Assert.True(result.AllMandatoryMet);
    }

    [Fact]
    public void Criterio_Obligatorio_Fallado_Impide_AllMandatoryMet()
    {
        var criteria = new List<BabyLeafScoreCalculator.CriterionInput>
        {
            new("Ventana GDD", "GDD", 20m, true, null, false),          // auto: 100, pasa
            new("Morfología / tamaño", "MORFOLOGIA", 45m, true, 40m, false), // falla
            new("GrowthRate", "CRECIMIENTO", 20m, false, 90m, true),
            new("Estado visual", "VISUAL", 15m, true, 100m, true)
        };

        var result = BabyLeafScoreCalculator.Calculate(criteria, lotGdd: 320m, gddWindowMin: 250m, gddWindowMax: 450m);

        Assert.False(result.AllMandatoryMet);
        Assert.NotNull(result.WeightedScore);
    }

    [Fact]
    public void Obligatorio_Sin_Dato_No_Se_Confirma()
    {
        var criteria = new List<BabyLeafScoreCalculator.CriterionInput>
        {
            new("Ventana GDD", "GDD", 20m, true, null, false),
            new("Morfología / tamaño", "MORFOLOGIA", 45m, true, null, false), // sin dato
            new("Estado visual", "VISUAL", 15m, true, 100m, true)
        };

        var result = BabyLeafScoreCalculator.Calculate(criteria, lotGdd: 320m, gddWindowMin: 250m, gddWindowMax: 450m);

        Assert.False(result.AllMandatoryMet);
    }

    [Fact]
    public void Sin_Criterios_Evaluados_Score_Null()
    {
        var criteria = new List<BabyLeafScoreCalculator.CriterionInput>
        {
            new("Morfología / tamaño", "MORFOLOGIA", 100m, true, null, false)
        };

        var result = BabyLeafScoreCalculator.Calculate(criteria, lotGdd: 320m, gddWindowMin: 250m, gddWindowMax: 450m);

        Assert.Null(result.WeightedScore);
        Assert.False(result.AllMandatoryMet);
    }

    [Theory]
    [InlineData(320, 100)] // dentro de la ventana 250-450
    [InlineData(200, 0)]   // fuera por debajo
    [InlineData(500, 0)]   // fuera por arriba
    public void Criterio_Gdd_Se_Auto_Evalua_Contra_Ventana(decimal lotGdd, decimal expectedScore)
    {
        var criteria = new List<BabyLeafScoreCalculator.CriterionInput>
        {
            new("Ventana GDD", "GDD", 100m, true, null, false)
        };

        var result = BabyLeafScoreCalculator.Calculate(criteria, lotGdd, gddWindowMin: 250m, gddWindowMax: 450m);

        Assert.Equal(expectedScore, result.WeightedScore!.Value);
        Assert.Equal(expectedScore == 100, result.AllMandatoryMet);
    }

    [Fact]
    public void Normalizacion_Sobre_Pesos_Evaluados_Cuando_Falta_Un_Criterio()
    {
        // Solo morfología (45 de 100) evaluada: el promedio ponderado renormaliza sobre 45.
        var criteria = new List<BabyLeafScoreCalculator.CriterionInput>
        {
            new("Morfología / tamaño", "MORFOLOGIA", 45m, true, 90m, true),
            new("GrowthRate", "CRECIMIENTO", 20m, false, null, false) // sin dato
        };

        var result = BabyLeafScoreCalculator.Calculate(criteria, lotGdd: 320m, gddWindowMin: 250m, gddWindowMax: 450m);

        Assert.Equal(90m, result.WeightedScore!.Value);
    }
}