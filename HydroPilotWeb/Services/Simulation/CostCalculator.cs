namespace HydroPilotWeb.Services.Simulation;

/// <summary>
/// Cálculo de costos del escenario (SIM-05). Fórmulas transparentes y expuestas
/// en cada componente:
///   semillas   = precioSemilla × cantidadSemillas
///   nutrientes = precioLitro × litrosPorDia × diasProyectados
///   energía    = precioKwh × kwhPorDia × diasProyectados
///   total      = suma de componentes
///   costoPorKg = total / rendimiento  (solo si total y rendimiento &gt; 0)
/// Si falta un precio el componente es "No calculable"; el total solo se informa
/// si todos los componentes de costo son calculables. Nunca se inventa rentabilidad.
/// </summary>
public static class CostCalculator
{
    public const string NotCalculable = "No calculable";

    /// <summary>
    /// Días proyectados usados para presupuestar nutrientes/energía.
    /// Con fecha de cosecha estimada: días desde la referencia hasta el cierre.
    /// Sin fecha: el horizonte de la proyección, con advertencia explícita.
    /// </summary>
    public static int ResolveProjectedDays(
        DateOnly referenceDate,
        DateOnly? harvestDate,
        int projectionHorizonDays,
        IList<string> warnings)
    {
        if (harvestDate is { } harvest)
        {
            var days = (harvest.ToDateTime(TimeOnly.MinValue) - referenceDate.ToDateTime(TimeOnly.MinValue)).Days;
            if (days <= 0)
            {
                warnings.Add("Fecha de cosecha estimada al día de referencia: no se proyectan días de consumo (nutrientes/energía en 0).");
                return 0;
            }

            return days;
        }

        if (projectionHorizonDays > 0)
        {
            warnings.Add($"Sin fecha de cosecha estimada: los costos de nutrientes/energía presupuestan los {projectionHorizonDays} días del horizonte simulado.");
            return projectionHorizonDays;
        }

        warnings.Add("Sin fecha de cosecha ni horizonte: los costos de nutrientes/energía quedan sin calcular.");
        return 0;
    }

    public static SimulationCostBreakdown Calculate(
        SimulationCostsInput costs,
        int projectedDays)
    {
        var warnings = new List<string>();
        var components = new List<SimulationCostComponent>();

        // Semillas: precio × cantidad.
        decimal? seedsAmount = null;
        string? seedsReason = null;
        if (costs.Seeds.PricePerSeed is not { } price)
            seedsReason = "Falta el precio por semilla.";
        else if (costs.Seeds.SeedCount is not { } count)
            seedsReason = "Falta la cantidad de semillas.";
        else
            seedsAmount = Math.Round(price * count, 2);

        components.Add(new SimulationCostComponent(
            "Semillas",
            "precioSemilla × cantidadSemillas",
            seedsAmount,
            seedsReason,
            "unidad"));

        // Nutrientes: precio/L × L/día × días.
        decimal? nutrientsAmount = null;
        string? nutrientsReason = null;
        if (costs.Nutrients.PricePerLiter is not { } nutrientPrice)
            nutrientsReason = "Falta el precio de nutrientes por litro.";
        else if (costs.Nutrients.LitersPerDay <= 0)
            nutrientsReason = "Litros por día en 0: sin consumo de nutrientes a presupuestar.";
        else
            nutrientsAmount = Math.Round(nutrientPrice * costs.Nutrients.LitersPerDay * projectedDays, 2);

        components.Add(new SimulationCostComponent(
            "Nutrientes",
            "precioLitro × litrosPorDia × diasProyectados",
            nutrientsAmount,
            nutrientsReason,
            "L"));

        // Energía: precio/kWh × kWh/día × días.
        decimal? energyAmount = null;
        string? energyReason = null;
        if (costs.Energy.PricePerKwh is not { } energyPrice)
            energyReason = "Falta el precio de energía por kWh.";
        else if (costs.Energy.KwhPerDay <= 0)
            energyReason = "kWh por día en 0: sin consumo de energía a presupuestar.";
        else
            energyAmount = Math.Round(energyPrice * costs.Energy.KwhPerDay * projectedDays, 2);

        components.Add(new SimulationCostComponent(
            "Energía",
            "precioKwh × kwhPorDia × diasProyectados",
            energyAmount,
            energyReason,
            "kWh"));

        // Total: solo si todos los componentes son calculables (nada se inventa).
        var notCalculable = components.Where(c => c.Amount is null).Select(c => c.Name).ToList();
        decimal? total = null;
        string? totalReason = null;
        if (notCalculable.Count == 0)
        {
            total = components.Sum(c => c.Amount!.Value);
        }
        else
        {
            totalReason = $"{NotCalculable}: faltan precios o consumos para: {string.Join(", ", notCalculable)}.";
            warnings.Add(totalReason);
        }

        if (total is > 0 && projectedDays <= 0)
        {
            warnings.Add("Total calculado con 0 días proyectados: refleja solo el costo de semillas.");
        }

        return new SimulationCostBreakdown(
            costs.CurrencyCode,
            components,
            total is not null ? Math.Round(total.Value, 2) : null,
            totalReason,
            CostPerKg: null, // se setea por el llamador con el rendimiento del escenario
            projectedDays,
            warnings);
    }

    /// <summary>Costo por kg: total / rendimiento, solo si ambos son válidos (&gt; 0).</summary>
    public static decimal? CostPerKg(decimal? total, decimal? yieldKg) =>
        total is { } t && t > 0 && yieldKg is { } y && y > 0
            ? Math.Round(t / y, 2)
            : null;
}