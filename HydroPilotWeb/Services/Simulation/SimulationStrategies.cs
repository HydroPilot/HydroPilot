using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Services.Simulation;

/// <summary>
/// Estrategias de cosecha del escenario, con la lógica de la vista Lotes (gana Lotes):
/// <list type="bullet">
/// <item>Corte Baby Leaf en su óptimo (300 GDD); si ya pasó, ahora; si la ventana (450) cerró, no se puede.</item>
/// <item>Corte convencional en su óptimo (650 GDD); si ya pasó, ahora (pasada la sobremadurez, 750, con aviso).</item>
/// <item>Plantas por destino con la previsión (<see cref="BabyLeafOutlook"/>): primero se descuentan
/// las pérdidas tempranas que faltan; de las vivas, como Baby Leaf como mucho las previstas aptas;
/// a las que siguen a convencional se les descuentan las pérdidas tardías.</item>
/// <item>Rendimiento = plantas × peso por planta × precio por kg (LotYieldRules, sin m²).</item>
/// <item>Gastos del sistema = semillas + nutrientes (según plantas vivas y etapa, <see cref="NutrientConsumptionRules"/>),
/// de la siembra al último corte; margen sobre gastos del sistema = ingresos − esos gastos.</item>
/// </list>
/// Estrategias: "Máximo Baby Leaf", "Meta Baby Leaf (X %)" (si hay meta) y "Todo adulta".
/// Lógica pura, testeable sin I/O.
/// </summary>
public static class SimulationStrategies
{
    public sealed record Input(
        DateOnly SowingDate,
        DateOnly Today,
        CycleTimeline Timeline,
        int Plants,
        decimal? BabyLeafGoalPercent,
        decimal? AptRate,               // previstas aptas a Baby Leaf (0..1) de las vivas en el óptimo; null = todas
        decimal RemainingEarlyLossRate, // pérdidas que faltan hasta cerrar la ventana Baby Leaf (0..1)
        decimal RemainingLateLossRate,  // pérdidas que faltan esperando al convencional (0..1)
        YieldReference Weights,
        YieldPrices Prices,
        SimulationCostsInput Costs,
        int Seeds,                                // plantas del lote (todas) o del escenario
        IReadOnlyList<DateOnly> ExitedPlantDates); // lote: el día que se cosechó o descartó cada planta que ya no está activa

    /// <summary>Reparto de plantas de una estrategia: Baby Leaf, convencional y pérdidas previstas (tempranas y tardías).</summary>
    public sealed record PlantAllocation(int BabyLeaf, int Conventional, int EarlyLost, int LateLost)
    {
        public int Lost => EarlyLost + LateLost;
    }

    public static IReadOnlyList<SimulationStrategyOutcome> Build(Input input)
    {
        var babyLeafCut = BabyLeafCut(input.Timeline, input.Today);
        var conventionalCut = ConventionalCut(input.Timeline, input.Today);

        var strategies = new List<SimulationStrategyOutcome>
        {
            Strategy(SimulationStrategyKind.MaxBabyLeaf, "Máximo Baby Leaf", input, babyLeafCut, conventionalCut),
        };
        if (input.BabyLeafGoalPercent is { } goal)
        {
            strategies.Add(Strategy(SimulationStrategyKind.BabyLeafGoal, $"Meta Baby Leaf ({goal:0.#} %)",
                input, babyLeafCut, conventionalCut));
        }
        strategies.Add(Strategy(SimulationStrategyKind.AllConventional, "Todo adulta", input, babyLeafCut, conventionalCut));
        return strategies;
    }

