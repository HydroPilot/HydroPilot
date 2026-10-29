using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Services.Simulation;

/// <summary>
/// Superficie a repartir (null = la de los circuitos activos de hoy), precio por semilla y kg de Baby Leaf que se pueden
/// vender por mes (null = lo vendido en los últimos 12 meses; las adultas no tienen tope).
/// </summary>
public sealed record GreenhousePlanRequest(decimal? AreaM2, decimal? PricePerSeed, decimal? BabyLeafKgPerMonth = null);

/// <summary>Lo que precarga la vista antes de calcular: la superficie de los circuitos activos y lo vendido de Baby Leaf.</summary>
public sealed record GreenhousePlanDefaults(decimal AreaM2, decimal? SoldBabyLeafKgPerMonth);

/// <summary>
/// De dónde sale el ciclo: lotes cerrados usados, sus fechas, lo que tardó cada destino y los kg de Baby Leaf vendidos por
/// mes en los últimos 12 meses (null = ninguna).
/// </summary>
public sealed record GreenhouseCycleSource(
    int Lots, DateOnly From, DateOnly To, decimal AverageDailyGdd, decimal BabyLeafDays, decimal? SoldBabyLeafKgPerMonth);

/// <summary>
/// Plan del invernadero: el ciclo del histórico, un circuito de cada tipo, la superficie, el tope de Baby Leaf usado
/// (kg/mes, null = sin tope), cómo está hoy, la mejor distribución, todas las que entran, lo que deja por mes un circuito de
/// cada tipo, cuántos densos cubren el tope y los gramos por planta (para pasar a kg). Cycle null = no se puede planificar
/// (los motivos van en Warnings).
/// </summary>
public sealed record GreenhousePlanResult(
    GreenhouseCycle? Cycle,
    GreenhouseCycleSource? Source,
    GreenhouseCircuits Circuits,
    decimal AreaM2,
    decimal? BabyLeafCapKg,
    GreenhouseOption? Current,
    GreenhouseOption? Best,
    IReadOnlyList<GreenhouseOption> Options,
    decimal? DenseCircuitPerMonth,
    decimal? AdultCircuitPerMonth,
    decimal? DenseCircuitsForCap,
    decimal BabyLeafGrams,
    decimal AdultGrams,
    string Currency,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Arma el plan del invernadero (<see cref="GreenhousePlanRules"/>) con lo que pasó en los lotes cerrados de los últimos
/// 12 meses (ciclos, Baby Leaf, trasplantadas, pérdidas) y los precios y parámetros de hoy. Solo lectura.
/// </summary>
public sealed class GreenhousePlanService
{
    public const int HistoryDays = 365;
    public const decimal DefaultSeedPrice = 2.5m;

    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly SystemParametersService _parameters;
    private readonly BabyLeafOutlookService _outlook;

    public GreenhousePlanService(
        IDbContextFactory<HydroPilotDbContext> dbFactory, SystemParametersService parameters, BabyLeafOutlookService outlook)
    {
        _dbFactory = dbFactory;
        _parameters = parameters;
        _outlook = outlook;
    }

    /// <summary>Superficie de los circuitos activos y kg de Baby Leaf vendidos por mes (sin calcular el plan).</summary>
    public async Task<GreenhousePlanDefaults> DefaultsAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var transplant = await _parameters.GetTransplantParametersAsync(ct);
        var yield = await _parameters.GetYieldParametersAsync(ct);
        var area = AreaOf(await ActiveCircuitsAsync(context, ct), transplant);
        var cropId = await context.CropTypes.AsNoTracking().OrderBy(c => c.Id).Select(c => (int?)c.Id).FirstOrDefaultAsync(ct);
        var sold = cropId is { } crop && yield.BabyLeafGrams is { } grams
            ? await SoldBabyLeafKgPerMonthAsync(context, crop, grams, ct)
            : null;
        return new GreenhousePlanDefaults(area, sold);
    }

    public async Task<GreenhousePlanResult> PlanAsync(GreenhousePlanRequest request, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var warnings = new List<string>();
        var transplant = await _parameters.GetTransplantParametersAsync(ct);
        var yield = await _parameters.GetYieldParametersAsync(ct);

        // Circuitos de hoy: la grilla más común de cada tipo y cuántos hay.
        var circuits = await ActiveCircuitsAsync(context, ct);
        int Typical(string kind, int fallback) => circuits.Where(c => c.Kind == kind).Select(c => c.Holes)
            .DefaultIfEmpty(fallback).GroupBy(h => h).OrderByDescending(g => g.Count()).First().Key;
        var denseHoles = Typical(CircuitKinds.Dense, 200);
        var adultHoles = Typical(CircuitKinds.Adult, 20);
        var kinds = new GreenhouseCircuits(denseHoles, adultHoles,
            Math.Round(denseHoles * transplant.DenseHoleM2, 3), Math.Round(adultHoles * transplant.AdultHoleM2, 3));
        var currentDense = circuits.Count(c => c.Kind == CircuitKinds.Dense);
        var currentAdult = circuits.Count(c => c.Kind == CircuitKinds.Adult);
        var area = request.AreaM2 is > 0 ? request.AreaM2.Value : AreaOf(circuits, transplant);

        var cropId = await context.CropTypes.AsNoTracking().OrderBy(c => c.Id).Select(c => (int?)c.Id).FirstOrDefaultAsync(ct);
        var (cycle, source) = cropId is { } crop
            ? await CycleAsync(context, crop, transplant, yield, request.PricePerSeed ?? DefaultSeedPrice, warnings, ct)
            : (null, null);
        var blGrams = yield.BabyLeafGrams ?? 0m;
        var adultGrams = yield.ConventionalGrams ?? 0m;
        if (cycle is null)
            return new GreenhousePlanResult(
                null, source, kinds, area, null, null, null, [], null, null, null, blGrams, adultGrams, yield.Currency, warnings);

        // Tope de Baby Leaf (kg/mes → plantas): el pedido o, si no, lo vendido en los últimos 12 meses. Adultas sin tope.
        var capKg = request.BabyLeafKgPerMonth is > 0 ? request.BabyLeafKgPerMonth : source?.SoldBabyLeafKgPerMonth;
        if (capKg is null)
            warnings.Add("Sin Baby Leaf vendidas en los últimos 12 meses: sin tope de venta. Cargá cuántos kg podés vender por mes.");
        var cap = capKg is { } kg && blGrams > 0 ? kg * 1000m / blGrams : (decimal?)null;

        var options = GreenhousePlanRules.Options(cycle, kinds, area, cap);
        if (options.Count == 0)
            warnings.Add("En esa superficie no entra ni un circuito denso.");
        var current = currentDense > 0 ? GreenhousePlanRules.Evaluate(cycle, kinds, currentDense, currentAdult, cap) : null;
        return new GreenhousePlanResult(
            cycle, source, kinds, area, cap is null ? null : capKg, current, GreenhousePlanRules.Best(options), options,
            GreenhousePlanRules.DenseCircuitPerMonth(cycle, kinds), GreenhousePlanRules.AdultCircuitPerMonth(cycle, kinds),
            cap is { } plants ? GreenhousePlanRules.DenseCircuitsForCap(cycle, kinds, plants) : null,
            blGrams, adultGrams, yield.Currency, warnings);
    }

    /// <summary>El ciclo típico de los lotes cerrados (los de los últimos 12 meses; si no hay, todos).</summary>
    private async Task<(GreenhouseCycle?, GreenhouseCycleSource?)> CycleAsync(
        HydroPilotDbContext context, int cropId, TransplantParameters transplant, YieldParameters yield, decimal seedPrice,
        List<string> warnings, CancellationToken ct)
    {
        if (yield.BabyLeafGrams is not { } blGrams || yield.ConventionalGrams is not { } adultGrams
            || yield.BabyLeafPricePerKg is not { } blPrice || yield.ConventionalPricePerKg is not { } adultPrice)
        {
            warnings.Add("Faltan pesos o precios por kg en Ajustes > Parámetros: no se puede valorizar.");
            return (null, null);
        }

        var lots = await context.Lots.AsNoTracking()
            .Where(l => l.CropTypeId == cropId && l.Status != null
                        && (l.Status.Name == LotStatusNames.Cosechado || l.Status.Name == LotStatusNames.Descartado))
            .Select(l => new { l.Id, l.SowingDate, Closed = l.ActualHarvestDate ?? l.SowingDate })
            .ToListAsync(ct);
        var since = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-HistoryDays);
        var used = lots.Where(l => l.Closed >= since).ToList();
        if (used.Count == 0 && lots.Count > 0)
        {
            used = lots;
            warnings.Add("Sin lotes cerrados en los últimos 12 meses: se usan todos los cerrados.");
        }
        if (used.Count == 0)
        {
            warnings.Add("Sin lotes cerrados: hace falta el historial de al menos un ciclo para planificar.");
            return (null, null);
        }

        var ids = used.Select(l => l.Id).ToList();
        var sowing = used.ToDictionary(l => l.Id, l => l.SowingDate);
        var plants = await context.Plants.AsNoTracking()
            .Where(p => ids.Contains(p.LotId))
            .Select(p => new
            {
                p.LotId,
                p.OperationalState,
                p.HarvestDate,
                p.DiscardDate,
                p.TransplantedOn,
                CutAsBabyLeaf = p.OperationalState == PlantOperationalState.Cosechada && p.TransplantedOn == null
                    && p.CommercialStage != null
                    && (p.CommercialStage.Name == CommercialStageNames.BabyLeafApta
                        || p.CommercialStage.Name == CommercialStageNames.CandidataBabyLeaf),
            })
            .ToListAsync(ct);
        if (plants.Count == 0)
        {
            warnings.Add("Los lotes cerrados no tienen plantas: no hay con qué planificar.");
            return (null, null);
        }
        var outcomes = (await _outlook.GetCycleOutcomesAsync(cropId, ct)).Where(o => ids.Contains(o.LotId)).ToList();

        // Clima de los ciclos: GDD por día (para repartir los días de cada etapa y los nutrientes).
        var gdds = outcomes.Where(o => o.AverageDailyGdd is > 0).Select(o => o.AverageDailyGdd!.Value).ToList();
        var dailyGdd = gdds.Count > 0 ? gdds.Average() : 15m;
        if (gdds.Count == 0)
            warnings.Add("Sin lecturas de temperatura en los lotes usados: se suponen 15 GDD por día.");
        var config = await context.BabyLeafConfigs.AsNoTracking()
            .Where(c => c.CropTypeId == cropId && c.IsActive)
            .OrderByDescending(c => c.Id)
            .Select(c => new { c.GddMin, c.GddMax })
            .FirstOrDefaultAsync(ct);
        var opensDay = (int)Math.Round((config?.GddMin ?? 250m) / dailyGdd);
        var closesDay = (int)Math.Round((config?.GddMax ?? 450m) / dailyGdd);
        var decisionDay = (int)Math.Round(transplant.DecisionGdd / dailyGdd);

        // Destinos: Baby Leaf, las que pueden ir a Adultas (vivas que no fueron Baby Leaf) y las adultas perdidas.
        var total = plants.Count;
        var cut = plants.Count(p => p.CutAsBabyLeaf);
        var early = outcomes.Sum(o => o.EarlyLosses ?? 0);
        var transplanted = plants.Where(p => p.TransplantedOn != null).ToList();
        var lossRate = transplanted.Count > 0
            ? (decimal)transplanted.Count(p => p.OperationalState == PlantOperationalState.Descartada) / transplanted.Count
            : 0m;

        // Días: el denso hasta que sale la última planta (las trasplantadas salen al trasplantarse) y la adulta en su hueco.
        var denseDays = plants
            .GroupBy(p => p.LotId)
            .Select(g => (Lot: g.Key, Exit: g.Select(p => p.TransplantedOn ?? p.HarvestDate ?? p.DiscardDate).Max()))
            .Where(x => x.Exit is not null)
            .Select(x => (decimal)(x.Exit!.Value.DayNumber - sowing[x.Lot].DayNumber))
            .DefaultIfEmpty(30m)
            .Average();
        var adultSpans = transplanted.Where(p => p.HarvestDate is not null)
            .Select(p => (decimal)(p.HarvestDate!.Value.DayNumber - p.TransplantedOn!.Value.DayNumber))
            .ToList();
        var adultDays = adultSpans.Count > 0
            ? adultSpans.Average()
            : (CycleTimelineLogic.ConventionalOptimumGdd - transplant.DecisionGdd) / dailyGdd + transplant.StressDays;
        if (adultSpans.Count == 0)
            warnings.Add("Ningún lote cerrado tuvo adultas cosechadas: los días de la adulta salen del GDD (650 − decisión) y el estrés.");
        var babyLeafSpans = plants.Where(p => p.CutAsBabyLeaf && p.HarvestDate is not null)
            .Select(p => (decimal)(p.HarvestDate!.Value.DayNumber - sowing[p.LotId].DayNumber))
            .ToList();
        var babyLeafDays = babyLeafSpans.Count > 0 ? babyLeafSpans.Average() : CycleTimelineLogic.BabyLeafOptimumGdd / dailyGdd;

        // Nutrientes de una planta hasta un día del ciclo (con las dosis de cada etapa y los precios de A y B).
        var nutrientPrices = await _parameters.GetNutrientPricesAsync(ct);
        if (nutrientPrices.SolutionAPerLiter is null || nutrientPrices.SolutionBPerLiter is null)
            warnings.Add("Falta el precio de la Solución A o B en Ajustes > Parámetros: esos nutrientes se cuentan en $0.");
        decimal Nutrients(decimal untilDay)
        {
            var day0 = new DateOnly(2026, 1, 1); // solo cuentan los días de cada etapa
            var usage = NutrientConsumptionRules.Calculate(day0, day0.AddDays(opensDay), day0.AddDays(closesDay),
                [new NutrientConsumptionRules.PlantSpan(1, day0.AddDays(Math.Max(1, (int)Math.Round(untilDay))))]);
            return usage.SolutionALiters * (nutrientPrices.SolutionAPerLiter ?? 0m)
                   + usage.SolutionBLiters * (nutrientPrices.SolutionBPerLiter ?? 0m);
        }

        var cycle = new GreenhouseCycle(
            DenseCycleDays: Math.Round(denseDays + SimulationStrategies.DenseCleaningDays, 1),
            AdultDays: Math.Round(adultDays, 1),
            BabyLeafShare: Math.Round((decimal)cut / total, 4),
            RescueShare: Math.Round(Math.Max(0m, (decimal)(total - cut - early) / total), 4),
            AdultLossRate: Math.Round(lossRate, 4),
            BabyLeafValue: Math.Round(blGrams / 1000m * blPrice - Nutrients(babyLeafDays), 2),
            AdultValue: Math.Round((1m - lossRate) * adultGrams / 1000m * adultPrice - Nutrients(decisionDay + adultDays)
                                   - transplant.CostPerPlant, 2),
            UnplacedCost: Math.Round(Nutrients(closesDay), 2),
            SeedCost: seedPrice);

        var sold = await SoldBabyLeafKgPerMonthAsync(context, cropId, blGrams, ct);
        var source = new GreenhouseCycleSource(used.Count, used.Min(l => l.SowingDate), used.Max(l => l.Closed),
            Math.Round(dailyGdd, 1), Math.Round(babyLeafDays, 1), sold);
        return (cycle, source);
    }

    private sealed record ActiveCircuit(string Kind, int Holes);

    private static async Task<List<ActiveCircuit>> ActiveCircuitsAsync(HydroPilotDbContext context, CancellationToken ct) =>
        await context.Circuits.AsNoTracking()
            .Where(c => c.IsActive)
            .Select(c => new ActiveCircuit(c.Kind, c.GridRows * c.GridColumns))
            .ToListAsync(ct);

    private static decimal AreaOf(IEnumerable<ActiveCircuit> circuits, TransplantParameters transplant) =>
        Math.Round(circuits.Sum(c => c.Holes * (c.Kind == CircuitKinds.Adult ? transplant.AdultHoleM2 : transplant.DenseHoleM2)), 2);

    /// <summary>
    /// Lo vendido de Baby Leaf por mes en los últimos 12 meses (lotes cerrados y activos), el tope por defecto: Baby Leaf
    /// cosechadas × g ÷ meses (si el historial es más corto, sus meses). Null = ninguna.
    /// </summary>
    private static async Task<decimal?> SoldBabyLeafKgPerMonthAsync(
        HydroPilotDbContext context, int cropId, decimal blGrams, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var since = today.AddDays(-HistoryDays);
        var soldCount = await context.Plants.AsNoTracking()
            .Where(p => p.Lot != null && p.Lot.CropTypeId == cropId && p.HarvestDate >= since
                        && p.OperationalState == PlantOperationalState.Cosechada && p.TransplantedOn == null
                        && p.CommercialStage != null
                        && (p.CommercialStage.Name == CommercialStageNames.BabyLeafApta
                            || p.CommercialStage.Name == CommercialStageNames.CandidataBabyLeaf))
            .CountAsync(ct);
        var firstSowing = await context.Lots.AsNoTracking()
            .Where(l => l.CropTypeId == cropId)
            .MinAsync(l => (DateOnly?)l.SowingDate, ct);
        if (soldCount == 0 || firstSowing is null)
            return null;
        var days = Math.Min(HistoryDays, today.DayNumber - firstSowing.Value.DayNumber);
        return days > 0 ? Math.Round(soldCount * blGrams / 1000m / (days / GreenhousePlanRules.DaysPerMonth), 1) : null;
    }
}
