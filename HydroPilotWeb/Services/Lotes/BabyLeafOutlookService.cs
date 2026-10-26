using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Forecasting;

namespace HydroPilotWeb.Services.Lotes;

/// <summary>Escenario a prever: cultivo, siembra, su línea de tiempo (GDD hoy y umbrales) y el clima esperado.</summary>
public sealed record BabyLeafOutlookRequest(
    int CropTypeId,
    DateOnly SowingDate,
    CycleTimeline Timeline,
    decimal? ExpectedAverageDailyGdd,
    int? LotId);

/// <summary>
/// Previsión de Baby Leaf y pérdidas (compartida: hoy la usa Simulación). Combina el histórico de
/// lotes cerrados del cultivo, pesado por temporada × clima (<see cref="BabyLeafOutlookRules"/>),
/// con el estado actual del lote si lo hay. Solo lectura.
/// El GDD medido de cada lote cerrado (lo más caro: lee todas sus lecturas) se guarda en memoria,
/// porque un ciclo cerrado ya no cambia.
/// </summary>
public class BabyLeafOutlookService : ILotHistorySource
{
    private static readonly TimeSpan MeasuredCycleCacheTime = TimeSpan.FromHours(6);

    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly GddService _gddService;
    private readonly IMemoryCache? _cache;

    public BabyLeafOutlookService(IDbContextFactory<HydroPilotDbContext> dbFactory, GddService gddService, IMemoryCache? cache = null)
    {
        _dbFactory = dbFactory;
        _gddService = gddService;
        _cache = cache;
    }

    /// <summary>Lo que sale de las lecturas de un ciclo cerrado: cuándo cerró la ventana Baby Leaf y el GDD diario promedio.</summary>
    private sealed record MeasuredCycle(DateOnly? WindowClosedOn, decimal? AverageDailyGdd, bool HasReadings);

