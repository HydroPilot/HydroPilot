using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Regla híbrida de cosecha del lote: ventana GDD + porcentaje configurable de
/// aptas. Sin porcentaje objetivo → IsReady null (decisión pendiente), nunca
/// constante oculta.
/// </summary>
public class HarvestReadinessRulesTests
{
    private static HarvestReadinessRules.Result Evaluate(decimal gdd, int apta, int total, decimal? target)
        => HarvestReadinessRules.Evaluate(new HarvestReadinessRules.Input(gdd, 250m, 450m, apta, total, target));

    [Fact]
    public void Gdd_En_Ventana_Y_Porcentaje_Suficiente_Es_Ready()
    {
        var result = Evaluate(gdd: 320m, apta: 7, total: 10, target: 70m);
        Assert.True(result.GddInWindow);
        Assert.Equal(70m, result.AptaPercent);
        Assert.True(result.IsReady);
        Assert.Null(result.PendingReason);
    }

    [Fact]
    public void Porcentaje_Por_Debajo_Del_Objetivo_No_Es_Ready()
    {
        var result = Evaluate(gdd: 320m, apta: 5, total: 10, target: 70m);
        Assert.False(result.IsReady);
    }

    [Fact]
    public void Fuera_De_Ventana_No_Es_Ready_Aun_Con_Porcentaje_Alto()
    {
        var result = Evaluate(gdd: 500m, apta: 9, total: 10, target: 70m);
        Assert.False(result.GddInWindow);
        Assert.False(result.IsReady);
    }

    [Fact]
    public void Sin_Objetivo_Configurado_Es_Decision_Pendiente_Y_Nunca_Constante_Oculta()
    {
        var result = Evaluate(gdd: 320m, apta: 8, total: 10, target: null);
        Assert.Null(result.IsReady);
        Assert.NotNull(result.PendingReason);
        Assert.Contains("pendiente", result.PendingReason!);
    }

    [Fact]
    public void Sin_Plantas_Activas_Porcentaje_Cero()
    {
        var result = Evaluate(gdd: 320m, apta: 0, total: 0, target: 50m);
        Assert.Equal(0m, result.AptaPercent);
        Assert.False(result.IsReady);
    }
}