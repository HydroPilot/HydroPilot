using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Pruebas del árbol de decisión de la etapa comercial (plan 09, con el trasplante a Adultas).
/// El orden documentado: riesgo primero → trasplantada (Activa hasta llegar al tamaño de cosecha adulta en la etapa
/// adulta, después Adulta) → ruta Baby Leaf: ventana cerrada o tamaño pasado (descarte), ventana + score, debajo de ventana.
/// </summary>
public class PlantDecisionEngineTests
{
    private static readonly PlantDecisionEngine.PlantDecisionThresholds T = new(
        BabyLeafGddMin: 250m,
        BabyLeafGddMax: 450m,
        OvermaturityGdd: 750m,
        ScoreMinCandidate: 60m,
        ScoreMinReady: 80m,
        MaxSizeFactor: 1.3m,
        AdultReadySize: 2.0m);

    private static PlantDecisionEngine.PlantDecision Decide(decimal gdd, decimal? score = null, bool? mandatory = null, bool risk = false,
        string? riskReason = null, bool adult = false, decimal? size = null)
        => PlantDecisionEngine.Evaluate(
            new PlantDecisionEngine.PlantEvaluationInput(gdd, score, mandatory, risk, riskReason, adult, size), T);

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
        Assert.Equal(CommercialStageNames.Activa, decision.CommercialStageName);
    }

    [Fact]
    public void VentanaBabyLeaf_Sin_Evaluacion_No_Inventa_Resultado()
    {
        var decision = Decide(gdd: 300m, score: null);
        Assert.Equal(CommercialStageNames.Activa, decision.CommercialStageName);
        Assert.Contains("Sin evaluación", decision.Reason!);
    }

    [Fact]
    public void Trasplantada_Queda_Activa_Hasta_La_Etapa_Adulta()
    {
        // Recién trasplantada, en la ventana Baby Leaf: aunque sea grande, todavía no es adulta.
        var decision = Decide(gdd: 320m, score: 95m, mandatory: true, adult: true, size: 2.5m);
        Assert.Equal(CommercialStageNames.Activa, decision.CommercialStageName);
        Assert.False(decision.Discard);
    }

    [Theory]
    [InlineData(1.6, CommercialStageNames.Activa)]                 // posible adulta: todavía no llega
    [InlineData(2.0, CommercialStageNames.CosechaConvencional)]   // llegó al tamaño de cosecha: apta
    [InlineData(2.4, CommercialStageNames.CosechaConvencional)]
    public void En_La_Etapa_Adulta_Es_Adulta_La_Que_Llego_Al_Tamano_De_Cosecha(double size, string expected)
    {
        var decision = Decide(gdd: 560m, adult: true, size: (decimal)size);
        Assert.Equal(expected, decision.CommercialStageName);
        Assert.False(decision.Discard);
    }

    [Theory]
    [InlineData(600, CommercialStageNames.Activa)]
    [InlineData(650, CommercialStageNames.CosechaConvencional)]
    public void Sin_Medicion_De_Tamano_Es_Adulta_Desde_El_Corte_De_Adultas(int gdd, string expected)
    {
        Assert.Equal(expected, Decide(gdd: gdd, adult: true).CommercialStageName);
    }

    [Theory]
    [InlineData(620, CommercialStageNames.Activa)]               // todavía no llega al tamaño ni al corte
    [InlineData(650, CommercialStageNames.CosechaConvencional)] // en el corte de adultas es Adulta igual
    [InlineData(700, CommercialStageNames.CosechaConvencional)]
    public void En_El_Corte_De_Adultas_Es_Adulta_Aunque_No_Llegue_Al_Tamano(int gdd, string expected)
    {
        // 1,3 veces el ideal: lo máximo que mide una foto de la planta antes de que cierre la ventana Baby Leaf.
        Assert.Equal(expected, Decide(gdd: gdd, adult: true, size: 1.3m).CommercialStageName);
    }

    [Fact]
    public void Ruta_Baby_Leaf_Con_La_Ventana_Cerrada_Se_Descarta()
    {
        // En el denso no hay lugar para seguir como adulta.
        var decision = Decide(gdd: 550m, score: 90m, mandatory: true);
        Assert.Equal(CommercialStageNames.RiesgoFueraDeVentana, decision.CommercialStageName);
        Assert.True(decision.Discard);
    }

    [Fact]
    public void Se_Paso_Del_Tamano_Maximo_Deja_De_Ser_Baby_Leaf()
    {
        var passed = Decide(gdd: 340m, score: 95m, mandatory: true, size: 1.4m);
        Assert.Equal(CommercialStageNames.RiesgoFueraDeVentana, passed.CommercialStageName);
        Assert.True(passed.Discard);

        var ideal = Decide(gdd: 340m, score: 95m, mandatory: true, size: 1.2m);
        Assert.Equal(CommercialStageNames.BabyLeafApta, ideal.CommercialStageName);
    }

    [Theory]
    [InlineData(249)]
    [InlineData(100)]
    [InlineData(0)]
    public void Gdd_Debajo_De_Ventana_Es_En_Desarrollo(decimal gdd)
    {
        var decision = Decide(gdd);
        Assert.Equal(CommercialStageNames.Activa, decision.CommercialStageName);
    }

    [Fact]
    public void Limite_450_Cierra_La_Ventana_Baby_Leaf()
    {
        // [250,450) es Baby Leaf; en 450 la que sigue en el denso se descarta.
        var bl = Decide(gdd: 449m, score: 90m, mandatory: true);
        Assert.Equal(CommercialStageNames.BabyLeafApta, bl.CommercialStageName);

        var closed = Decide(gdd: 450m, score: 90m, mandatory: true);
        Assert.True(closed.Discard);
    }
}