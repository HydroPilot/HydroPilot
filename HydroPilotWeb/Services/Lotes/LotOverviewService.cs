using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Optimization;
using HydroPilotWeb.Services.Dashboard;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Optimization;

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

/// <summary>
/// pH o CE de la solución: última lectura usable del sensor del lote o, si no hay,
/// el valor manual guardado en el lote (IsManualValue). La banda sale del cultivo
/// (pH) o de la etapa aplicada (CE).
/// </summary>
public sealed record SolutionReadingDto(
    string SensorType,
    decimal? Value,
    string? Unit,
    DateTime? ObservedAtUtc,
    bool IsFresh,
    bool IsManualValue,
    decimal? BandMin,
    decimal? BandMax,
    bool? InBand);

/// <summary>Resultado de un ciclo cerrado (cosechado o descartado), con lo registrado en el lote.</summary>
public sealed record ClosedCycleDto(
    DateOnly? HarvestDate,
    int? CycleDays,
    decimal? ActualYieldKg,
    decimal? YieldPerM2);

/// <summary>
/// Todo lo que muestra la vista Lotes, armado desde los servicios existentes. Un lote
/// cerrado no tiene línea de tiempo (sus ventanas ya pasaron): expone ClosedCycle.
/// </summary>
public sealed record LotOverviewDto(
    LotStateDto State,
    ForecastResult? Forecast,
    CycleTimeline? Timeline,
    LotPlantBreakdown Plants,
    LotObjectives Objectives,
    LotGoalsRules.Result Goals,
    LotYieldEstimate Yield,
    IReadOnlyList<SolutionReadingDto> Solution,
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
/// ciclo y la solución nutritiva. No recalcula GDD: lo consume. El rendimiento por destino
/// sale de plantas × peso de referencia (LotYieldRules), no del kg/m² del forecast.
/// </summary>
public class LotOverviewService
{
    /// <summary>Roles que pueden editar el umbral de cosecha y las metas del ciclo.</summary>
    public static readonly IReadOnlyList<string> ObjectiveEditorRoles = ["Administrador", "Productor"];

    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly LotAggregateService _aggregate;
    private readonly ForecastService _forecast;
    private readonly SystemParametersService _parameters;
    private readonly IOptions<DashboardOptions> _dashboardOptions;
    private readonly ILogger<LotOverviewService> _logger;

    public LotOverviewService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        LotAggregateService aggregate,
        ForecastService forecast,
        SystemParametersService parameters,
        IOptions<DashboardOptions> dashboardOptions,
        ILogger<LotOverviewService> logger)
    {
        _dbFactory = dbFactory;
        _aggregate = aggregate;
        _forecast = forecast;
        _parameters = parameters;
        _dashboardOptions = dashboardOptions;
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

        // Forecast primero: con la fecha de hoy refresca el snapshot de GDD que usa el estado del lote.
        ForecastResult? forecast = null;
        try
        {
            forecast = await _forecast.GetForecastAsync(lotId, asOfDate, ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo calcular el forecast del lote {LotId}", lotId);
            warnings.Add("No se pudo calcular el pronóstico del lote: fechas y rendimiento no disponibles.");
        }

        var state = await _aggregate.GetLotStateAsync(lotId, asOfDate, ct);
        if (state is null)
            return null;

        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var lot = await context.Lots
            .AsNoTracking()
            .Include(l => l.CropType)
            .Include(l => l.AppliedPhenologicalStage)
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

        // Un ciclo cerrado no lleva línea de tiempo: sus ventanas ya pasaron (se muestra ClosedCycle).
        CycleTimeline? timeline = null;
        if (!isClosed)
        {
            if (config is null || lastStage is null)
            {
                warnings.Add("Sin configuración Baby Leaf o etapas fenológicas del cultivo: no se pueden calcular las ventanas de cosecha.");
            }
            else if (forecast is not null)
            {
                timeline = CycleTimelineLogic.Build(
                    lot.SowingDate, forecast.AsOfDate, forecast.GddAccumulated,
                    forecast.GddHistory, forecast.FutureProjection,
                    config.GddMin, config.GddMax, lastStage.GddMax);
            }
        }

        var harvestedAsBabyLeaf = await context.Plants
            .AsNoTracking()
            .CountAsync(p => p.LotId == lotId
                && p.OperationalState == PlantOperationalState.Cosechada
                && p.CommercialStage != null
                && (p.CommercialStage.Name == CommercialStageNames.BabyLeafApta
                    || p.CommercialStage.Name == CommercialStageNames.CandidataBabyLeaf), ct);

        var plants = Breakdown(state, harvestedAsBabyLeaf);
        var objectives = new LotObjectives(lot.BabyLeafHarvestTargetPercent, lot.BabyLeafGoalPercent, lot.MaxLossPercent);
        var goals = LotGoalsRules.Evaluate(new LotGoalsRules.Input(
            plants.TotalPlants, plants.HarvestedAsBabyLeaf, plants.Apta, plants.Candidate,
            plants.Discarded, plants.Risk, objectives.BabyLeafGoalPercent, objectives.MaxLossPercent));

        var solution = await GetSolutionAsync(context, lot, asOfDate, ct);

        // Rendimiento por plantas (cada una tiene su hueco): peso de referencia × plantas de cada destino.
        var yieldEstimate = LotYieldRules.Estimate(
            new LotYieldRules.Input(plants.Apta, plants.Candidate, plants.Developing, plants.Conventional),
            await _parameters.GetYieldReferenceAsync(ct),
            await GetYieldPricesAsync(context, lot, asOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow), ct));

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
            plants,
            objectives,
            goals,
            yieldEstimate,
            solution,
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
        lot.MaxLossPercent = normalized.MaxLossPercent;
        await context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Objetivos del lote {LotId} actualizados por {User}: umbral {Threshold}, meta Baby Leaf {Goal}, pérdidas máximas {MaxLoss}",
            lotId, user.Identity?.Name, normalized.HarvestThresholdPercent, normalized.BabyLeafGoalPercent, normalized.MaxLossPercent);

        return new LotObjectivesUpdateResult(true, []);
    }

    /// <summary>
    /// Precio por kg vigente de cada destino, con la misma precedencia que Optimización pero sin
    /// los precios demo: si el usuario no cargó uno, queda sin definir.
    /// </summary>
    private static async Task<YieldPrices> GetYieldPricesAsync(
        HydroPilotDbContext context, Lot lot, DateOnly date, CancellationToken ct)
    {
        var rows = (await CostPriceResolver.ResolveActiveAsync(context, lot, date, ct, includeDemo: false))
            .Where(r => r.Item == OptimizationContractCostItem.PricePerKg)
            .ToList();
        if (rows.Count == 0)
            return YieldPrices.None;

        var babyLeaf = rows.FirstOrDefault(r => r.Destination == OptimizationContractDestination.BabyLeaf);
        var conventional = rows.FirstOrDefault(r => r.Destination == OptimizationContractDestination.Conventional);
        return new YieldPrices(babyLeaf?.Value, conventional?.Value, (babyLeaf ?? conventional)!.Currency);
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

    private async Task<IReadOnlyList<SolutionReadingDto>> GetSolutionAsync(
        HydroPilotDbContext context, Lot lot, DateOnly? asOfDate, CancellationToken ct)
    {
        // Con fecha simulada se toma la última lectura hasta el fin de ese día.
        var reference = asOfDate is { } date
            ? date.ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Utc)
            : DateTime.UtcNow;
        var freshness = TimeSpan.FromMinutes(Math.Max(1, _dashboardOptions.Value.KpiFreshnessMinutes));

        var ecMin = lot.AppliedPhenologicalStage?.EcMin ?? lot.CropType?.OptimalEcMin;
        var ecMax = lot.AppliedPhenologicalStage?.EcMax ?? lot.CropType?.OptimalEcMax;

        return
        [
            await ReadingAsync(AnomalyContract.SensorTypePh, lot.CurrentPh, lot.CropType?.OptimalPhMin, lot.CropType?.OptimalPhMax),
            await ReadingAsync(AnomalyContract.SensorTypeCe, lot.CurrentEc, ecMin, ecMax),
        ];

        async Task<SolutionReadingDto> ReadingAsync(string sensorType, decimal? manualValue, decimal? min, decimal? max)
        {
            var latest = await context.SensorReadings
                .AsNoTracking()
                .Where(TelemetryQualityPolicy.OperationallyUsableReading)
                .Where(r => r.LotId == lot.Id
                    && r.ObservedAtUtc <= reference
                    && r.Sensor != null && r.Sensor.SensorType != null
                    && r.Sensor.SensorType.Name == sensorType)
                .OrderByDescending(r => r.ObservedAtUtc)
                .Select(r => new { r.Value, Unit = r.MeasurementUnit != null ? r.MeasurementUnit.Symbol : null, r.ObservedAtUtc })
                .FirstOrDefaultAsync(ct);

            var value = latest?.Value ?? manualValue;
            bool? inBand = value is { } v && min is { } lo && max is { } hi ? v >= lo && v <= hi : null;

            return new SolutionReadingDto(
                sensorType,
                value,
                latest?.Unit,
                latest?.ObservedAtUtc,
                IsFresh: latest is not null && reference - latest.ObservedAtUtc <= freshness,
                IsManualValue: latest is null && manualValue is not null,
                min,
                max,
                inBand);
        }
    }
}
