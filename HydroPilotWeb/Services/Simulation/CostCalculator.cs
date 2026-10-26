namespace HydroPilotWeb.Services.Simulation;

/// <summary>
/// Gastos del sistema de una estrategia (SIM-05): solo lo que el sistema sabe calcular, con
/// fórmulas transparentes en cada componente:
///   semillas   = precioSemilla × plantas del lote (todas) o del escenario
///   nutrientes = precioLitro × litros de nutrientes del ciclo (<see cref="NutrientConsumptionRules"/>:
///                plantas vivas cada día × agua de la etapa × ml de nutrientes por litro)
///   trasplante = plantas trasplantadas a Adultas × costo por planta (costo de la hora ÷ plantas por hora)
///   total      = suma de componentes
///   costoPorKg = total / kg cosechados  (solo si total y kg &gt; 0)
/// Los demás costos (mano de obra, energía, etc.) los arma el contador, fuera del sistema; por eso
/// el margen es "sobre gastos del sistema". Si falta un precio el componente es "No calculable";
/// el total solo se informa si todos los componentes son calculables. Nunca se inventa rentabilidad.
/// La moneda es la de los precios por kg (Ajustes &gt; Parámetros).
/// </summary>
public static class CostCalculator
{
    public const string NotCalculable = "No calculable";

    public static SimulationCostBreakdown Calculate(
        SimulationCostsInput costs,
        int seeds,
        NutrientUsage? usage,
        int cycleDays,
        string currencyCode,
        int transplanted = 0,
        decimal? transplantCostPerPlant = null)
    {
        var warnings = new List<string>();
        var components = new List<SimulationCostComponent>();

        // Semillas: precio × plantas.
        components.Add(costs.PricePerSeed is { } seedPrice
            ? new SimulationCostComponent("Semillas", "precio por semilla × semillas", Math.Round(seedPrice * seeds, 2), null, "semilla")
            : new SimulationCostComponent("Semillas", "precio por semilla × semillas", null, "Falta el precio por semilla.", "semilla"));

        // Nutrientes: precio/L × litros de nutrientes que consume el ciclo.
        decimal? nutrientsAmount = null;
        string? nutrientsReason = null;
        if (costs.NutrientPricePerLiter is not { } nutrientPrice)
            nutrientsReason = "Falta el precio de nutrientes por litro (Ajustes > Parámetros).";
        else if (usage is null)
            nutrientsReason = "Sin fecha de corte: no se puede calcular el consumo del ciclo.";
        else
            nutrientsAmount = Math.Round(nutrientPrice * usage.NutrientLiters, 2);
        components.Add(new SimulationCostComponent(
            "Nutrientes", "litros de nutrientes del ciclo × precio por litro", nutrientsAmount, nutrientsReason, "L"));

        // Trasplante: la mano de obra de las que pasan a Adultas.
        if (transplanted > 0)
        {
            components.Add(transplantCostPerPlant is { } perPlant
                ? new SimulationCostComponent("Trasplante", "plantas trasplantadas × costo por planta", Math.Round(perPlant * transplanted, 2), null, "planta")
                : new SimulationCostComponent("Trasplante", "plantas trasplantadas × costo por planta", null, "Falta el costo del trasplante.", "planta"));
        }

        // Total: solo si todos los componentes son calculables (nada se inventa).
        var notCalculable = components.Where(c => c.Amount is null).Select(c => c.Name).ToList();
        decimal? total = null;
        string? totalReason = null;
        if (notCalculable.Count == 0)
        {
            total = Math.Round(components.Sum(c => c.Amount!.Value), 2);
        }
        else
        {
            totalReason = $"{NotCalculable}: faltan precios para: {string.Join(", ", notCalculable)}.";
            warnings.Add(totalReason);
        }

        return new SimulationCostBreakdown(currencyCode, components, total, totalReason, cycleDays, seeds, usage, warnings, transplanted);
    }

    /// <summary>Costo por kg: total / kg, solo si ambos son válidos (&gt; 0).</summary>
    public static decimal? CostPerKg(decimal? total, decimal? kg) =>
        total is { } t && t > 0 && kg is { } k && k > 0
            ? Math.Round(t / k, 2)
            : null;
}
