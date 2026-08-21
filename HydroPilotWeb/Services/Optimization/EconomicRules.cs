using HydroPilotWeb.Models.Optimization;

namespace HydroPilotWeb.Services.Optimization;

/// <summary>
/// Reglas económicas Baby Leaf vs convencional (OPT-03/OPT-08): ingresos,
/// semillas, nutrientes, energía, trasplante y resultado por m² cuando el
/// catálogo tiene datos. Sin precios vigentes la comparación es "No calculable"
/// con el motivo (regla del plan 16). Solo se recomienda cambiar de destino si
/// la diferencia de rentabilidad SUPERA el umbral configurable (default 10%:
/// `>` estricto; igual no recomienda). Lógica pura, testeable sin I/O.
/// </summary>
public static class EconomicRules
{
    /// <summary>Escenario por destino con sus números por m².</summary>
    public static EconomicScenarioDto BuildScenario(
        string destination,
        decimal yieldKgM2,
        IReadOnlyList<SnapshotPriceCost> catalog,
        IList<string> warnings)
    {
        var items = catalog.Where(c => c.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase)).ToList();
        var price = FindItem(items, OptimizationContractCostItem.PricePerKg);

        if (price is null)
        {
            warnings.Add($"Sin precio vigente ('{OptimizationContractCostItem.PricePerKg}') para el destino '{destination}': escenario no calculable.");
            return new EconomicScenarioDto(
                destination, Calculable: false,
                $"Sin precio vigente para {DestinationLabel(destination)}.", yieldKgM2,
                null, null, null, null, null, null, null, null, null, []);
        }

        var seed = FindItem(items, OptimizationContractCostItem.SeedCostM2);
        var nutrient = FindItem(items, OptimizationContractCostItem.NutrientCostM2);
        var energy = FindItem(items, OptimizationContractCostItem.EnergyCostM2);
        var transplant = FindItem(items, OptimizationContractCostItem.TransplantCostM2);

        var scenarioWarnings = new List<string>();
        if (seed is null) scenarioWarnings.Add($"Sin dato de costo de semillas por m² para {DestinationLabel(destination)}.");
        if (nutrient is null) scenarioWarnings.Add($"Sin dato de costo de nutrientes por m² para {DestinationLabel(destination)}.");
        if (energy is null) scenarioWarnings.Add($"Sin dato de costo de energía por m² para {DestinationLabel(destination)}.");
        if (transplant is null) scenarioWarnings.Add($"Sin dato de costo de trasplante por m² para {DestinationLabel(destination)}.");

        var revenue = yieldKgM2 * price.Value;
        var knownCosts = new[] { seed, nutrient, energy, transplant }
            .Where(c => c is not null)
            .Select(c => c!.Value)
            .ToList();
        var totalCost = knownCosts.Count > 0 ? knownCosts.Sum() : (decimal?)null;

        decimal? result = totalCost is { } cost ? revenue - cost : null;
        decimal? margin = totalCost is { } tc and > 0m ? result / tc * 100m : null;
        if (totalCost is 0m) scenarioWarnings.Add("Costos totales en cero: margen no calculable (revisar catálogo).");

        return new EconomicScenarioDto(
            destination, Calculable: true, null, yieldKgM2,
            price.Value, seed?.Value, nutrient?.Value, energy?.Value, transplant?.Value,
            revenue, totalCost, result, margin, scenarioWarnings);
    }

    /// <summary>
    /// Comparativa: calculable solo si AMBOS destinos tienen precio vigente
    /// (si falta uno, la comparación es parcial/no calculable con motivo).
    /// ExceedsThreshold usa `>` estricto contra el umbral configurado.
    /// </summary>
    public static EconomicComparisonDto Compare(
        decimal yieldKgM2,
        IReadOnlyList<SnapshotPriceCost> catalog,
        decimal thresholdPercent,
        DateTime? nowUtc = null)
    {
        var warnings = new List<string>();
        var babyLeaf = BuildScenario(OptimizationContractDestination.BabyLeaf, yieldKgM2, catalog, warnings);
        var conventional = BuildScenario(OptimizationContractDestination.Conventional, yieldKgM2, catalog, warnings);

        var assumptions = new List<string>
        {
            $"Rendimiento base del forecast: {yieldKgM2:0.0#} kg/m² (misma base para ambos destinos).",
            $"Umbral de diferencia de rentabilidad: {thresholdPercent:0.#}% (configurable, 'Optimization:ProfitabilityThresholdPercent').",
            "Ingresos = rendimiento × precio vigente; costos por m² según catálogo. Sin ajuste por precio futuro ni descuentos.",
            "La comparación es informativa: no decide la cosecha ni envía órdenes.",
        };

        if (!babyLeaf.Calculable || !conventional.Calculable)
        {
            var missing = new List<string>();
            if (!babyLeaf.Calculable) missing.Add("Baby Leaf");
            if (!conventional.Calculable) missing.Add("convencional");
            var reason =
                $"{OptimizationContract.NotCalculableReason}: faltan precios vigentes de {string.Join(" y ", missing)} (catálogo de costos/precios).";
            return new EconomicComparisonDto(
                Calculable: false, reason, babyLeaf, conventional,
                null, thresholdPercent, null, false, assumptions.Concat(warnings).ToList());
        }

        var marginBaby = babyLeaf.MarginPercent ?? 0m;
        var marginConv = conventional.MarginPercent ?? 0m;
        var difference = Math.Abs(marginBaby - marginConv);
        var exceeds = difference > thresholdPercent;

        string? recommended = null;
        if (exceeds)
        {
            recommended = marginBaby > marginConv
                ? OptimizationContractDestination.BabyLeaf
                : marginConv > marginBaby
                    ? OptimizationContractDestination.Conventional
                    : null;
            assumptions.Add(recommended is not null
                ? $"Diferencia de rentabilidad {difference:0.#} pts > umbral {thresholdPercent:0.#}%: el escenario con mayor margen es '{DestinationLabel(recommended)}'."
                : $"Diferencia de rentabilidad {difference:0.#} pts > umbral {thresholdPercent:0.#}% con márgenes iguales: sin destino recomendado.");
        }
        else
        {
            assumptions.Add(
                $"Diferencia de rentabilidad {difference:0.#} pts ≤ umbral {thresholdPercent:0.#}%: no se recomienda cambiar de destino.");
        }

        return new EconomicComparisonDto(
            Calculable: true, null, babyLeaf, conventional,
            difference, thresholdPercent, recommended, exceeds, assumptions.Concat(warnings).ToList());
    }

    private static SnapshotPriceCost? FindItem(IReadOnlyList<SnapshotPriceCost> items, string item) =>
        items.FirstOrDefault(c => c.Item.Equals(item, StringComparison.OrdinalIgnoreCase));

    public static string DestinationLabel(string destination) =>
        destination.Equals(OptimizationContractDestination.BabyLeaf, StringComparison.OrdinalIgnoreCase)
            ? "Baby Leaf"
            : "convencional";
}