namespace HydroPilotWeb.Models.Optimization;

/// <summary>Hacia dónde tiene que ir una magnitud de la solución respecto de su banda (SolutionRules).</summary>
public static class OptimizationContractDirection
{
    public const string Raise = "RAISE";
    public const string Lower = "LOWER";
    public const string Maintain = "MAINTAIN";
}

/// <summary>Ítems del catálogo de costos/precios (hoy solo se usa el precio por kg).</summary>
public static class OptimizationContractCostItem
{
    public const string PricePerKg = "price_per_kg";
    public const string SeedCostM2 = "seed_cost_m2";
    public const string NutrientCostM2 = "nutrient_cost_m2";
    public const string EnergyCostM2 = "energy_cost_m2";
    public const string TransplantCostM2 = "transplant_cost_m2";
}

/// <summary>Destinos comerciales del catálogo de precios.</summary>
public static class OptimizationContractDestination
{
    public const string BabyLeaf = "baby_leaf";
    public const string Conventional = "conventional";
}