    public async Task<BabyLeafOutlook> GetOutlookAsync(BabyLeafOutlookRequest request, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var timeline = request.Timeline;

        var history = BabyLeafOutlookRules.FromHistory(
            await GetCycleOutcomesAsync(context, request.CropTypeId, ct), request.SowingDate, request.ExpectedAverageDailyGdd);

        // Estado actual del lote (E): aptas + candidatas sobre activas, solo si hay evaluaciones.
        decimal? current = null;
        var weight = 0m;
        var hasEvaluations = false;
        if (request.LotId is { } lotId)
        {
            var active = context.Plants.Where(p => p.LotId == lotId && p.OperationalState == PlantOperationalState.Activa);
            var activeCount = await active.CountAsync(ct);
            hasEvaluations = activeCount > 0 && await active.AnyAsync(p => p.Evaluations.Any(), ct);
            if (hasEvaluations)
            {
                var babyLeafReady = await active.CountAsync(p => p.CommercialStage != null
                    && (p.CommercialStage.Name == CommercialStageNames.BabyLeafApta
                        || p.CommercialStage.Name == CommercialStageNames.CandidataBabyLeaf), ct);
                current = Math.Round((decimal)babyLeafReady / activeCount, 4);

                var establishmentEnd = await context.PhenologicalStages.AsNoTracking()
                    .Where(s => s.CropTypeId == request.CropTypeId && s.IsActive)
                    .OrderBy(s => s.Order)
                    .Select(s => (decimal?)s.GddMax)
                    .FirstOrDefaultAsync(ct) ?? 150m;
                weight = BabyLeafOutlookRules.CurrentStateWeight(
                    timeline.AccumulatedGdd, establishmentEnd, timeline.BabyLeafOptimum.Gdd);
            }
        }

        return new BabyLeafOutlook(
            BabyLeafOutlookRules.Blend(history.AptRate, current, weight),
            history,
            current,
            current is null ? 0m : weight,
            hasEvaluations,
            BabyLeafOutlookRules.RemainingEarlyLoss(history.EarlyLossRate, timeline.AccumulatedGdd, timeline.BabyLeafCloses.Gdd),
            BabyLeafOutlookRules.RemainingLateLoss(history.LateLossRate, timeline.AccumulatedGdd,
                timeline.BabyLeafCloses.Gdd, timeline.ConventionalOptimum.Gdd));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LotCycleOutcome>> GetCycleOutcomesAsync(int cropTypeId, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        return await GetCycleOutcomesAsync(context, cropTypeId, ct);
    }

    /// <summary>
    /// Ciclos cerrados del cultivo con plantas. Lo cargado a mano en el lote manda; lo que falta se
    /// calcula: aptas en el óptimo = cortadas como Baby Leaf + las que estuvieron "Baby Leaf" y
    /// siguieron; pérdidas tempranas/tardías = descartes antes/después de que el GDD medido cruzó el
    /// cierre de la ventana Baby Leaf; clima = GDD diario promedio medido.
    /// </summary>
    private async Task<IReadOnlyList<LotCycleOutcome>> GetCycleOutcomesAsync(
        HydroPilotDbContext context, int cropTypeId, CancellationToken ct)
    {
        var lots = await context.Lots.AsNoTracking()
            .Include(l => l.CropType)
            .Where(l => l.CropTypeId == cropTypeId && l.Status != null
                        && (l.Status.Name == LotStatusNames.Cosechado || l.Status.Name == LotStatusNames.Descartado))
            .ToListAsync(ct);
        if (lots.Count == 0)
            return [];

        var ids = lots.Select(l => l.Id).ToList();
        var aptId = await context.CommercialStages.AsNoTracking()
            .Where(s => s.Name == CommercialStageNames.BabyLeafApta)
            .Select(s => (int?)s.Id)
            .FirstOrDefaultAsync(ct);
        var plants = await context.Plants.AsNoTracking()
            .Where(p => ids.Contains(p.LotId))
            .Select(p => new
            {
                p.Id,
                p.LotId,
                p.OperationalState,
                p.HarvestDate,
                p.DiscardDate,
                CutAsBabyLeaf = p.OperationalState == PlantOperationalState.Cosechada && p.CommercialStage != null
                    && (p.CommercialStage.Name == CommercialStageNames.BabyLeafApta
                        || p.CommercialStage.Name == CommercialStageNames.CandidataBabyLeaf),
                WasApt = aptId != null && p.History.Any(h => h.NewCommercialStageId == aptId),
            })
            .ToListAsync(ct);

        var windowClose = await context.BabyLeafConfigs.AsNoTracking()
            .Where(c => c.CropTypeId == cropTypeId && c.IsActive)
            .OrderByDescending(c => c.Id)
            .Select(c => (decimal?)c.GddMax)
            .FirstOrDefaultAsync(ct) ?? 450m;

        // Lo que falta cargado se calcula de las lecturas de cada lote (en paralelo y con caché).
        var withPlants = lots.Where(l => plants.Any(p => p.LotId == l.Id)).ToList();
        var measuredByLot = (await Task.WhenAll(withPlants
                .Where(l => l.EarlyLossPlants is null || l.LateLossPlants is null || l.CycleAverageDailyGdd is null)
                .Select(async l =>
                {
                    var closedOn = l.ActualHarvestDate
                        ?? plants.Where(p => p.LotId == l.Id).Select(p => p.HarvestDate ?? p.DiscardDate).Where(d => d is not null).Max()
                        ?? DateOnly.FromDateTime(DateTime.UtcNow);
                    return (l.Id, Cycle: await MeasuredCycleAsync(l, closedOn, windowClose, ct));
                })))
            .ToDictionary(x => x.Id, x => x.Cycle);

        var outcomes = new List<LotCycleOutcome>();
        foreach (var lot in withPlants)
        {
            var own = plants.Where(p => p.LotId == lot.Id).ToList();
            var cutAsBabyLeaf = own.Count(p => p.CutAsBabyLeaf);
            var harvested = own.Count(p => p.OperationalState == PlantOperationalState.Cosechada);
            var discarded = own.Where(p => p.OperationalState == PlantOperationalState.Descartada).ToList();

            var apt = lot.BabyLeafAptAtOptimum ?? cutAsBabyLeaf + own.Count(p => !p.CutAsBabyLeaf && p.WasApt);
            var (early, late, averageGdd) = (lot.EarlyLossPlants, lot.LateLossPlants, lot.CycleAverageDailyGdd);

            if (measuredByLot.TryGetValue(lot.Id, out var measured) && measured.HasReadings)
            {
                averageGdd ??= measured.AverageDailyGdd;
                if (early is null || late is null)
                {
                    var earlyCount = discarded.Count(p =>
                        measured.WindowClosedOn is null || p.DiscardDate is null || p.DiscardDate <= measured.WindowClosedOn);
                    early ??= earlyCount;
                    late ??= discarded.Count - earlyCount;
                }
            }

            outcomes.Add(new LotCycleOutcome(
                lot.Id,
                lot.Name ?? $"Lote {lot.Id}",
                lot.SowingDate,
                own.Count,
                cutAsBabyLeaf,
                harvested - cutAsBabyLeaf,
                discarded.Count,
                apt,
                early,
                late,
                averageGdd,
                lot.BabyLeafAptAtOptimum is not null || lot.EarlyLossPlants is not null
                    || lot.LateLossPlants is not null || lot.CycleAverageDailyGdd is not null));
        }

        return outcomes;
    }

    private async Task<MeasuredCycle> MeasuredCycleAsync(Lot lot, DateOnly closedOn, decimal windowClose, CancellationToken ct)
    {
        var key = $"hp:lot-cycle:{lot.Id}:{closedOn:yyyyMMdd}:{windowClose}";
        if (_cache?.TryGetValue(key, out MeasuredCycle? cached) == true && cached is not null)
            return cached;

        var measured = (await _gddService.GetGddHistoryAsync(lot, closedOn, ct)).Daily
            .Where(p => p.Source == GddPointSource.Observed)
            .OrderBy(p => p.Date)
            .ToList();
        var cycle = measured.Count == 0
            ? new MeasuredCycle(null, null, false)
            : new MeasuredCycle(CrossDate(measured, windowClose), Math.Round(measured.Average(p => p.Gdd), 2), true);

        _cache?.Set(key, cycle, MeasuredCycleCacheTime);
        return cycle;
    }

    /// <summary>Día en que el GDD acumulado medido llegó al umbral; null si no llegó.</summary>
    private static DateOnly? CrossDate(IReadOnlyList<DailyGddPoint> measured, decimal threshold)
    {
        var cumulative = 0m;
        foreach (var point in measured)
        {
            cumulative += point.Gdd;
            if (cumulative >= threshold)
                return point.Date;
        }
        return null;
    }
}
