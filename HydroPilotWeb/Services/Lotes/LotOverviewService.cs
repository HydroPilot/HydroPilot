using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Forecasting;

namespace HydroPilotWeb.Services.Lotes;

/// <summary>Opción del selector de lotes (nombre legible + estado).</summary>
public sealed record LotOption(int Id, string Name, string? StatusName, DateOnly SowingDate, bool IsOperational);

/// <summary>Composición de las plantas del lote (las activas por estado comercial).</summary>
public sealed record LotPlantBreakdown(
    int TotalPlants,
    int Active,
    int Apta,
    int Candidate,
    int Developing,
    int Conventional,
    int Risk,
    int Harvested,
    int HarvestedAsBabyLeaf,
    int Discarded,
    int EmptyPositions);

/// <summary>Resultado de un ciclo cerrado (cosechado o descartado), con lo registrado en el lote.</summary>
public sealed record ClosedCycleDto(
    DateOnly? HarvestDate,
    int? CycleDays,
    decimal? ActualYieldKg,
    decimal? YieldPerM2);

/// <summary>
/// Todo lo que muestra la vista Lotes, armado desde los servicios existentes. En un lote
/// cerrado la línea de tiempo y la curva de GDD llegan hasta el cierre (sin proyección) y el
/// rendimiento es el real (HarvestYield), con los pesos y precios guardados al cosechar.
/// </summary>
public sealed record LotOverviewDto(
    LotStateDto State,
    ForecastResult? Forecast,
    CycleTimeline? Timeline,
    GddCurve? Curve,
    LotPlantBreakdown Plants,
    LotObjectives Objectives,
    LotGoalsRules.Result Goals,
    LotYieldEstimate Yield,
    LotHarvestYield? HarvestYield,
    bool IsClosed,
    ClosedCycleDto? ClosedCycle,
    IReadOnlyList<DataQualityAlert> DataAlerts,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Avisos a contar: cada problema una sola vez (alertas + avisos técnicos que no repiten una alerta).</summary>
    public int NoticeCount => DataAlerts.Count + Warnings.Count;
}

public sealed record LotObjectivesUpdateResult(bool Succeeded, IReadOnlyList<string> Errors);

/// <summary>
/// Vista de situación del lote: compone el estado del lote (LotAggregateService), el
/// forecast (ForecastService), la línea de tiempo de ventanas de cosecha, las metas del
/// ciclo. No recalcula GDD: lo consume. El rendimiento por destino
/// sale de plantas × peso de referencia (LotYieldRules), no del kg/m² del forecast.
/// </summary>
public class LotOverviewService
{
    /// <summary>Roles que pueden editar el umbral de cosecha y las metas del ciclo.</summary>
    public static readonly IReadOnlyList<string> ObjectiveEditorRoles = ["Administrador", "Productor"];

    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly LotAggregateService _aggregate;
    private readonly ForecastService _forecast;
    private readonly GddService _gdd;
    private readonly SystemParametersService _parameters;
    private readonly ILogger<LotOverviewService> _logger;