    /// <summary>
    /// Plantas de cada destino: vivas = plantas − pérdidas tempranas; Baby Leaf = las previstas aptas
    /// de las vivas (con meta, hasta la meta; todo convencional, ninguna); el resto sigue y pierde las
    /// tardías. Todo redondeado a plantas enteras.
    /// </summary>
    public static PlantAllocation Allocate(
        SimulationStrategyKind kind,
        int plants,
        decimal? goalPercent,
        decimal? aptRate,
        decimal earlyLossRate,
        decimal lateLossRate)
    {
        static int Round(decimal value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

        plants = Math.Max(0, plants);
        var earlyLost = Math.Min(plants, Round(plants * earlyLossRate));
        var alive = plants - earlyLost;
        var maxBabyLeaf = aptRate is { } rate ? Math.Min(alive, Round(alive * rate)) : alive;
        var babyLeaf = kind switch
        {
            SimulationStrategyKind.MaxBabyLeaf => maxBabyLeaf,
            SimulationStrategyKind.BabyLeafGoal => Math.Min(maxBabyLeaf, Round(alive * (goalPercent ?? 0m) / 100m)),
            _ => 0
        };
        var continuing = alive - babyLeaf;
        var lateLost = Math.Min(continuing, Round(continuing * lateLossRate));
        return new PlantAllocation(babyLeaf, continuing - lateLost, earlyLost, lateLost);
    }

    /// <summary>Corte Baby Leaf: el óptimo; ya pasado, ahora; con la ventana cerrada, no hay corte.</summary>
    public static SimulationCut BabyLeafCut(CycleTimeline timeline, DateOnly today)
    {
        if (timeline.AccumulatedGdd >= timeline.BabyLeafCloses.Gdd)
            return new SimulationCut(HarvestDestination.BabyLeaf, null, IsNow: false, IsExtrapolated: false, IsOverdue: true);
        if (timeline.AccumulatedGdd >= timeline.BabyLeafOptimum.Gdd)
            return new SimulationCut(HarvestDestination.BabyLeaf, today, IsNow: true, IsExtrapolated: false, IsOverdue: false);
        return new SimulationCut(HarvestDestination.BabyLeaf, timeline.BabyLeafOptimum.Date, IsNow: false,
            timeline.BabyLeafOptimum.IsExtrapolated, IsOverdue: false);
    }

    /// <summary>Corte convencional: el óptimo; ya pasado, ahora (pasada la sobremadurez, marcado como tal).</summary>
    public static SimulationCut ConventionalCut(CycleTimeline timeline, DateOnly today)
    {
        if (timeline.AccumulatedGdd >= timeline.ConventionalOptimum.Gdd)
            return new SimulationCut(HarvestDestination.Convencional, today, IsNow: true, IsExtrapolated: false,
                IsOverdue: timeline.AccumulatedGdd > timeline.Overmaturity.Gdd);
        return new SimulationCut(HarvestDestination.Convencional, timeline.ConventionalOptimum.Date, IsNow: false,
            timeline.ConventionalOptimum.IsExtrapolated, IsOverdue: false);
    }

    private static SimulationStrategyOutcome Strategy(
        SimulationStrategyKind kind,
        string name,
        Input input,
        SimulationCut babyLeafCut,
        SimulationCut conventionalCut)
    {
        var allocation = Allocate(kind, input.Plants, input.BabyLeafGoalPercent, input.AptRate,
            input.RemainingEarlyLossRate, input.RemainingLateLossRate);
        var babyLeafPlants = allocation.BabyLeaf;
        var conventionalPlants = allocation.Conventional;
        var warnings = new List<string>();
        var usesBabyLeaf = babyLeafPlants > 0;
        var usesConventional = conventionalPlants > 0;

        if (usesBabyLeaf && babyLeafCut.Date is null)
        {
            warnings.Add(babyLeafCut.IsOverdue
                ? "La ventana Baby Leaf ya cerró: estas plantas ya no se pueden cortar como Baby Leaf."
                : "Sin proyección de GDD para estimar el corte Baby Leaf.");
        }
        if (usesConventional && conventionalCut.Date is null)
            warnings.Add("Sin proyección de GDD para estimar el corte de adultas.");
        if (usesConventional && conventionalCut.IsOverdue)
            warnings.Add("El lote ya pasó la sobremadurez: el corte de adultas sería ahora, fuera de la ventana.");
        if (babyLeafPlants + conventionalPlants == 0)
            warnings.Add("No hay plantas para cosechar en el escenario.");

        var available = (!usesBabyLeaf || babyLeafCut.Date is not null)
                        && (!usesConventional || conventionalCut.Date is not null);

        // El ciclo termina con el último corte de la estrategia.
        DateOnly? endDate = available
            ? new[] { usesBabyLeaf ? babyLeafCut.Date : null, usesConventional ? conventionalCut.Date : null }
                .Where(d => d is not null).Select(d => d!.Value).DefaultIfEmpty(input.Today).Max()
            : null;
        var cycleDays = endDate is { } end ? Math.Max(0, end.DayNumber - input.SowingDate.DayNumber) : 0;

        var harvest = LotYieldRules.Harvested(babyLeafPlants, conventionalPlants, input.Weights, input.Prices);
        var totalKg = Total(harvest.Real.BabyLeaf.Plants, harvest.Real.BabyLeaf.Kg, harvest.Real.Conventional.Plants, harvest.Real.Conventional.Kg);
        var revenue = available ? harvest.TotalRevenue : null;

        // Agua y nutrientes: cada planta consume desde la siembra hasta su corte (las que ya se
        // cosecharon o descartaron, hasta ese día; las pérdidas, hasta el fin de su etapa).
        NutrientUsage? usage = null;
        if (endDate is { } last)
        {
            var spans = input.ExitedPlantDates
                .GroupBy(d => d)
                .Select(g => new NutrientConsumptionRules.PlantSpan(g.Count(), g.Key))
                .Append(new(babyLeafPlants, babyLeafCut.Date ?? last))
                .Append(new(conventionalPlants, conventionalCut.Date ?? last))
                .Append(new(allocation.EarlyLost, input.Timeline.BabyLeafCloses.Date is { } closes && closes < last ? closes : last))
                .Append(new(allocation.LateLost, conventionalCut.Date ?? last));
            usage = NutrientConsumptionRules.Calculate(
                input.SowingDate, input.Timeline.BabyLeafOpens.Date, input.Timeline.BabyLeafCloses.Date, spans);
        }

        var costs = CostCalculator.Calculate(input.Costs, input.Seeds, usage, cycleDays, input.Prices.Currency);
        if (!available)
            costs = costs with { Total = null, TotalNotCalculableReason = "Sin fecha de corte: no se puede presupuestar el ciclo." };

        // Costos e ingresos están en la misma moneda (la de los precios).
        decimal? margin = revenue is { } income && costs.Total is { } total ? Math.Round(income - total, 2) : null;

        return new SimulationStrategyOutcome(
            kind,
            name,
            available,
            usesBabyLeaf ? babyLeafCut : null,
            usesConventional ? conventionalCut : null,
            endDate,
            harvest.Real.BabyLeaf,
            harvest.Real.Conventional,
            allocation.Lost,
            available ? totalKg : null,
            revenue,
            costs,
            margin,
            available ? CostCalculator.CostPerKg(costs.Total, totalKg) : null,
            warnings);
    }

    /// <summary>Kg totales; null si a un destino con plantas le falta el peso.</summary>
    private static decimal? Total(int babyLeafPlants, decimal? babyLeafKg, int conventionalPlants, decimal? conventionalKg) =>
        (babyLeafPlants == 0 || babyLeafKg is not null) && (conventionalPlants == 0 || conventionalKg is not null)
            ? (babyLeafKg ?? 0m) + (conventionalKg ?? 0m)
            : null;
}
