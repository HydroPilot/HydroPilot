using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Services.Simulation;

/// <summary>
/// Orquesta el escenario what-if (SIM-02): recibe el <see cref="SimulationRequest"/> y devuelve
/// UN único <see cref="SimulationResult"/> (la vista y la API consumen el mismo resultado).
///
/// Usa la lógica de la vista Lotes (gana Lotes):
/// <list type="number">
/// <item>GDD acumulado hasta hoy: lecturas reales del lote o, en el escenario libre, el clima
/// elegido desde la siembra (siembra futura: 0, todavía no se sembró).</item>
/// <item>Proyección con el clima del escenario (manual / pronóstico) desde mañana, o desde la
/// siembra si es futura.</item>
/// <item>Ventanas y óptimos de corte por GDD con <see cref="CycleTimelineLogic"/>.</item>
/// <item>Previsión de aptas a Baby Leaf y pérdidas por etapa (<see cref="BabyLeafOutlookService"/>:
/// histórico pesado por temporada × clima, combinado con el estado actual del lote).</item>
/// <item>Estrategias (máximo Baby Leaf / meta Baby Leaf / todo convencional) con plantas × peso ×
/// precio y costos del ciclo (<see cref="SimulationStrategies"/>).</item>
/// </list>
/// SOLO LECTURA sobre el dominio productivo y nunca invoca hardware (ver <see cref="IHardwareGateway"/>).
/// SIM-06: no persiste escenarios (preview).
/// </summary>
public class SimulationService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly GddService _gddService;
    private readonly ClimateScenarioProvider _climateProvider;
    private readonly LotAggregateService _lotAggregate;
    private readonly SystemParametersService _parameters;
    private readonly BabyLeafOutlookService _outlook;

    public SimulationService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        GddService gddService,
        ClimateScenarioProvider climateProvider,
        LotAggregateService lotAggregate,
        SystemParametersService parameters,
        BabyLeafOutlookService outlook)
    {
        _dbFactory = dbFactory;
        _gddService = gddService;
        _climateProvider = climateProvider;
        _lotAggregate = lotAggregate;
        _parameters = parameters;
        _outlook = outlook;
    }

    /// <summary>
    /// Opciones de contexto para los selectores de la UI y los parámetros del sistema (solo lectura).
    /// Solo lotes abiertos: uno cerrado no tiene plantas activas que simular.
    /// </summary>
    public async Task<SimulationContextOptions> GetContextOptionsAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var lots = await context.Lots
            .AsNoTracking()
            .Where(l => l.Status == null || (l.Status.Name != LotStatusNames.Cosechado && l.Status.Name != LotStatusNames.Descartado))
            .OrderByDescending(l => l.SowingDate)
            .Select(l => new SimulationLotOption(
                l.Id,
                l.Name ?? $"Lote {l.Id}",
                l.CropType!.Name,
                l.Status!.Name,
                l.SowingDate,
                l.Plants.Count,
                l.Plants.Count(p => p.OperationalState == PlantOperationalState.Activa),
                l.BabyLeafGoalPercent))
            .ToListAsync(ct);

        var crops = await context.CropTypes
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new SimulationCropOption(c.Id, c.Name, c.BaseTemperature))
            .ToListAsync(ct);

        return new SimulationContextOptions(
            lots, crops, await _parameters.GetYieldParametersAsync(ct), await _parameters.GetNutrientPricePerLiterAsync(ct));
    }

    /// <summary>
    /// Simulación preview (SIM-06, sin persistencia), siempre desde hoy. Devuelve validación +
    /// resultado. La misma entrada el mismo día produce el mismo resultado, salvo que cambien las
    /// lecturas, el pronóstico guardado o los parámetros del sistema entre corridas.
    /// </summary>
    public async Task<SimulationPreview> PreviewAsync(SimulationRequest request, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var errors = SimulationRequestValidator.Validate(request, today);
        if (errors.Count > 0)
            return new SimulationPreview(false, errors, null);

        var warnings = new List<string>();

        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        // --- Contexto: lote o cultivo (SIM-01) ---
        Lot? lot = null;
        if (request.LotId is { } lotId)
        {
            lot = await context.Lots
                .AsNoTracking()
                .Include(l => l.CropType)
                .Include(l => l.Status)
                .FirstOrDefaultAsync(l => l.Id == lotId, ct);

            if (lot is null)
                return new SimulationPreview(false, [$"Lote '{lotId}' no encontrado."], null);
            if (lot.Status?.Name is LotStatusNames.Cosechado or LotStatusNames.Descartado)
                return new SimulationPreview(false, [$"El lote '{lot.Name}' está cerrado: no tiene plantas activas que simular."], null);
        }

        var crop = lot?.CropType;
        if (crop is null && request.CropTypeId is { } cropId)
            crop = await context.CropTypes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cropId, ct);

        if (crop is null)
            return new SimulationPreview(false, ["Cultivo no encontrado para el contexto del escenario."], null);

        var sowingDate = lot?.SowingDate ?? request.SowingDate!.Value;
        var baseTemperature = crop.BaseTemperature == 0 ? 4.5m : crop.BaseTemperature;

        // Sin lote, el pronóstico (afuera) se corrige con lo que midió el invernadero en los últimos
        // días (con lote, GddService lo corrige con las lecturas del propio lote, como en Lotes).
        var freeCorrection = lot is null && request.Climate.Mode == SimulationClimateMode.Forecast
            ? await _gddService.GetForecastCorrectionAsync(null, today, ct: ct)
            : null;

        // --- 1. GDD hasta hoy (SIM-04): el medido del lote, o el clima elegido desde la siembra ---
        IReadOnlyList<DailyGddPoint> history;
        GddHistoryResult? measured = null;
        if (lot is not null)
        {
            measured = await _gddService.GetGddHistoryAsync(lot, today, ct);
            history = measured.Daily;
            warnings.AddRange(measured.Warnings);
        }
        else
        {
            (history, var freeWarnings) = await _climateProvider.FreeHistoryAsync(
                request.Climate, baseTemperature, sowingDate, today, freeCorrection, ct);
            warnings.AddRange(freeWarnings);
        }
        var accumulated = Math.Round(history.Sum(p => p.Gdd), 2);

        // --- 2. Clima del escenario (SIM-03): desde mañana, o desde la siembra si es futura ---
        var plan = await _climateProvider.BuildAsync(
            request.Climate, lot, GddCalculator.ProjectionStart(sowingDate, today), baseTemperature, measured, freeCorrection, ct);
        warnings.AddRange(plan.Warnings);
        var projection = GddCalculator.ToCorePoints(plan.Points);

        // --- 3. Ventanas y óptimos de corte: la misma línea de tiempo que la vista Lotes ---
        var config = await context.BabyLeafConfigs
            .AsNoTracking()
            .Where(c => c.CropTypeId == crop.Id && c.IsActive)
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync(ct);
        var lastStage = await context.PhenologicalStages
            .AsNoTracking()
            .Where(s => s.CropTypeId == crop.Id && s.IsActive)
            .OrderByDescending(s => s.Order)
            .FirstOrDefaultAsync(ct);

        CycleTimeline? timeline = null;
        if (config is null || lastStage is null)
        {
            warnings.Add("Sin configuración Baby Leaf o etapas fenológicas del cultivo: no se pueden calcular las ventanas ni los cortes.");
        }
        else
        {
            timeline = CycleTimelineLogic.Build(
                sowingDate, today, accumulated, history, projection,
                config.GddMin, config.GddMax, lastStage.GddMax);
        }
        var curve = GddCurveLogic.Build(sowingDate, today, history, projection);

        // --- 4. Plantas que se simulan: las activas del lote o las del escenario libre ---
        var plants = lot is not null
            ? await context.Plants.CountAsync(p => p.LotId == lot.Id && p.OperationalState == PlantOperationalState.Activa, ct)
            : request.PlantCount!.Value;
        if (lot is not null && plants == 0)
            warnings.Add("El lote no tiene plantas activas: no queda nada por cosechar en el escenario.");

        // Semillas = todas las plantas del lote (o las del escenario libre). Las que ya se cosecharon o
        // descartaron consumieron agua y nutrientes hasta ese día.
        var seeds = plants;
        IReadOnlyList<DateOnly> exitedPlantDates = [];
        if (lot is not null)
        {
            var lotPlants = await context.Plants.AsNoTracking()
                .Where(p => p.LotId == lot.Id)
                .Select(p => new { p.OperationalState, p.HarvestDate, p.DiscardDate })
                .ToListAsync(ct);
            seeds = lotPlants.Count;
            exitedPlantDates = lotPlants
                .Where(p => p.OperationalState != PlantOperationalState.Activa)
                .Select(p => p.HarvestDate ?? p.DiscardDate ?? today)
                .ToList();
        }

        // --- 5. Pesos y precios: los del sistema (precio propio del lote si lo tiene), o los del escenario ---
        var reference = await YieldReferenceAsync(context, lot, crop, request, today, ct);
        if (reference.Weights.BabyLeafGramsPerPlant is null || reference.Weights.ConventionalGramsPerPlant is null)
            warnings.Add("Falta el peso por planta de algún destino (Ajustes > Parámetros o el escenario): esos kg quedan sin calcular.");
        if (reference.Prices.BabyLeafPerKg is null || reference.Prices.ConventionalPerKg is null)
            warnings.Add("Falta el precio por kg de algún destino (Ajustes > Parámetros o el escenario): esos ingresos quedan sin calcular.");

        // --- 6. Previsión: aptas a Baby Leaf y pérdidas por etapa (histórico + estado del lote) ---
        var outlook = BabyLeafOutlook.None;
        if (timeline is not null)
        {
            // Clima esperado del ciclo para comparar con los lotes previos: lo que va (medido o del
            // escenario) más la proyección.
            var cycleDays = history.Where(p => p.Source != GddPointSource.Fallback).Select(p => p.Gdd)
                .Concat(plan.Points.Select(p => p.Gdd)).ToList();
            var expectedAverage = cycleDays.Count > 0 ? Math.Round(cycleDays.Average(), 2) : (decimal?)null;
            outlook = await _outlook.GetOutlookAsync(
                new BabyLeafOutlookRequest(crop.Id, sowingDate, timeline, expectedAverage, lot?.Id), ct);

            if (outlook.AptRate is null)
                warnings.Add("Sin lotes cerrados con datos ni evaluaciones del lote: se asume que todas las plantas llegan a Baby Leaf.");
            if (outlook.History.EarlyLossRate is null && outlook.History.LateLossRate is null)
                warnings.Add("Sin historial de pérdidas por etapa: no se descuentan pérdidas.");
            if (outlook.History.UsedAllLots)
                warnings.Add($"Ningún lote cerrado es de una temporada o clima parecido: la previsión usa el promedio de los {outlook.History.Lots.Count}.");
        }

        // Precio de nutrientes: el del escenario o, vacío, el de Ajustes > Parámetros.
        var costs = request.Costs.NutrientPricePerLiter is null
            ? request.Costs with { NutrientPricePerLiter = await _parameters.GetNutrientPricePerLiterAsync(ct) }
            : request.Costs;

        // --- 7. Estrategias: máximo Baby Leaf / meta Baby Leaf / todo convencional ---
        IReadOnlyList<SimulationStrategyOutcome> strategies = timeline is null
            ? []
            : SimulationStrategies.Build(new SimulationStrategies.Input(
                sowingDate, today, timeline, plants, request.Harvest.BabyLeafGoalPercent,
                outlook.AptRate, outlook.RemainingEarlyLossRate, outlook.RemainingLateLossRate,
                reference.Weights, reference.Prices, costs, seeds, exitedPlantDates));

        // --- Contexto de lote + grilla (SIM-08/10, solo lectura) ---
        SimulationLotInfo? lotInfo = null;
        if (lot is not null && await _lotAggregate.GetLotStateAsync(lot.Id, today, ct) is { } lotState)
        {
            lotInfo = new SimulationLotInfo(
                lotState.LotId,
                lotState.PhenologicalStageName,
                lotState.GridRows ?? 0,
                lotState.GridColumns ?? 0,
                lotState.TotalPlants,
                lotState.ActivePlants,
                lotState.HarvestedPlants,
                lotState.DiscardedPlants,
                lotState.Cells);
        }

        var result = new SimulationResult(
            SimulationId: ComputeSimulationId(request, today),
            Request: request,
            Today: today,
            IsSimulated: true,
            Context: new SimulationContextInfo(
                lot is not null ? "lote" : "cultivo",
                lot?.Id,
                lot?.Name,
                crop.Name,
                sowingDate,
                plants,
                baseTemperature),
            Lot: lotInfo,
            Gdd: new SimulationGdd(
                accumulated,
                baseTemperature,
                plan.Points,
                plan.HorizonDays,
                plan.CoveredDays,
                plan.MissingDays,
                plan.CoveragePercent,
                plan.ProviderName,
                plan.FetchedAtUtc,
                plan.UsedFallback,
                plan.Warnings,
                plan.Correction),
            Timeline: timeline,
            Curve: curve,
            Reference: reference,
            Outlook: outlook,
            Strategies: strategies,
            Warnings: warnings.Concat(strategies.SelectMany(s => s.Warnings)).Distinct().ToList());

        return new SimulationPreview(true, [], result);
    }

    /// <summary>
    /// Pesos y precios del escenario: lo que cargó el usuario y, para lo vacío, los de Ajustes &gt;
    /// Parámetros (con el precio propio del lote, si lo tiene, vigente hoy).
    /// </summary>
    private async Task<SimulationYieldReference> YieldReferenceAsync(
        HydroPilotDbContext context, Lot? lot, CropType crop, SimulationRequest request, DateOnly today, CancellationToken ct)
    {
        var system = await _parameters.GetYieldReferenceAsync(ct);
        var prices = await SystemParametersService.ResolveLotPricesAsync(
            context, lot ?? new Lot { CropTypeId = crop.Id }, today, ct);
        var overrides = request.Yield;

        return new SimulationYieldReference(
            new YieldReference(
                overrides?.BabyLeafGramsPerPlant ?? system.BabyLeafGramsPerPlant,
                overrides?.ConventionalGramsPerPlant ?? system.ConventionalGramsPerPlant),
            prices with
            {
                BabyLeafPerKg = overrides?.BabyLeafPricePerKg ?? prices.BabyLeafPerKg,
                ConventionalPerKg = overrides?.ConventionalPricePerKg ?? prices.ConventionalPerKg,
            },
            HasScenarioOverrides: overrides is { } o
                && (o.BabyLeafGramsPerPlant is not null || o.ConventionalGramsPerPlant is not null
                    || o.BabyLeafPricePerKg is not null || o.ConventionalPricePerKg is not null));
    }

    /// <summary>
    /// Id determinista del escenario (SIM: misma entrada el mismo día → mismo id y mismo
    /// resultado). Hash SHA-256 del request serializado y de hoy (el escenario arranca hoy);
    /// no identifica runs persistidos (no los hay en esta entrega).
    /// </summary>
    public static string ComputeSimulationId(SimulationRequest request, DateOnly today)
    {
        var json = JsonSerializer.Serialize(new { request, today });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash)[..16];
    }
}