    public LotOverviewService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        LotAggregateService aggregate,
        ForecastService forecast,
        GddService gdd,
        SystemParametersService parameters,
        ILogger<LotOverviewService> logger)
    {
        _dbFactory = dbFactory;
        _aggregate = aggregate;
        _forecast = forecast;
        _gdd = gdd;
        _parameters = parameters;
        _logger = logger;
    }

    public static bool CanEditObjectives(ClaimsPrincipal user) =>
        ObjectiveEditorRoles.Any(user.IsInRole);

    /// <summary>Lotes para el selector: primero los operativos, luego los cerrados; más recientes arriba.</summary>
    public async Task<IReadOnlyList<LotOption>> GetLotOptionsAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await context.Lots
            .AsNoTracking()
            .Select(l => new { l.Id, l.Name, Status = l.Status!.Name, l.SowingDate })
            .ToListAsync(ct);

        return rows
            .Select(r => new LotOption(
                r.Id,
                string.IsNullOrWhiteSpace(r.Name) ? $"Lote #{r.Id}" : r.Name!,
                r.Status,
                r.SowingDate,
                r.Status is LotStatusNames.Activo or LotStatusNames.EnPausa))
            .OrderByDescending(o => o.IsOperational)
            .ThenByDescending(o => o.SowingDate)
            .ToList();
    }

    public async Task<LotOverviewDto?> GetOverviewAsync(int lotId, DateOnly? asOfDate = null, CancellationToken ct = default)
    {
        var warnings = new List<string>();

        // Un ciclo cerrado no necesita pronóstico (sus ventanas se arman con lo medido hasta el cierre).
        var closedStatus = await LotStatusAsync(lotId, ct);
        var lotIsClosed = closedStatus is LotStatusNames.Cosechado or LotStatusNames.Descartado;

        // Forecast primero: con la fecha de hoy refresca el snapshot de GDD que usa el estado del lote.
        ForecastResult? forecast = null;
        if (!lotIsClosed)
        {
            try
            {
                forecast = await _forecast.GetForecastAsync(lotId, asOfDate, ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo calcular el forecast del lote {LotId}", lotId);
                warnings.Add("No se pudo calcular el pronóstico del lote: fechas y rendimiento no disponibles.");
            }
        }

        var state = await _aggregate.GetLotStateAsync(lotId, asOfDate, ct);
        if (state is null)
            return null;

        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var lot = await context.Lots
            .AsNoTracking()
            .Include(l => l.CropType)
            .FirstAsync(l => l.Id == lotId, ct);

        var config = await context.BabyLeafConfigs
            .AsNoTracking()
            .Where(c => c.CropTypeId == lot.CropTypeId && c.IsActive)
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync(ct);

        var lastStage = await context.PhenologicalStages
            .AsNoTracking()
            .Where(s => s.CropTypeId == lot.CropTypeId && s.IsActive)
            .OrderByDescending(s => s.Order)
            .FirstOrDefaultAsync(ct);

        var isClosed = state.StatusName is LotStatusNames.Cosechado or LotStatusNames.Descartado;

        CycleTimeline? timeline = null;
        GddCurve? curve = null;
        if (config is null || lastStage is null)
        {
            if (!isClosed)
                warnings.Add("Sin configuración Baby Leaf o etapas fenológicas del cultivo: no se pueden calcular las ventanas de cosecha.");
        }
        else if (isClosed)
        {
            // Ciclo cerrado: las ventanas y la curva llegan hasta el cierre, solo con lo medido (sin
            // proyección); las ventanas que no se alcanzaron quedan sin fecha.
            var closedOn = await ClosedOnAsync(context, lot, asOfDate, ct);
            var history = await _gdd.GetGddHistoryAsync(lot, closedOn, ct);
            timeline = CycleTimelineLogic.Build(
                lot.SowingDate, closedOn, history.Daily.Sum(p => p.Gdd), history.Daily, [],
                config.GddMin, config.GddMax, lastStage.GddMax);
            curve = GddCurveLogic.Build(lot.SowingDate, closedOn, history.Daily, []);
        }
        else if (forecast is not null)
        {
            timeline = CycleTimelineLogic.Build(
                lot.SowingDate, forecast.AsOfDate, forecast.GddAccumulated,
                forecast.GddHistory, forecast.FutureProjection,
                config.GddMin, config.GddMax, lastStage.GddMax);
            curve = GddCurveLogic.Build(lot.SowingDate, forecast.AsOfDate, forecast.GddHistory, forecast.FutureProjection);
        }

        var harvestedAsBabyLeaf = await context.Plants
            .AsNoTracking()
            .CountAsync(p => p.LotId == lotId
                && p.OperationalState == PlantOperationalState.Cosechada
                && p.CommercialStage != null
                && (p.CommercialStage.Name == CommercialStageNames.BabyLeafApta
                    || p.CommercialStage.Name == CommercialStageNames.CandidataBabyLeaf), ct);

        var plants = Breakdown(state, harvestedAsBabyLeaf);
        var objectives = new LotObjectives(lot.BabyLeafHarvestTargetPercent, lot.BabyLeafGoalPercent, lot.ExpectedActivePercent);
        var goals = LotGoalsRules.Evaluate(new LotGoalsRules.Input(
            plants.TotalPlants, plants.HarvestedAsBabyLeaf, plants.Apta, plants.Candidate,
            plants.Discarded, objectives.BabyLeafGoalPercent, objectives.ExpectedActivePercent));

        // Rendimiento por plantas (cada una tiene su hueco): peso de referencia × plantas de cada destino.
        // El esperado mezcla el histórico del invernadero con la situación del lote según la fase.
        var yieldEstimate = LotYieldRules.Estimate(
            new LotYieldRules.Input(plants.Apta, plants.Candidate, plants.Developing, plants.Conventional,
                plants.Risk, plants.HarvestedAsBabyLeaf, plants.Harvested - plants.HarvestedAsBabyLeaf),
            await _parameters.GetYieldReferenceAsync(ct),
            await SystemParametersService.ResolveLotPricesAsync(context, lot, asOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow), ct),
            isClosed ? null : await YieldHistoryAsync(context, lot, ct),
            LotYieldRules.LotWeight(timeline));

        // Ciclo cerrado: lo cosechado de cada destino, valorizado con los pesos y precios del día de la cosecha.
        LotHarvestYield? harvestYield = null;
        if (isClosed)
        {
            var (reference, prices) = SystemParametersService.HarvestReference(lot);
            harvestYield = LotYieldRules.Harvested(
                plants.HarvestedAsBabyLeaf, plants.Harvested - plants.HarvestedAsBabyLeaf, reference, prices);
        }

        ClosedCycleDto? closedCycle = isClosed
            ? new ClosedCycleDto(
                lot.ActualHarvestDate,
                lot.ActualHarvestDate is { } harvested ? harvested.DayNumber - lot.SowingDate.DayNumber : null,
                lot.ActualYieldKg,
                lot.ActualYieldKg is { } kg && lot.PlantedAreaM2 > 0 ? Math.Round(kg / lot.PlantedAreaM2, 2) : null)
            : null;

        warnings.AddRange(state.Warnings);
        if (forecast is not null)
            warnings.AddRange(forecast.Warnings);

        // Problemas de datos que restan confiabilidad a la fase y las fechas: marcan la sección técnica.
        var dataAlerts = !isClosed && forecast is not null
            ? LotDataQualityRules.Alerts(forecast.SourceKind, forecast.CoverageDays, forecast.MissingDays)
            : [];

        return new LotOverviewDto(
            state,
            forecast,
            timeline,
            curve,
            plants,
            objectives,
            goals,
            yieldEstimate,
            harvestYield,
            isClosed,
            closedCycle,
            dataAlerts,
            LotDataQualityRules.RemainingWarnings(warnings.Distinct(), dataAlerts));
    }

    /// <summary>Guarda el umbral de cosecha y las metas del ciclo (solo Administrador y Productor).</summary>
    public async Task<LotObjectivesUpdateResult> UpdateObjectivesAsync(
        int lotId,
        LotObjectives input,
        ClaimsPrincipal user,
        CancellationToken ct = default)
    {
        if (!CanEditObjectives(user))
            return new LotObjectivesUpdateResult(false, ["Solo Administrador y Productor pueden editar los objetivos del lote."]);

        var errors = LotObjectivesRules.Validate(input);
        if (errors.Count > 0)
            return new LotObjectivesUpdateResult(false, errors);

        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var lot = await context.Lots.FirstOrDefaultAsync(l => l.Id == lotId, ct);
        if (lot is null)
            return new LotObjectivesUpdateResult(false, ["El lote no existe."]);

        var normalized = LotObjectivesRules.Normalize(input);
        lot.BabyLeafHarvestTargetPercent = normalized.HarvestThresholdPercent;
        lot.BabyLeafGoalPercent = normalized.BabyLeafGoalPercent;
        lot.ExpectedActivePercent = normalized.ExpectedActivePercent;
        await context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Objetivos del lote {LotId} actualizados por {User}: umbral {Threshold}, meta Baby Leaf {Goal}, plantas activas esperadas {ExpectedActive}",
            lotId, user.Identity?.Name, normalized.HarvestThresholdPercent, normalized.BabyLeafGoalPercent, normalized.ExpectedActivePercent);

        return new LotObjectivesUpdateResult(true, []);
    }

    private async Task<string?> LotStatusAsync(int lotId, CancellationToken ct)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        return await context.Lots.AsNoTracking().Where(l => l.Id == lotId).Select(l => l.Status!.Name).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Fecha de cierre del ciclo: la cosecha registrada o, si no hay (lote descartado), la última
    /// cosecha o descarte de una planta. Con "Ver al" anterior, esa fecha.
    /// </summary>
    private static async Task<DateOnly> ClosedOnAsync(HydroPilotDbContext context, Lot lot, DateOnly? asOfDate, CancellationToken ct)
    {
        var closedOn = lot.ActualHarvestDate;
        if (closedOn is null)
        {
            var lastHarvest = await context.Plants.Where(p => p.LotId == lot.Id).MaxAsync(p => p.HarvestDate, ct);
            var lastDiscard = await context.Plants.Where(p => p.LotId == lot.Id).MaxAsync(p => p.DiscardDate, ct);
            closedOn = lastHarvest > lastDiscard || lastDiscard is null ? lastHarvest : lastDiscard;
        }

        var date = closedOn ?? asOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        return asOfDate is { } viewDate && viewDate < date ? viewDate : date;
    }

    /// <summary>
    /// Cómo terminaron los lotes cerrados (cosechados o descartados) del mismo invernadero y cultivo:
    /// plantas sembradas, cosechadas como Baby Leaf (apta o candidata al cosecharse) y como convencional.
    /// </summary>
    private static async Task<YieldHistory?> YieldHistoryAsync(HydroPilotDbContext context, Lot lot, CancellationToken ct)
    {
        var closed = await context.Lots
            .AsNoTracking()
            .Where(l => l.Id != lot.Id
                && l.GreenhouseId == lot.GreenhouseId
                && l.CropTypeId == lot.CropTypeId
                && (l.Status!.Name == LotStatusNames.Cosechado || l.Status.Name == LotStatusNames.Descartado))
            .Select(l => new
            {
                l.SowingDate,
                Total = context.Plants.Count(p => p.LotId == l.Id),
                BabyLeaf = context.Plants.Count(p => p.LotId == l.Id
                    && p.OperationalState == PlantOperationalState.Cosechada
                    && p.CommercialStage != null
                    && (p.CommercialStage.Name == CommercialStageNames.BabyLeafApta
                        || p.CommercialStage.Name == CommercialStageNames.CandidataBabyLeaf)),
                Harvested = context.Plants.Count(p => p.LotId == l.Id && p.OperationalState == PlantOperationalState.Cosechada)
            })
            .ToListAsync(ct);

        return LotYieldHistoryRules.Build(lot.SowingDate,
            closed.Select(l => new LotYieldHistoryRules.ClosedLot(l.SowingDate, l.Total, l.BabyLeaf, l.Harvested - l.BabyLeaf)));
    }

    private static LotPlantBreakdown Breakdown(LotStateDto state, int harvestedAsBabyLeaf)
    {
        int Count(string stage) => state.CommercialCounts.FirstOrDefault(c => c.StageName == stage)?.Count ?? 0;

        var apta = Count(CommercialStageNames.BabyLeafApta);
        var candidate = Count(CommercialStageNames.CandidataBabyLeaf);
        var conventional = Count(CommercialStageNames.CosechaConvencional);
        var risk = Count(CommercialStageNames.RiesgoFueraDeVentana);
        // Resto de las activas (en desarrollo o sin estado): todavía sin destino comercial.
        var developing = Math.Max(0, state.ActivePlants - apta - candidate - conventional - risk);

        return new LotPlantBreakdown(
            state.TotalPlants, state.ActivePlants, apta, candidate, developing, conventional, risk,
            state.HarvestedPlants, harvestedAsBabyLeaf, state.DiscardedPlants, state.EmptyPositions);
    }
}
