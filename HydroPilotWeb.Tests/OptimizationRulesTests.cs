using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Optimization;
using HydroPilotWeb.Services.Optimization;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Reglas de optimización del plan 16 (OPT-02/OPT-03/OPT-07/OPT-08/OPT-09):
/// evaluación de pH contra CropType y CE contra la etapa fenológica, evidencia de
/// lecturas (calidad + frescura + desbalance), comparativa económica Baby Leaf vs
/// convencional con umbral configurable, y agregado comercial con riesgo.
/// </summary>
public class OptimizationRulesTests
{
    private static readonly DateTime Now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------
    // OPT-02: pH vs CropType (límites absolutos, nunca regla porcentual)
    // ------------------------------------------------------------------

    private static readonly ChemicalRules.PhBounds PhBounds = new(5.5m, 6.0m, 6.5m);

    [Fact]
    public void Ph_Dentro_De_Rango_Es_Mantener()
    {
        var assessment = ChemicalRules.EvaluatePh(
            [Ph("6.0", minutesAgo: 5)], PhBounds, Now, maxAgeMinutes: 60, consistentCount: 2);

        Assert.True(assessment.Evaluable);
        Assert.Equal(OptimizationContractDirection.Maintain, assessment.Direction);
        Assert.Equal(6.0m, assessment.CurrentValue);
        Assert.Equal(6.0m, assessment.TargetValue);
    }

