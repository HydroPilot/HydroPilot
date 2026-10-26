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
/// de la siembra al último corte, + el trasplante de las que van a Adulta.</item>
/// <item>Con trasplante (<see cref="TransplantScenario"/>): las que van a Adulta se trasplantan a los huecos libres de
/// Adultas (las que no entran se cortan como Baby Leaf si llegan, si no se pierden) y pagan el lugar que ocupan
/// (<see cref="SpaceCostRules"/>). Margen = ingresos − gastos del sistema − lugar ocupado.</item>
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
        IReadOnlyList<DateOnly> ExitedPlantDates, // lote: el día que se cosechó o descartó cada planta que ya no está activa
        TransplantScenario? Transplant = null);

    /// <summary>
    /// Trasplante del escenario: separaciones (lugar de cada planta), costo por planta trasplantada, huecos libres de
    /// Adultas (null = sin límite) y las ya trasplantadas (activas), que son Adultas en todas las estrategias.
    /// </summary>
    public sealed record TransplantScenario(
        decimal BabyLeafSpacingCm,
        decimal AdultSpacingCm,
        decimal CostPerPlant,
        int? AdultCapacity,
        int FixedAdults);

    /// <summary>
    /// Reparto de plantas de una estrategia: Baby Leaf, adultas y pérdidas previstas (tempranas, tardías y las que no
    /// entraron en Adultas ni llegaron a Baby Leaf). Transplanted = las que la estrategia trasplanta.
    /// </summary>
    public sealed record PlantAllocation(int BabyLeaf, int Conventional, int EarlyLost, int LateLost, int NoRoom = 0, int Transplanted = 0)
    {
        public int Lost => EarlyLost + LateLost + NoRoom;
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
    /// de las vivas (con meta, hasta la meta; todo adulta, ninguna); el resto va a Adulta (trasplantadas
    /// hasta llenar los huecos de Adultas; las que no entran se cortan como Baby Leaf si llegan y si no se
    /// pierden) y pierde las tardías. Las ya trasplantadas (<paramref name="fixedAdults"/>, aparte de
    /// <paramref name="plants"/>) son adultas siempre. Todo redondeado a plantas enteras.
    /// </summary>
    public static PlantAllocation Allocate(
        SimulationStrategyKind kind,
        int plants,
        decimal? goalPercent,
        decimal? aptRate,
        decimal earlyLossRate,
        decimal lateLossRate,
        int? adultCapacity = null,
        int fixedAdults = 0)
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

        var wanted = alive - babyLeaf;
        var transplanted = adultCapacity is { } room ? Math.Min(wanted, Math.Max(0, room)) : wanted;
        var overflow = wanted - transplanted;
        var overflowBabyLeaf = Math.Min(overflow, maxBabyLeaf - babyLeaf);
        babyLeaf += overflowBabyLeaf;
        var noRoom = overflow - overflowBabyLeaf;

        var adults = transplanted + Math.Max(0, fixedAdults);
        var lateLost = Math.Min(adults, Round(adults * lateLossRate));
        return new PlantAllocation(babyLeaf, adults - lateLost, earlyLost, lateLost, noRoom, transplanted);
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
        var transplant = input.Transplant;
        var fixedAdults = transplant?.FixedAdults ?? 0;
        var allocation = Allocate(kind, input.Plants - fixedAdults, input.BabyLeafGoalPercent, input.AptRate,
            input.RemainingEarlyLossRate, input.RemainingLateLossRate, transplant?.AdultCapacity, fixedAdults);
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
        if (allocation.NoRoom > 0)
            warnings.Add($"{allocation.NoRoom} plantas no entran en Adultas: quedan en el denso y se pierden al cerrar la ventana Baby Leaf.");

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
                .Append(new(allocation.EarlyLost + allocation.NoRoom, input.Timeline.BabyLeafCloses.Date is { } closes && closes < last ? closes : last))
                .Append(new(allocation.LateLost, conventionalCut.Date ?? last));
            usage = NutrientConsumptionRules.Calculate(
                input.SowingDate, input.Timeline.BabyLeafOpens.Date, input.Timeline.BabyLeafCloses.Date, spans);
        }

        // El trasplante se paga por todas las que van a Adulta en el ciclo (las ya trasplantadas también).
        var costs = CostCalculator.Calculate(input.Costs, input.Seeds, usage, cycleDays, input.Prices.Currency,
            transplant is null ? 0 : allocation.Transplanted + fixedAdults, transplant?.CostPerPlant);
        if (!available)
            costs = costs with { Total = null, TotalNotCalculableReason = "Sin fecha de corte: no se puede presupuestar el ciclo." };

        var space = transplant is null ? null : SpaceCost(input, transplant, allocation.Transplanted + fixedAdults, babyLeafCut, conventionalCut);

        // Costos e ingresos están en la misma moneda (la de los precios).
        decimal? margin = revenue is { } income && costs.Total is { } total && (space is null || space.Total is not null)
            ? Math.Round(income - total - (space?.Total ?? 0m), 2)
            : null;

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
            warnings,
            space);
    }

    /// <summary>
    /// Lugar ocupado por las Adultas: equivalencia (lugar × días de una Adulta ÷ los de una Baby Leaf) − 1, por el margen
    /// de una Baby Leaf de este escenario (ingreso − semilla − sus nutrientes hasta el corte).
    /// </summary>
    private static SimulationSpaceCost SpaceCost(
        Input input, TransplantScenario transplant, int adults, SimulationCut babyLeafCut, SimulationCut conventionalCut)
    {
        if (adults == 0)
            return new SimulationSpaceCost(0, null, null, 0m, null);

        var timeline = input.Timeline;
        var babyLeafDate = babyLeafCut.Date ?? timeline.BabyLeafOptimum.Date;
        if (babyLeafDate is not { } blCut || timeline.Transplant?.Date is not { } decision || conventionalCut.Date is not { } adultCut)
            return new SimulationSpaceCost(adults, null, null, null, "Sin fechas de corte o de trasplante: no se puede medir el lugar ocupado.");

        var equivalence = SpaceCostRules.Equivalence(transplant.BabyLeafSpacingCm, transplant.AdultSpacingCm,
            blCut.DayNumber - input.SowingDate.DayNumber, decision.DayNumber - input.SowingDate.DayNumber,
            adultCut.DayNumber - decision.DayNumber);
        if (equivalence is not { } eq)
            return new SimulationSpaceCost(adults, null, null, null, "Sin días de Baby Leaf: no se puede medir el lugar ocupado.");

        if (input.Weights.BabyLeafGramsPerPlant is not { } grams || input.Prices.BabyLeafPerKg is not { } price)
            return new SimulationSpaceCost(adults, eq, null, null, "Falta el peso o el precio de Baby Leaf para valorizar el lugar.");
        if (input.Costs.PricePerSeed is not { } seed || input.Costs.NutrientPricePerLiter is not { } nutrientPrice)
            return new SimulationSpaceCost(adults, eq, null, null, "Faltan el precio por semilla o el de nutrientes para valorizar el lugar.");

        var nutrients = NutrientConsumptionRules.Calculate(input.SowingDate, timeline.BabyLeafOpens.Date, timeline.BabyLeafCloses.Date,
            [new NutrientConsumptionRules.PlantSpan(1, blCut)]);
        var margin = Math.Round(grams / 1000m * price - seed - nutrients.NutrientLiters * nutrientPrice, 2);
        return new SimulationSpaceCost(adults, eq, margin, SpaceCostRules.Total(adults, eq, margin), null);
    }

    /// <summary>Kg totales; null si a un destino con plantas le falta el peso.</summary>
    private static decimal? Total(int babyLeafPlants, decimal? babyLeafKg, int conventionalPlants, decimal? conventionalKg) =>
        (babyLeafPlants == 0 || babyLeafKg is not null) && (conventionalPlants == 0 || conventionalKg is not null)
            ? (babyLeafKg ?? 0m) + (conventionalKg ?? 0m)
            : null;
}
