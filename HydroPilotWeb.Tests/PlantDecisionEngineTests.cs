using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Pruebas del árbol de decisión de la etapa comercial (plan 09).
/// El orden documentado: riesgo primero → ventana Baby Leaf + score →
/// ventana convencional → debajo de ventana.
/// </summary>
public class PlantDecisionEngineTests
{
    private static readonly PlantDecisionEngine.PlantDecisionThresholds T = new(
        BabyLeafGddMin: 250m,
        BabyLeafGddMax: 450m,
        OvermaturityGdd: 750m,
        ScoreMinCandidate: 60m,
        ScoreMinReady: 80m);

    private static PlantDecisionEngine.PlantDecision Decide(decimal gdd, decimal? score = null, bool? mandatory = null, bool? maturity = null, bool risk = false, string? riskReason = null)
        => PlantDecisionEngine.Evaluate(
            new PlantDecisionEngine.PlantEvaluationInput(gdd, score, mandatory, maturity, risk, riskReason), T);

    [Fact]
    public void Riesgo_Tiene_Prioridad_Sobre_Score_Y_Ventana()
    {
        // Score de apta y GDD en ventana Baby Leaf, pero hay riesgo declarado.
        var decision = Decide(gdd: 320m, score: 95m, mandatory: true, risk: true, riskReason: "bolting");
        Assert.Equal(CommercialStageNames.RiesgoFueraDeVentana, decision.CommercialStageName);
        Assert.Equal("bolting", decision.Reason);
    }

    [Fact]
    public void Gdd_Sobre_Sobremadurez_Es_Riesgo_Aun_Sin_Riesgo_Declarado()
    {
        var decision = Decide(gdd: 800m, score: 90m, mandatory: true);
        Assert.Equal(CommercialStageNames.RiesgoFueraDeVentana, decision.CommercialStageName);
    }

    [Fact]
    public void VentanaBabyLeaf_Score_Alto_Y_Obligatorios_Ok_Es_Apta()
    {
        var decision = Decide(gdd: 320m, score: 88m, mandatory: true);
        Assert.Equal(CommercialStageNames.BabyLeafApta, decision.CommercialStageName);
    }

    [Fact]
    public void VentanaBabyLeaf_Score_Alto_Sin_Obligatorios_Confirmados_Es_Candidata()
    {
        // Caso documentado fuera del árbol literal (score de apta sin confirmar obligatorios):
        // el plan no lo cubre; se resuelve como candidata (aún no aprobada).
        var decision = Decide(gdd: 320m, score: 90m, mandatory: null);
        Assert.Equal(CommercialStageNames.CandidataBabyLeaf, decision.CommercialStageName);
    }

    [Theory]
    [InlineData(78)]
    [InlineData(60)]
    [InlineData(61)]
    public void VentanaBabyLeaf_Score_En_Banda_Candidata(decimal score)
    {
        var decision = Decide(gdd: 300m, score: score, mandatory: true);
        Assert.Equal(CommercialStageNames.CandidataBabyLeaf, decision.CommercialStageName);
    }

    [Theory]
    [InlineData(59)]
    [InlineData(10)]
    [InlineData(0)]
    public void VentanaBabyLeaf_Score_Bajo_Es_En_Desarrollo(decimal score)
    {
        var decision = Decide(gdd: 300m, score: score, mandatory: true);
        Assert.Equal(CommercialStageNames.EnDesarrollo, decision.CommercialStageName);
    }

    [Fact]
    public void VentanaBabyLeaf_Sin_Evaluacion_No_Inventa_Resultado()
    {
        var decision = Decide(gdd: 300m, score: null);
        Assert.Equal(CommercialStageNames.EnDesarrollo, decision.CommercialStageName);
        Assert.Contains("Sin evaluación", decision.Reason!);
    }

    [Fact]
    public void VentanaConvencional_Con_Madurez_Confirmada_Es_CosechaConvencional()
    {
        var decision = Decide(gdd: 550m, maturity: true);
        Assert.Equal(CommercialStageNames.CosechaConvencional, decision.CommercialStageName);
    }

    [Fact]
    public void VentanaConvencional_Sin_Madurez_Confirmada_Es_En_Desarrollo()
    {
        var decision = Decide(gdd: 550m, maturity: null);
        Assert.Equal(CommercialStageNames.EnDesarrollo, decision.CommercialStageName);
    }

    [Theory]
    [InlineData(249)]
    [InlineData(100)]
    [InlineData(0)]
    public void Gdd_Debajo_De_Ventana_Es_En_Desarrollo(decimal gdd)
    {
        var decision = Decide(gdd);
        Assert.Equal(CommercialStageNames.EnDesarrollo, decision.CommercialStageName);
    }

    [Fact]
    public void Limite_450_Pertenece_A_Ventana_Convencional_No_BabyLeaf()
    {
        // [250,450) es Baby Leaf; 450 cae en la ventana convencional.
        var bl = Decide(gdd: 449m, score: 90m, mandatory: true);
        Assert.Equal(CommercialStageNames.BabyLeafApta, bl.CommercialStageName);

        var conv = Decide(gdd: 450m, maturity: true);
        Assert.Equal(CommercialStageNames.CosechaConvencional, conv.CommercialStageName);
    }
}