    [Fact]
    public void Ph_Bajo_El_Minimo_Es_Subir_Hacia_El_Objetivo()
    {
        var assessment = ChemicalRules.EvaluatePh(
            [Ph("5.2", minutesAgo: 5)], PhBounds, Now, maxAgeMinutes: 60, consistentCount: 2);

        Assert.Equal(OptimizationContractDirection.Raise, assessment.Direction);
        Assert.Equal(6.0m, assessment.TargetValue);
        Assert.Contains("subir", assessment.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ph_Alto_Es_Bajar()
    {
        var assessment = ChemicalRules.EvaluatePh(
            [Ph("6.9", minutesAgo: 5)], PhBounds, Now, maxAgeMinutes: 60, consistentCount: 2);

        Assert.Equal(OptimizationContractDirection.Lower, assessment.Direction);
        Assert.Equal(6.0m, assessment.TargetValue);
    }

    [Fact]
    public void Cultivo_Sin_Rango_De_Ph_No_Evaluable()
    {
        var assessment = ChemicalRules.EvaluatePh(
            [Ph("6.0", minutesAgo: 5)], new ChemicalRules.PhBounds(null, null, null),
            Now, maxAgeMinutes: 60, consistentCount: 2);

        Assert.False(assessment.Evaluable);
        Assert.Contains("rango de pH", assessment.Explanation);
    }

    // ------------------------------------------------------------------
    // OPT-07: EC vs etapa fenológica (EcObjective resuelto por GDD)
    // ------------------------------------------------------------------

    [Fact]
    public void Ec_Dentro_Del_Rango_De_La_Etapa_Es_Mantener()
    {
        var stage = new PhenologicalStage { Name = "Crecimiento vegetativo", EcMin = 1.2m, EcObjective = 1.5m, EcMax = 1.8m };
        var bounds = new ChemicalRules.EcBounds(stage.EcMin, stage.EcObjective, stage.EcMax);

        var assessment = ChemicalRules.EvaluateEc(
            [Ec("1.5", minutesAgo: 4)], bounds, stage.Name, Now, maxAgeMinutes: 60, consistentCount: 2);

        Assert.True(assessment.Evaluable);
        Assert.Equal(OptimizationContractDirection.Maintain, assessment.Direction);
        Assert.Equal(1.5m, assessment.TargetValue);
        Assert.Contains(stage.Name, assessment.TargetLabel!);
    }

    [Fact]
    public void Ec_Baja_Sube_Hacia_El_Objetivo_De_La_Etapa()
    {
        var bounds = new ChemicalRules.EcBounds(1.2m, 1.5m, 1.8m);

        var assessment = ChemicalRules.EvaluateEc(
            [Ec("1.0", minutesAgo: 4)], bounds, "Establecimiento", Now, maxAgeMinutes: 60, consistentCount: 2);

        Assert.Equal(OptimizationContractDirection.Raise, assessment.Direction);
        Assert.Equal(1.5m, assessment.TargetValue);
    }

    [Fact]
    public void Ec_Alta_Baja_Hacia_El_Objetivo()
    {
        var bounds = new ChemicalRules.EcBounds(1.2m, 1.5m, 1.8m);

        var assessment = ChemicalRules.EvaluateEc(
            [Ec("2.0", minutesAgo: 4)], bounds, "Crecimiento vegetativo", Now, maxAgeMinutes: 60, consistentCount: 2);

        Assert.Equal(OptimizationContractDirection.Lower, assessment.Direction);
        Assert.Equal(1.5m, assessment.TargetValue);
    }

    [Fact]
    public void Sin_Etapa_Fenologica_CE_No_Evaluable()
    {
        var assessment = ChemicalRules.EvaluateEc(
            [Ec("1.5", minutesAgo: 4)], bounds: null, stageName: null, Now, maxAgeMinutes: 60, consistentCount: 2);

        Assert.False(assessment.Evaluable);
        Assert.Contains("etapa fenológica", assessment.Explanation);
    }

    // ------------------------------------------------------------------
    // OPT-02: calidad y frescura de lecturas bloquean (nunca inventan)
    // ------------------------------------------------------------------

    [Fact]
    public void Lectura_Con_Calidad_Invalida_Bloquea()
    {
        var assessment = ChemicalRules.EvaluatePh(
            [new SnapshotReading("pH", TelemetryContract.QualityInvalid, 6.9m, Now.AddMinutes(-5))],
            PhBounds, Now, maxAgeMinutes: 60, consistentCount: 2);

        Assert.False(assessment.Evaluable);
        Assert.Contains("utilizables", assessment.Explanation);
    }

    [Fact]
    public void Lectura_Antigua_Fuera_De_Ventana_Bloquea()
    {
        // Ventana de frescura 60 min, lectura de hace 90 min.
        var assessment = ChemicalRules.EvaluatePh(
            [Ph("6.9", minutesAgo: 90)], PhBounds, Now, maxAgeMinutes: 60, consistentCount: 2);

        Assert.False(assessment.Evaluable);
        Assert.Contains("frescura", assessment.Explanation);
    }

    [Fact]
    public void Lectura_Stale_Dentro_De_La_Ventana_Se_Usa()
    {
        // STALE es operativamente usable (contrato IoT) y dentro de la ventana.
        var reading = new SnapshotReading("pH", TelemetryContract.QualityStale, 6.9m, Now.AddMinutes(-30));
        var assessment = ChemicalRules.EvaluatePh(
            [reading], PhBounds, Now, maxAgeMinutes: 60, consistentCount: 2);

        Assert.True(assessment.Evaluable);
        Assert.Equal(OptimizationContractDirection.Lower, assessment.Direction);
    }

    // ------------------------------------------------------------------
    // OPT-02: desbalance de 1 lectura vs dos lecturas consecutivas
    // ------------------------------------------------------------------

    [Fact]
    public void Una_Sola_Lectura_Confianza_Baja()
    {
        var assessment = ChemicalRules.EvaluatePh(
            [Ph("6.9", minutesAgo: 5)], PhBounds, Now, maxAgeMinutes: 60, consistentCount: 2);

        Assert.Equal(45, assessment.ConfidencePercent);
        Assert.Contains("Una sola lectura", assessment.Explanation);
    }

    [Fact]
    public void Dos_Lecturas_Consistentes_Confianza_Operativa()
    {
        var assessment = ChemicalRules.EvaluatePh(
            [Ph("6.9", minutesAgo: 8), Ph("6.8", minutesAgo: 3)],
            PhBounds, Now, maxAgeMinutes: 60, consistentCount: 2);

        Assert.Equal(80, assessment.ConfidencePercent);
        Assert.Equal(OptimizationContractDirection.Lower, assessment.Direction);
        Assert.DoesNotContain("Una sola lectura", assessment.Explanation);
    }

    [Fact]
    public void Dos_Lecturas_Contradictorias_Direccion_Verificar()
    {
        // Una dentro de rango y otra fuera: no se afirma dirección; se verifica.
        var assessment = ChemicalRules.EvaluatePh(
            [Ph("6.0", minutesAgo: 8), Ph("6.9", minutesAgo: 3)],
            PhBounds, Now, maxAgeMinutes: 60, consistentCount: 2);

        Assert.Equal(OptimizationContractDirection.Verify, assessment.Direction);
        Assert.Equal(50, assessment.ConfidencePercent);
    }

    // ------------------------------------------------------------------
    // OPT-03: comparativa económica con umbral configurable (10% default)
    // ------------------------------------------------------------------

    private static readonly List<SnapshotPriceCost> FullCatalog =
    [
        new("baby_leaf", OptimizationContractCostItem.PricePerKg, 100m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
        new("baby_leaf", OptimizationContractCostItem.SeedCostM2, 10m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
        new("baby_leaf", OptimizationContractCostItem.NutrientCostM2, 20m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
        new("baby_leaf", OptimizationContractCostItem.EnergyCostM2, 5m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
        new("baby_leaf", OptimizationContractCostItem.TransplantCostM2, 0m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
        new("conventional", OptimizationContractCostItem.PricePerKg, 60m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
        new("conventional", OptimizationContractCostItem.SeedCostM2, 5m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
        new("conventional", OptimizationContractCostItem.NutrientCostM2, 30m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
        new("conventional", OptimizationContractCostItem.EnergyCostM2, 10m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
        new("conventional", OptimizationContractCostItem.TransplantCostM2, 15m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
    ];

    [Fact]
    public void Comparacion_Calculable_Con_Precios_De_Ambos_Destinos()
    {
        var comparison = EconomicRules.Compare(yieldKgM2: 1m, FullCatalog, thresholdPercent: 10m, Now);

        Assert.True(comparison.Calculable);
        Assert.NotNull(comparison.BabyLeaf);
        Assert.NotNull(comparison.Conventional);
        // Baby Leaf: ingresos 100 - costos 35 = 65 → margen 65/35 = 185,7%
        Assert.Equal(100m, comparison.BabyLeaf!.RevenueM2);
        Assert.Equal(35m, comparison.BabyLeaf!.TotalCostM2);
        Assert.Equal(65m, comparison.BabyLeaf!.ResultM2);
    }

    [Fact]
    public void Sin_Precio_Baby_Leaf_Comparacion_No_Calculable_Con_Motivo()
    {
        var catalog = FullCatalog.Where(c => !(c.Destination == "baby_leaf" && c.Item == OptimizationContractCostItem.PricePerKg)).ToList();
        var comparison = EconomicRules.Compare(yieldKgM2: 1m, catalog, thresholdPercent: 10m, Now);

        Assert.False(comparison.Calculable);
        Assert.Contains(OptimizationContract.NotCalculableReason, comparison.NotCalculableReason!);
        Assert.Contains("Baby Leaf", comparison.NotCalculableReason!);
    }

    [Fact]
    public void Diferencia_Menor_Al_Umbral_No_Recomienda_Alternativa()
    {
        // Baby Leaf precio 100, convencional 98 con mismos costos → margen muy cercano.
        var catalog = new List<SnapshotPriceCost>
        {
            new("baby_leaf", OptimizationContractCostItem.PricePerKg, 100m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
            new("baby_leaf", OptimizationContractCostItem.SeedCostM2, 10m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
            new("baby_leaf", OptimizationContractCostItem.NutrientCostM2, 20m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
            new("baby_leaf", OptimizationContractCostItem.EnergyCostM2, 5m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
            new("baby_leaf", OptimizationContractCostItem.TransplantCostM2, 0m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
            new("conventional", OptimizationContractCostItem.PricePerKg, 98m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
            new("conventional", OptimizationContractCostItem.SeedCostM2, 10m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
            new("conventional", OptimizationContractCostItem.NutrientCostM2, 20m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
            new("conventional", OptimizationContractCostItem.EnergyCostM2, 5m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
            new("conventional", OptimizationContractCostItem.TransplantCostM2, 0m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
        };
        var comparison = EconomicRules.Compare(yieldKgM2: 1m, catalog, thresholdPercent: 10m, Now);

        Assert.True(comparison.Calculable);
        Assert.False(comparison.ExceedsThreshold);
        Assert.Null(comparison.RecommendedDestination);
        // 185,7% vs 180% → diferencia 5,7 pts < 10.
        Assert.True(comparison.ProfitabilityDifferencePct < 10m);
    }

    [Fact]
    public void Diferencia_Igual_Al_Umbral_No_Recomienda_Alternativa()
    {
        // Diferencia EXACTA de 10 pts de margen: la regla exige superar (>) el
        // umbral, por lo que igual no recomienda.
        // Baby Leaf: costo 50, precio 100 → margen 100%. Convencional: costo 25,
        // precio 47,5 → margen 90%. Diff = 10 pts = umbral.
        var catalog = new List<SnapshotPriceCost>
        {
            new("baby_leaf", OptimizationContractCostItem.PricePerKg, 100m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
            new("baby_leaf", OptimizationContractCostItem.SeedCostM2, 50m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
            new("conventional", OptimizationContractCostItem.PricePerKg, 47.5m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
            new("conventional", OptimizationContractCostItem.SeedCostM2, 25m, "ARS", "manual", DateOnly.FromDateTime(Now), null),
        };
        var comparison = EconomicRules.Compare(yieldKgM2: 1m, catalog, thresholdPercent: 10m, Now);

        Assert.Equal(100m, comparison.BabyLeaf!.MarginPercent);
        Assert.Equal(90m, comparison.Conventional!.MarginPercent);
        Assert.Equal(10m, comparison.ProfitabilityDifferencePct);
        Assert.False(comparison.ExceedsThreshold);
        Assert.Null(comparison.RecommendedDestination);
    }

    [Fact]
    public void Diferencia_Mayor_Al_Umbral_Recomienda_El_Destino_Con_Mayor_Margen()
    {
        var comparison = EconomicRules.Compare(yieldKgM2: 1m, FullCatalog, thresholdPercent: 10m, Now);

        Assert.True(comparison.Calculable);
        Assert.True(comparison.ExceedsThreshold);
        Assert.Equal("baby_leaf", comparison.RecommendedDestination);
        Assert.Contains("umbral", comparison.Assumptions.Last());
    }

    [Fact]
    public void Umbral_Configurable_Cambia_La_Decision()
    {
        // Con umbral 200% la diferencia ya no supera.
        var comparison = EconomicRules.Compare(yieldKgM2: 1m, FullCatalog, thresholdPercent: 200m, Now);
        Assert.False(comparison.ExceedsThreshold);
    }

    [Fact]
    public void Costos_Faltantes_Producen_Escenario_Parcial_Con_Advertencia()
    {
        var catalog = FullCatalog
            .Where(c => !(c.Destination == "conventional" && c.Item == OptimizationContractCostItem.TransplantCostM2))
            .ToList();
        var comparison = EconomicRules.Compare(yieldKgM2: 1m, catalog, thresholdPercent: 10m, Now);

        Assert.True(comparison.Calculable);
        Assert.Contains("trasplante", comparison.Conventional!.Warnings[^1], StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // OPT-08/OPT-09: agregado comercial con riesgo
    // ------------------------------------------------------------------

    [Fact]
    public void Riesgo_Genera_Hallazgo_Pero_No_Descarta_El_Lote()
    {
        var result = CommercialRules.Evaluate(new CommercialRules.Input(
            ActivePlants: 10, AptaCount: 7, RiskCount: 2, AptaPercent: 70m,
            IsCommercialMixed: false, AptaTargetPercent: 70m,
            Gdd: 320m, WindowMin: 250m, WindowMax: 450m));

        Assert.True(result.HasFinding);
        Assert.Equal(OptimizationContractDirection.Verify, result.Direction);
        Assert.Equal(OptimizationContractPriority.High, result.Priority);
        Assert.Contains("No se descarta el lote", result.Explanation);
    }

    [Fact]
    public void Apta_Por_Debajo_Del_Objetivo_Es_Hallazgo_Medio()
    {
        var result = CommercialRules.Evaluate(new CommercialRules.Input(
            ActivePlants: 10, AptaCount: 5, RiskCount: 0, AptaPercent: 50m,
            IsCommercialMixed: false, AptaTargetPercent: 70m,
            Gdd: 320m, WindowMin: 250m, WindowMax: 450m));

        Assert.True(result.HasFinding);
        Assert.Equal(OptimizationContractPriority.Medium, result.Priority);
        Assert.Contains("no alcanza", result.Explanation);
    }

    [Fact]
    public void Objetivo_De_Aptas_Sin_Configurar_Es_Decision_Pendiente()
    {
        var result = CommercialRules.Evaluate(new CommercialRules.Input(
            ActivePlants: 10, AptaCount: 8, RiskCount: 0, AptaPercent: 80m,
            IsCommercialMixed: false, AptaTargetPercent: null,
            Gdd: 320m, WindowMin: 250m, WindowMax: 450m));

        Assert.True(result.HasFinding);
        Assert.Contains("sin configurar", string.Join(" ", result.Details), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Lote_En_Condiciones_Normales_No_Tiene_Hallazgo()
    {
        var result = CommercialRules.Evaluate(new CommercialRules.Input(
            ActivePlants: 10, AptaCount: 8, RiskCount: 0, AptaPercent: 80m,
            IsCommercialMixed: false, AptaTargetPercent: 70m,
            Gdd: 320m, WindowMin: 250m, WindowMax: 450m));

        Assert.False(result.HasFinding);
    }

    [Fact]
    public void Empate_De_Estados_Se_Informa_Sin_Afirmar_Predominancia()
    {
        var result = CommercialRules.Evaluate(new CommercialRules.Input(
            ActivePlants: 10, AptaCount: 5, RiskCount: 0, AptaPercent: 50m,
            IsCommercialMixed: true, AptaTargetPercent: 50m,
            Gdd: 320m, WindowMin: 250m, WindowMax: 450m));

        Assert.Contains("Mixto", string.Join(" ", result.Details));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static SnapshotReading Ph(string value, int minutesAgo) =>
        new("pH", TelemetryContract.QualityValid, decimal.Parse(value), Now.AddMinutes(-minutesAgo));

    private static SnapshotReading Ec(string value, int minutesAgo) =>
        new("CE", TelemetryContract.QualityValid, decimal.Parse(value), Now.AddMinutes(-minutesAgo));
}