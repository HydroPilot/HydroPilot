namespace HydroPilotWeb.Services.Simulation;

/// <summary>Agua y nutrientes que consume el ciclo (litros): Solución A (macro) y Solución B (micro).</summary>
public sealed record NutrientUsage(decimal WaterLiters, decimal SolutionALiters, decimal SolutionBLiters)
{
    public decimal NutrientLiters => SolutionALiters + SolutionBLiters;
}

/// <summary>
/// Consumo de agua y nutrientes del ciclo, por planta viva y por día según la etapa. Los nutrientes
/// se disuelven en el agua que se repone para mantener el nivel, así que el gasto sigue al consumo
/// de agua y a cuántas plantas hay vivas cada día. Valores fijos por ahora (después pueden ir a
/// Ajustes &gt; Parámetros):
/// <list type="bullet">
/// <item>Crecimiento (antes de la ventana Baby Leaf, 0–250 GDD): 0,015 L de agua por planta y día; A 1,75 y B 0,65 ml/L.</item>
/// <item>Ventana Baby Leaf (250–450 GDD): 0,055 L; A 2,75 y B 1,1 ml/L.</item>
/// <item>Adultas (después de la ventana): 0,2 L; A 4,5 y B 1,75 ml/L.</item>
/// </list>
/// Las dosis son el valor medio de cada rango de la tabla del cultivo (A 1,5–2,0 / 2,5–3,0 / 4,0–5,0 ml/L; B 0,5–0,8 /
/// 1,0–1,2 / 1,5–2,0 ml/L, para CE 0,8–1,2 / 1,2–1,6 / 1,6–2,0).
/// Por planta individual (un hueco, desde 2026-10-26): una Baby Leaf de 30 g y una adulta de 280 g.
/// Lógica pura, testeable sin I/O.
/// </summary>
public static class NutrientConsumptionRules
{
    public const decimal WaterBeforeBabyLeaf = 0.015m;
    public const decimal WaterDuringBabyLeaf = 0.055m;
    public const decimal WaterConventional = 0.2m;
    public const decimal SolutionAMlPerLiterGrowth = 1.75m;
    public const decimal SolutionBMlPerLiterGrowth = 0.65m;
    public const decimal SolutionAMlPerLiterBabyLeaf = 2.75m;
    public const decimal SolutionBMlPerLiterBabyLeaf = 1.1m;
    public const decimal SolutionAMlPerLiterAdult = 4.5m;
    public const decimal SolutionBMlPerLiterAdult = 1.75m;

    /// <summary>
    /// Parte de Solución A en lo que se dosifica (A ÷ (A + B)), para el control de CE: en Adultas, la de adultas
    /// (4,5 : 1,75 → 72 %); en el denso, la de la ventana Baby Leaf (2,75 : 1,1 → 71 %; la de crecimiento es casi igual, 73 %).
    /// </summary>
    public static decimal SolutionAShare(bool adult) => adult
        ? Math.Round(SolutionAMlPerLiterAdult / (SolutionAMlPerLiterAdult + SolutionBMlPerLiterAdult), 4)
        : Math.Round(SolutionAMlPerLiterBabyLeaf / (SolutionAMlPerLiterBabyLeaf + SolutionBMlPerLiterBabyLeaf), 4);

    /// <summary>Plantas que consumen desde la siembra hasta el día anterior a <paramref name="Until"/> (corte, descarte o pérdida).</summary>
    public sealed record PlantSpan(int Plants, DateOnly Until);

    /// <summary>
    /// Suma día por día, de la siembra al último día con plantas vivas: plantas vivas × litros de agua
    /// de la etapa de ese día, y los nutrientes disueltos en esa agua. La etapa sale de las fechas de
    /// la línea de tiempo: hasta el día en que abre la ventana Baby Leaf, antes; hasta el día en que
    /// cierra, Baby Leaf; después, convencional.
    /// </summary>
    public static NutrientUsage Calculate(
        DateOnly sowingDate,
        DateOnly? babyLeafOpens,
        DateOnly? babyLeafCloses,
        IEnumerable<PlantSpan> spans)
    {
        var alive = spans.Where(s => s.Plants > 0 && s.Until > sowingDate).ToList();
        if (alive.Count == 0)
            return new NutrientUsage(0m, 0m, 0m);

        decimal water = 0m, solutionA = 0m, solutionB = 0m;
        var end = alive.Max(s => s.Until);
        for (var day = sowingDate; day < end; day = day.AddDays(1))
        {
            var plants = alive.Where(s => day < s.Until).Sum(s => s.Plants);
            var (liters, a, b) = Stage(day, babyLeafOpens, babyLeafCloses);
            water += plants * liters;
            solutionA += plants * liters * a / 1000m;
            solutionB += plants * liters * b / 1000m;
        }

        return new NutrientUsage(water, solutionA, solutionB);
    }

    private static (decimal Liters, decimal SolutionAMlPerLiter, decimal SolutionBMlPerLiter) Stage(DateOnly day, DateOnly? opens, DateOnly? closes) =>
        opens is null || day <= opens ? (WaterBeforeBabyLeaf, SolutionAMlPerLiterGrowth, SolutionBMlPerLiterGrowth)
        : closes is null || day <= closes ? (WaterDuringBabyLeaf, SolutionAMlPerLiterBabyLeaf, SolutionBMlPerLiterBabyLeaf)
        : (WaterConventional, SolutionAMlPerLiterAdult, SolutionBMlPerLiterAdult);
}
