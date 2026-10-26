using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;

namespace HydroPilotWeb.Services.Lotes;

/// <summary>
/// Reglas puras de agregación del lote (LOT-06): predominancia comercial
/// con "Mixto" ante empate de conteos. Se cuentan SOLO plantas activas;
/// las posiciones vacías no participan.
/// </summary>
public static class Predominance
{
    /// <summary>
    /// Resuelve el estado predominante por conteo. Ante empate de la cantidad
    /// máxima → (null, IsMixed = true), que la vista muestra como "Mixto".
    /// </summary>
    public static (string? WinnerName, bool IsMixed) Resolve(IReadOnlyDictionary<string, int> counts)
    {
        if (counts.Count == 0)
            return (null, false);

        var max = counts.Values.Max();
        if (max <= 0)
            return (null, false);

        var winners = counts.Where(kv => kv.Value == max).Select(kv => kv.Key).ToList();
        if (winners.Count == 1)
            return (winners[0], false);

        return (null, true);
    }
}

/// <summary>
/// Consultas agregadas de lote/planta (LOT-06): estado del lote con resumen,
/// mapa celda a celda (incluyendo posiciones vacías), conteos por estado,
/// predominancia (Mixto), readiness híbrido de cosecha y detalle de planta con
/// última evaluación, última imagen e histórico de cambios.
///
/// El GDD mostrado proviene de GddService (propietario: forecasting) — fuente
/// canónica; no se duplica el cálculo en este módulo.
/// </summary>
public class LotAggregateService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly GddService _gddService;

    public LotAggregateService(IDbContextFactory<HydroPilotDbContext> dbFactory, GddService gddService)
    {
        _dbFactory = dbFactory;
        _gddService = gddService;
    }

    public async Task<LotStateDto?> GetLotStateAsync(
        int lotId,
        DateOnly? asOfDate = null,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var lot = await context.Lots
            .Include(l => l.CropType)
            .Include(l => l.Status)
            .Include(l => l.AppliedPhenologicalStage)
            .Include(l => l.PredominantPhenologicalStage)
            .Include(l => l.PredominantCommercialStage)
            .FirstOrDefaultAsync(l => l.Id == lotId, ct);

        if (lot is null)
            return null;

        // El snapshot persistido es del día de hoy: con fecha simulada se recalcula a esa fecha.
        var gdd = asOfDate is null && lot.AccumulatedGdd is { } snapshot
            ? snapshot
            : await _gddService.GetAccumulatedGddAsync(lot, asOfDate, ct);

        var plants = await context.Plants
            .Include(p => p.CommercialStage)
            .Include(p => p.PhenologicalStage)
            .Where(p => p.LotId == lotId)
            .OrderBy(p => p.Row).ThenBy(p => p.Column)
            .ToListAsync(ct);

        // Ética de datos: si no hay evaluación/imagen, no se muestran valores inventados.
        var warnings = new List<string>();
        if (gdd == 0 && (asOfDate is not null || !lot.AccumulatedGdd.HasValue))
            warnings.Add("No hay lecturas de temperatura para calcular GDD: se muestra 0.");

        if (lot.GridRows is not int rows || lot.GridColumns is not int columns
            || rows <= 0 || columns <= 0)
        {
            warnings.Add("Grilla no configurada para este lote (filas/columnas nulas): el mapa no puede renderizarse.");
            rows = 0;
            columns = 0;
        }

        // Conteos por estado comercial sobre plantas ACTIVAS (predominancia).
        var activeCounts = plants
            .Where(p => p.OperationalState == PlantOperationalState.Activa)
            .GroupBy(p => p.CommercialStage?.Name ?? "(sin estado)")
            .ToDictionary(g => g.Key, g => g.Count());

        var (predominantName, isMixed) = Predominance.Resolve(activeCounts);

        // Mapa: celda por hueco. El del circuito denso (la grilla del lote) y uno por circuito de Adultas con
        // trasplantadas. Las plantas sin circuito (lotes de antes de los circuitos) son del denso. En el denso, el hueco
        // de cada trasplantada queda marcado con dónde está ahora.
        static List<PlantCellDto> Cells(int rows, int columns, IEnumerable<Plant> plants, int? circuitId,
            IReadOnlyDictionary<(int Row, int Column), PlantCellMove>? moved = null)
        {
            var cells = new List<PlantCellDto>(rows * columns);
            var byPosition = plants.GroupBy(p => (p.Row, p.Column)).ToDictionary(g => g.Key, g => g.First());
            for (var row = 1; row <= rows; row++)
            {
                for (var col = 1; col <= columns; col++)
                {
                    if (byPosition.TryGetValue((row, col), out var plant))
                    {
                        // Cosechada se muestra con su estado comercial congelado.
                        var discarded = plant.OperationalState == PlantOperationalState.Descartada;
                        cells.Add(new PlantCellDto(row, col, plant.Id, plant.CommercialStage?.Name, plant.OperationalState,
                            discarded ? "discarded" : "plant", circuitId, plant.IsAdultRoute));
                    }
                    else if (moved is not null && moved.TryGetValue((row, col), out var to))
                    {
                        cells.Add(new PlantCellDto(row, col, null, null, null, "transplanted", circuitId, MovedTo: to));
                    }
                    else
                    {
                        cells.Add(new PlantCellDto(row, col, null, null, null, "empty", circuitId));
                    }
                }
            }
            return cells;
        }

        var denseId = lot.CircuitId;
        bool InDense(Plant p) => p.CircuitId is null || p.CircuitId == denseId;

        var adultIds = plants.Where(p => !InDense(p)).Select(p => p.CircuitId!.Value).Distinct().ToList();
        var circuits = await context.Circuits.AsNoTracking()
            .Where(c => adultIds.Contains(c.Id) || c.Id == denseId)
            .Select(c => new { c.Id, c.Name, c.GridRows, c.GridColumns })
            .ToListAsync(ct);
        var names = circuits.ToDictionary(c => c.Id, c => c.Name);
        PlantCellMove MoveOf(Plant p) => new(p.CircuitId!.Value, names.GetValueOrDefault(p.CircuitId.Value, "Adultas"), p.Row, p.Column);
        var moved = plants
            .Where(p => !InDense(p) && p.DenseRow is not null && p.DenseColumn is not null)
            .GroupBy(p => (p.DenseRow!.Value, p.DenseColumn!.Value))
            .ToDictionary(g => g.Key, g => MoveOf(g.First()));
        var cells = Cells(rows, columns, plants.Where(InDense), denseId, moved);

        var adultMaps = circuits
            .Where(c => adultIds.Contains(c.Id))
            .OrderBy(c => c.Name)
            .Select(c => new LotCircuitMapDto(c.Id, c.Name, c.GridRows, c.GridColumns,
                Cells(c.GridRows, c.GridColumns, plants.Where(p => p.CircuitId == c.Id), c.Id)))
            .ToList();

        var stageCounts = activeCounts
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new StageCountDto(kv.Key, kv.Value))
            .ToList();

        var readiness = await ComputeReadinessAsync(lot, gdd, activeCounts, plants, ct);

        // EC objetivo de la etapa aplicada (receta vigente).
        decimal? ecObjective = lot.AppliedPhenologicalStage?.EcObjective;

        var phenoName = lot.AppliedPhenologicalStage?.Name;
        var phenoOrder = lot.AppliedPhenologicalStage?.Order;

        return new LotStateDto(
            LotId: lot.Id,
            Name: lot.Name,
            CropTypeName: lot.CropType?.Name ?? "Desconocido",
            SowingDate: lot.SowingDate,
            AreaM2: lot.PlantedAreaM2,
            GddAccumulated: Math.Round(gdd, 2),
            StatusName: lot.Status?.Name,
            PhenologicalStageName: phenoName,
            PhenologicalStageOrder: phenoOrder,
            CommercialStageName: isMixed ? CommercialStageNames.Mixto : predominantName,
            IsCommercialStageMixed: isMixed,
            CurrentPh: lot.CurrentPh,
            CurrentEc: lot.CurrentEc,
            EcObjective: ecObjective,
            GridRows: rows,
            GridColumns: columns,
            TotalConfiguredPositions: rows * columns,
            TotalPlants: plants.Count,
            ActivePlants: plants.Count(p => p.OperationalState == PlantOperationalState.Activa),
            HarvestedPlants: plants.Count(p => p.OperationalState == PlantOperationalState.Cosechada),
            DiscardedPlants: plants.Count(p => p.OperationalState == PlantOperationalState.Descartada),
            EmptyPositions: (rows * columns) - plants.Count,
            CommercialCounts: stageCounts,
            Cells: cells,
            HarvestReadiness: readiness,
            Warnings: warnings,
            AdultMaps: adultMaps,
            CircuitName: circuits.FirstOrDefault(c => c.Id == denseId)?.Name);
    }

    /// <summary>
    /// Regla híbrida de cosecha: GDD dentro de la ventana Baby Leaf del cultivo
    /// Y porcentaje de plantas aptas sobre activas ≥ objetivo configurado.
    /// Sin objetivo configurado → IsReady null + advertencia (decisión pendiente).
    /// </summary>
    public async Task<LotHarvestReadiness?> ComputeReadinessAsync(
        Lot lot,
        decimal gdd,
        IReadOnlyDictionary<string, int> activeCounts,
        IReadOnlyList<Plant> plants,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var config = await context.BabyLeafConfigs
            .Where(c => c.CropTypeId == lot.CropTypeId && c.IsActive)
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync(ct);

        var warnings = new List<string>();
        if (config is null)
        {
            warnings.Add("No hay configuración Baby Leaf activa para el cultivo: no se puede evaluar la ventana GDD.");
            return new LotHarvestReadiness(false, 0, 0, 0m, lot.BabyLeafHarvestTargetPercent, null, warnings);
        }

        var aptaCount = activeCounts.TryGetValue(CommercialStageNames.BabyLeafApta, out var apta) ? apta : 0;
        var totalActive = activeCounts.Values.Sum();

        var ruleResult = HarvestReadinessRules.Evaluate(new HarvestReadinessRules.Input(
            gdd, config.GddMin, config.GddMax, aptaCount, totalActive, lot.BabyLeafHarvestTargetPercent));

        if (ruleResult.PendingReason is not null)
            warnings.Add(ruleResult.PendingReason);
        if (ruleResult.IsReady == false)
        {
            if (!ruleResult.GddInWindow)
                warnings.Add($"GDD {gdd} fuera de la ventana Baby Leaf ({config.GddMin}–{config.GddMax}).");
            if (ruleResult.AptaPercent < lot.BabyLeafHarvestTargetPercent)
                warnings.Add($"Porcentaje de Baby Leafs ({ruleResult.AptaPercent}%) por debajo del objetivo ({lot.BabyLeafHarvestTargetPercent}%).");
        }

        return new LotHarvestReadiness(
            ruleResult.GddInWindow, aptaCount, totalActive, ruleResult.AptaPercent,
            lot.BabyLeafHarvestTargetPercent, ruleResult.IsReady, warnings);
    }

    /// <summary>Detalle de una planta: estados, GDD/EC/pH del lote, última evaluación, imagen e histórico.</summary>
    public async Task<PlantDetailDto?> GetPlantDetailAsync(
        int plantId,
        decimal lotGdd,
        decimal? currentPh,
        decimal? currentEc,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var plant = await context.Plants
            .Include(p => p.PhenologicalStage)
            .Include(p => p.CommercialStage)
            .FirstOrDefaultAsync(p => p.Id == plantId, ct);

        if (plant is null)
            return null;

        var lastEvaluation = await context.BabyLeafEvaluations
            .Where(e => e.PlantId == plantId)
            .OrderByDescending(e => e.EvaluatedAtUtc)
            .FirstOrDefaultAsync(ct);

        var lastImage = await context.PlantImages
            .Where(i => i.PlantId == plantId)
            .OrderByDescending(i => i.CapturedAtUtc)
            .Select(i => new { i.Path, i.FileName })
            .FirstOrDefaultAsync(ct);

        var stageNames = await context.CommercialStages
            .AsNoTracking()
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var history = (await context.PlantStageHistories
                .AsNoTracking()
                .Where(h => h.PlantId == plantId)
                .OrderByDescending(h => h.ChangedAtUtc)
                .ToListAsync(ct))
            .Select(h => DescribeChange(h, stageNames))
            .ToList();

        var warnings = new List<string>();
        if (lastEvaluation is null)
            warnings.Add("Sin evaluaciones registradas: BabyLeafScore y GrowthRate solo provienen de evaluaciones persistidas (no se inventan).");
        if (lastImage is null)
            warnings.Add("Sin imagen disponible.");

        return new PlantDetailDto(
            plant.Id,
            plant.Row,
            plant.Column,
            plant.PhenologicalStage?.Name,
            plant.CommercialStage?.Name,
            plant.OperationalState,
            plant.HarvestDate,
            plant.DiscardDate,
            plant.DiscardReason,
            lotGdd,
            currentPh,
            currentEc,
            lastEvaluation?.BabyLeafScore,
            lastEvaluation?.GrowthRateUsed,
            lastEvaluation?.Result,
            lastEvaluation?.EvaluatedAtUtc,
            lastImage is not null,
            lastImage?.Path,
            lastImage?.FileName,
            history,
            warnings);
    }

    /// <summary>
    /// Cambio legible: si cambió el estado operativo (cosecha/descarte) se muestra ese
    /// cambio (el comercial queda congelado); si no, los nombres de las etapas comerciales.
    /// </summary>
    private static PlantHistoryEntryDto DescribeChange(PlantStageHistory h, IReadOnlyDictionary<int, string> stageNames)
    {
        string? Stage(int? id) => id is { } value && stageNames.TryGetValue(value, out var name) ? name : null;

        var operationalChanged = h.PreviousOperationalState is not null
            && h.NewOperationalState is not null
            && h.PreviousOperationalState != h.NewOperationalState;

        return operationalChanged
            ? new PlantHistoryEntryDto(h.ChangedAtUtc, h.Source, h.PreviousOperationalState, h.NewOperationalState, h.Reason)
            : new PlantHistoryEntryDto(
                h.ChangedAtUtc,
                h.Source,
                Stage(h.PreviousCommercialStageId) ?? h.PreviousOperationalState,
                Stage(h.NewCommercialStageId) ?? h.NewOperationalState,
                h.Reason);
    }
}