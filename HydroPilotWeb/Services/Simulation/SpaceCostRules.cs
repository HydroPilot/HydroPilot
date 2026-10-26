namespace HydroPilotWeb.Services.Simulation;

/// <summary>
/// Lugar ocupado por las Adultas de una estrategia: cada una usa el lugar (separación²) y el tiempo de Equivalence Baby
/// Leafs; se le cobra el margen de las que no se plantaron (Equivalence − 1). Total null = no calculable (Reason).
/// </summary>
public sealed record SimulationSpaceCost(
    int Adults,
    decimal? Equivalence,
    decimal? BabyLeafMarginPerPlant,
    decimal? Total,
    string? NotCalculableReason);

/// <summary>
/// Cuánto lugar ocupa una Adulta comparada con una Baby Leaf (lógica pura): lugar × días en cada circuito.
/// <list type="bullet">
/// <item>Baby Leaf: separación Baby Leaf² × días de la siembra a su corte.</item>
/// <item>Adulta: separación Baby Leaf² × días hasta el trasplante + separación Adulta² × días del trasplante a su corte
/// (con el estrés).</item>
/// </list>
/// Con 6,5 y 22,5 cm, 30 días contra 12 + 25: 1.268 contra 13.163 cm²·día, ~10 Baby Leafs por Adulta.
/// </summary>
public static class SpaceCostRules
{
    public static decimal? Equivalence(decimal babyLeafSpacingCm, decimal adultSpacingCm, int babyLeafDays, int daysToTransplant, int adultDays)
    {
        if (babyLeafDays <= 0 || daysToTransplant < 0 || adultDays < 0 || babyLeafSpacingCm <= 0)
            return null;
        var babyLeaf = babyLeafSpacingCm * babyLeafSpacingCm;
        var adult = adultSpacingCm * adultSpacingCm;
        return Math.Round((babyLeaf * daysToTransplant + adult * adultDays) / (babyLeaf * babyLeafDays), 2);
    }

    /// <summary>Adultas × (equivalencia − 1) × margen de una Baby Leaf (nunca negativo).</summary>
    public static decimal Total(int adults, decimal equivalence, decimal babyLeafMarginPerPlant) =>
        Math.Round(adults * Math.Max(0m, equivalence - 1m) * Math.Max(0m, babyLeafMarginPerPlant), 2);
}
