namespace HydroPilotWeb.Services.Simulation;

/// <summary>Agua y nutrientes que consume el ciclo (litros).</summary>
public sealed record NutrientUsage(decimal WaterLiters, decimal NutrientLiters);

/// <summary>
/// Consumo de agua y nutrientes del ciclo, por planta viva y por día según la etapa. Los nutrientes
/// se disuelven en el agua que se repone para mantener el nivel, así que el gasto sigue al consumo
/// de agua y a cuántas plantas hay vivas cada día. Valores fijos por ahora (después pueden ir a
/// Ajustes &gt; Parámetros):
/// <list type="bullet">
/// <item>Antes de la ventana Baby Leaf: 0,04 L de agua por planta y día, con 5 ml de nutrientes por litro.</item>
/// <item>Ventana Baby Leaf: 0,075 L por planta y día, con 5 ml/L.</item>
/// <item>Después (la adulta): 0,25 L por planta y día, con 10 ml/L.</item>
/// </list>
/// Por planta individual (un hueco, desde 2026-10-26): una Baby Leaf de 30 g y una adulta de 280 g.
/// Lógica pura, testeable sin I/O.
/// </summary>
public static class NutrientConsumptionRules
{
    public const decimal WaterBeforeBabyLeaf = 0.04m;
    public const decimal WaterDuringBabyLeaf = 0.075m;
    public const decimal WaterConventional = 0.25m;
    public const decimal NutrientMlPerLiterUntilBabyLeafEnd = 5m;
    public const decimal NutrientMlPerLiterConventional = 10m;

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
            return new NutrientUsage(0m, 0m);

        decimal water = 0m, nutrients = 0m;
        var end = alive.Max(s => s.Until);
        for (var day = sowingDate; day < end; day = day.AddDays(1))
        {
            var plants = alive.Where(s => day < s.Until).Sum(s => s.Plants);
            var (liters, mlPerLiter) = Stage(day, babyLeafOpens, babyLeafCloses);
            water += plants * liters;
            nutrients += plants * liters * mlPerLiter / 1000m;
        }

        return new NutrientUsage(water, nutrients);
    }

    private static (decimal Liters, decimal MlPerLiter) Stage(DateOnly day, DateOnly? opens, DateOnly? closes) =>
        opens is null || day <= opens ? (WaterBeforeBabyLeaf, NutrientMlPerLiterUntilBabyLeafEnd)
        : closes is null || day <= closes ? (WaterDuringBabyLeaf, NutrientMlPerLiterUntilBabyLeafEnd)
        : (WaterConventional, NutrientMlPerLiterConventional);
}
