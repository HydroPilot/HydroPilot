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
/// Orquesta el escenario what-if (SIM-02): un servicio que recibe el
/// <see cref="SimulationRequest"/> y devuelve UN único <see cref="SimulationResult"/>
/// (la vista y la API consumen el mismo resultado; la vista no accede a EF).
///
/// La simulación es SOLO LECTURA sobre el dominio productivo: nunca modifica
/// lotes, plantas, lecturas, predicciones, nodos ni configuración de cultivo, y
/// nunca invoca hardware (ver <see cref="IHardwareGateway"/> — frontera no usada).
/// Respeta AsOfDate (ReferenceDate) en toda consulta simulada (SIM-04) y reutiliza
/// el núcleo de forecasting (GddService/GddDateLogic/YieldService/YieldMath) sin
/// copiar fórmulas. SIM-06: esta entrega NO persiste escenarios (preview).
/// </summary>
public class SimulationService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly GddService _gddService;
    private readonly ClimateScenarioProvider _climateProvider;
    private readonly YieldModel _yieldModel;
    private readonly LotAggregateService _lotAggregate;

    public SimulationService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        GddService gddService,
        ClimateScenarioProvider climateProvider,
        YieldModel yieldModel,
        LotAggregateService lotAggregate)
    {
        _dbFactory = dbFactory;
        _gddService = gddService;
        _climateProvider = climateProvider;
        _yieldModel = yieldModel;
        _lotAggregate = lotAggregate;
    }

    /// <summary>Opciones de contexto para los selectores de la UI (solo lectura).</summary>
    public async Task<SimulationContextOptions> GetContextOptionsAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var lots = await context.Lots
            .Include(l => l.CropType)
            .Include(l => l.Status)
            .OrderByDescending(l => l.SowingDate)
            .Select(l => new SimulationLotOption(
                l.Id, l.Name ?? $"Lote {l.Id}", l.CropType!.Name, l.Status!.Name, l.SowingDate, l.PlantedAreaM2, l.BabyLeafHarvestTargetPercent))
            .ToListAsync(ct);

        var crops = await context.CropTypes
            .OrderBy(c => c.Name)
            .Select(c => new SimulationCropOption(c.Id, c.Name, c.GddTarget, c.BaseTemperature, c.YieldPerM2))
            .ToListAsync(ct);

        return new SimulationContextOptions(lots, crops);
    }

    /// <summary>
    /// Simulación preview (SIM-06, sin persistencia). Devuelve validación + resultado.
    /// La misma entrada produce el mismo resultado (determinismo), salvo que el
    /// pronóstico climático persistido cambie entre corridas (origen informado).
    /// </summary>
    public async Task<SimulationPreview> PreviewAsync(SimulationRequest request, CancellationToken ct = default)
    {
        var errors = SimulationRequestValidator.Validate(request);
        if (errors.Count > 0)
        {
            return new SimulationPreview(false, errors, null);
        }

        var warnings = new List<string>();
        var realToday = DateOnly.FromDateTime(DateTime.UtcNow);

        if (request.SaveScenario)
        {
            warnings.Add("La persistencia de escenarios (SimulationRun) no está disponible en esta entrega: el preview no guarda nada (SIM-06).");
        }

        if (request.ReferenceDate > realToday.AddDays(1))
        {
            warnings.Add($"Fecha de referencia futura ({request.ReferenceDate:yyyy-MM-dd}): entorno simulado; las lecturas se limitan a esa fecha (AsOfDate) y el pronóstico puede no cubrirla.");
        }

        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        // --- Contexto: lote o cultivo (SIM-01) ---
        Lot? lot = null;
        if (request.LotId is { } lotId)
        {
            lot = await context.Lots
                .Include(l => l.CropType)
                .Include(l => l.Status)
                .Include(l => l.AppliedPhenologicalStage)
                .FirstOrDefaultAsync(l => l.Id == lotId, ct);

            if (lot is null)
            {
                return new SimulationPreview(false, [$"Lote '{lotId}' no encontrado."], null);
            }
        }

        var crop = lot?.CropType;
        if (crop is null && request.CropTypeId is { } cropId)
        {
            crop = await context.CropTypes.FirstOrDefaultAsync(c => c.Id == cropId, ct);
        }

        if (crop is null)
        {
            return new SimulationPreview(false, ["Cultivo no encontrado para el contexto del escenario."], null);
        }

        var sowingDate = lot?.SowingDate ?? request.SowingDate!.Value;
        var areaM2 = lot?.PlantedAreaM2 ?? request.AreaM2!.Value;
        var baseTemperature = crop.BaseTemperature == 0 ? 4.5m : crop.BaseTemperature;
        var gddTarget = crop.GddTarget;

        // --- GDD base respetando AsOfDate (SIM-04): nunca lecturas posteriores ---
        decimal accumulated;
        if (lot is not null)
        {
            var history = await _gddService.GetGddHistoryAsync(lot, request.ReferenceDate, ct);
            accumulated = history.Daily.Sum(p => p.Gdd);
            warnings.AddRange(history.Warnings);
        }
        else
        {
            (accumulated, var accWarnings) = GddCalculator.AccumulatedManualScenario(
                request.Climate, baseTemperature, sowingDate, request.ReferenceDate);
            warnings.AddRange(accWarnings);
        }

        // --- Plan climático del escenario (SIM-03) ---
        var plan = await _climateProvider.BuildAsync(request.Climate, lot, request.ReferenceDate, baseTemperature, ct);
        warnings.AddRange(plan.Warnings);

        // --- Fecha estimada por GDD puro (SIM-04) ---
        var (harvestDate, extrapolation, harvestWarnings) = GddCalculator.EstimateHarvestDate(
            accumulated, plan.Points, gddTarget, request.ReferenceDate, sowingDate,
            crop.EstimatedDaysToHarvest, plan.HorizonDays);
        warnings.AddRange(harvestWarnings);

        // --- Rendimiento conservador/base/optimista (SIM-04, núcleo forecasting) ---
        var (yieldEstimate, yieldWarnings) = await _yieldModel.EstimateAsync(lot, crop, accumulated, areaM2, ct);
        warnings.AddRange(yieldWarnings);

        // --- Costos (SIM-05) ---
        var projectedCostDays = CostCalculator.ResolveProjectedDays(
            request.ReferenceDate, harvestDate, plan.HorizonDays, warnings);
        var costs = CostCalculator.Calculate(request.Costs, projectedCostDays);
        costs = costs with { CostPerKg = CostCalculator.CostPerKg(costs.Total, yieldEstimate.Base) };
        warnings.AddRange(costs.Warnings);

        // --- Regla híbrida de cosecha simulada (SIM-08/09) ---
        var harvestOutcome = await BuildHarvestOutcomeAsync(
            context, lot, crop, request, accumulated, plan.Points, ct);
        if (harvestOutcome is not null)
        {
            warnings.AddRange(harvestOutcome.Warnings);
        }

        // --- Comparación de estrategias (SIM-09) ---
        var (realAptaCount, totalActive) = await AptaCountsAsync(context, lot, ct);
        var alternatives = AlternativeComparisonService.Compare(new AlternativeComparisonService.Context(
            request.ReferenceDate,
            accumulated,
            plan.Points,
            plan.HorizonDays,
            crop.Name,
            gddTarget,
            await BabyLeafWindowAsync(context, crop, request.Harvest.EnableBabyLeaf, ct),
            realAptaCount,
            totalActive,
            request.Harvest.HypothesisAptaPercent,
            request.Harvest.RequiredTargetPercent ?? lot?.BabyLeafHarvestTargetPercent,
            await ConventionalHasMaturityDataAsync(context, lot, ct),
            request.Harvest.EnableBabyLeaf,
            request.Harvest.EnableConvencional));
        warnings.AddRange(alternatives.Warnings);

        // --- Contexto de lote + grilla opcional (SIM-08/10, solo lectura) ---
        SimulationLotInfo? lotInfo = null;
        if (lot is not null)
        {
            var lotState = await _lotAggregate.GetLotStateAsync(lot.Id, request.ReferenceDate, ct);
            if (lotState is not null)
            {
                warnings.AddRange(lotState.Warnings);
                lotInfo = new SimulationLotInfo(
                    lotState.LotId,
                    lotState.Name,
                    lotState.CropTypeName,
                    lotState.SowingDate,
                    lotState.AreaM2,
                    Math.Round(accumulated, 2),
                    lotState.PhenologicalStageName,
                    lotState.PhenologicalStageOrder,
                    lotState.GridRows ?? 0,
                    lotState.GridColumns ?? 0,
                    lotState.TotalPlants,
                    lotState.ActivePlants,
                    lotState.HarvestedPlants,
                    lotState.DiscardedPlants,
                    lotState.EmptyPositions,
                    lotState.Cells);
            }
        }

        var contextInfo = new SimulationContextInfo(
            lot is not null ? "lote" : "cultivo",
            lot?.Id,
            lot?.Name,
            crop.Name,
            sowingDate,
            areaM2,
            baseTemperature,
            gddTarget);

        var result = new SimulationResult(
            SimulationId: ComputeSimulationId(request),
            Request: request,
            IsSimulated: true,
            Context: contextInfo,
            Lot: lotInfo,
            Gdd: new SimulationGdd(
                Math.Round(accumulated, 2),
                gddTarget,
                baseTemperature,
                plan.Points,
                plan.HorizonDays,
                plan.CoveredDays,
                plan.MissingDays,
                plan.CoveragePercent,
                harvestDate,
                GddCalculator.DaysRemaining(request.ReferenceDate, harvestDate),
                plan.ProviderName,
                plan.FetchedAtUtc,
                plan.UsedFallback,
                extrapolation,
                plan.Warnings.Concat(harvestWarnings).Distinct().ToList()),
            Yield: yieldEstimate,
            Harvest: harvestOutcome,
            Alternatives: alternatives,
            Costs: costs,
            Warnings: warnings.Distinct().ToList());

        return new SimulationPreview(true, [], result);
    }

    /// <summary>
    /// Regla híbrida de cosecha simulada (SIM-09): la MISMA regla de producción
    /// (HarvestReadinessRules) con el % de aptas REAL de evaluaciones o la
    /// HIPÓTESIS explícita del usuario. Nunca se inventa una evaluación visual.
    /// </summary>
    private async Task<SimulationHarvestOutcome?> BuildHarvestOutcomeAsync(
        HydroPilotDbContext context,
        Lot? lot,
        CropType crop,
        SimulationRequest request,
        decimal accumulated,
        IReadOnlyList<SimulationGddPoint> projection,
        CancellationToken ct)
    {
        if (!request.Harvest.EnableBabyLeaf && !request.Harvest.EnableConvencional)
        {
            return null;
        }

        var warnings = new List<string>();
        (decimal Min, decimal Max)? window = null;

        if (request.Harvest.EnableBabyLeaf)
        {
            var config = await context.BabyLeafConfigs
                .Where(c => c.CropTypeId == crop.Id && c.IsActive)
                .OrderByDescending(c => c.Id)
                .FirstOrDefaultAsync(ct);

            if (config is null)
            {
                warnings.Add("Sin configuración Baby Leaf activa para el cultivo: no se puede evaluar la ventana GDD ni la fecha híbrida Baby Leaf.");
            }
            else
            {
                window = (config.GddMin, config.GddMax);
            }
        }

        var requiredPercent = request.Harvest.RequiredTargetPercent ?? lot?.BabyLeafHarvestTargetPercent;
        var (realAptaCount, totalActive) = await AptaCountsAsync(context, lot, ct);
        var hypothesis = request.Harvest.HypothesisAptaPercent;

        // % de aptas efectivo para la regla: hipótesis explícita o dato real.
        var usesHypothesis = hypothesis is not null;
        decimal? aptaPercentUsed = usesHypothesis
            ? Math.Round(hypothesis!.Value, 1)
            : totalActive > 0 ? Math.Round(realAptaCount * 100m / totalActive, 1) : null;

        // Decisiones (GDD y/o hipótesis), separadas para que la salida indique
        // qué parte decidió cada cosa.
        bool gddInWindow = window is { } w && accumulated >= w.Min && accumulated < w.Max;
        DateOnly? babyLeafEntry = window is { } win
            ? GddDateLogic.EstimateCrossDate(
                accumulated,
                projection.Select(p => new DailyGddPoint(p.Date, p.Gdd, GddPointSource.Forecast)).ToList(),
                win.Min)
            : null;
        DateOnly? conventionalEntry = GddDateLogic.EstimateCrossDate(
            accumulated,
            projection.Select(p => new DailyGddPoint(p.Date, p.Gdd, GddPointSource.Forecast)).ToList(),
            crop.GddTarget);

        bool? isReady = null;
        string? decisionReason = null;

        if (window is { } activeWindow)
        {
            if (lot is not null)
            {
                // Regla híbrida de producción reutilizada (HarvestReadinessRules).
                var aptaCountForRule = usesHypothesis
                    ? Math.Max(0, (int)Math.Round(hypothesis!.Value * totalActive / 100m, 0, MidpointRounding.AwayFromZero))
                    : realAptaCount;
                var rule = HarvestReadinessRules.Evaluate(new HarvestReadinessRules.Input(
                    accumulated, activeWindow.Min, activeWindow.Max, aptaCountForRule, totalActive, requiredPercent));

                if (rule.PendingReason is not null)
                {
                    warnings.Add(rule.PendingReason);
                }

                if (rule.IsReady == false)
                {
                    if (!rule.GddInWindow)
                    {
                        warnings.Add($"GDD {accumulated} fuera de la ventana Baby Leaf ({activeWindow.Min}–{activeWindow.Max}).");
                    }

                    if (requiredPercent is { } req && aptaPercentUsed < req)
                    {
                        warnings.Add($"Porcentaje de aptas ({aptaPercentUsed}%) por debajo del objetivo ({req}%).");
                    }
                }

                isReady = rule.IsReady;
                decisionReason = rule.IsReady switch
                {
                    true when usesHypothesis =>
                        $"GDD {accumulated} en ventana ({activeWindow.Min}–{activeWindow.Max}) Y hipótesis del usuario de {aptaPercentUsed}% aptas ≥ umbral {requiredPercent}%.",
                    true =>
                        $"GDD {accumulated} en ventana ({activeWindow.Min}–{activeWindow.Max}) Y {aptaPercentUsed}% aptas reales ≥ umbral {requiredPercent}%.",
                    null => rule.PendingReason,
                    _ =>
                        $"GDD en ventana: {rule.GddInWindow}; % aptas ({aptaPercentUsed}%) vs umbral ({requiredPercent}%)."
                };
            }
            else
            {
                // Escenario libre: sin plantas activas no aplica la regla de % sobre
                // posición; se informa la ventana (parte GDD) y la hipótesis como dato.
                var pendingReason = requiredPercent is null
                    ? "Sin umbral objetivo de aptas: decisión híbrida pendiente."
                    : usesHypothesis
                        ? $"Escenario libre sin plantas activas: la regla híbrida de % sobre posiciones requiere contexto de lote; se informan la ventana GDD y la hipótesis ({aptaPercentUsed}%)."
                        : "Escenario libre sin plantas activas ni hipótesis de % aptas: decisión híbrida pendiente.";
                warnings.Add(pendingReason);
                decisionReason = pendingReason;
            }
        }
        else if (request.Harvest.EnableBabyLeaf)
        {
            decisionReason = "Sin configuración Baby Leaf: sin ventana GDD para la estrategia.";
            warnings.Add(decisionReason);
        }

        if (request.Harvest.EnableConvencional)
        {
            var hasMaturity = await ConventionalHasMaturityDataAsync(context, lot, ct);
            if (!hasMaturity)
            {
                warnings.Add(lot is null
                    ? "Cosecha convencional en escenario libre: la madurez requiere análisis de imagen (fase futura): decisión pendiente, no se infiere tamaño."
                    : "Cosecha convencional: sin análisis de imagen persistido (fase futura): la madurez queda pendiente, no se infiere tamaño.");
            }
        }

        return new SimulationHarvestOutcome(
            Enabled: true,
            HasBabyLeafConfig: window is not null,
            WindowMin: window?.Min,
            WindowMax: window?.Max,
            GddInWindow: gddInWindow,
            RequiredPercent: requiredPercent,
            RealAptaCount: realAptaCount,
            TotalActivePlants: totalActive,
            AptaPercentUsed: aptaPercentUsed,
            AptaPercentIsHypothesis: usesHypothesis,
            IsReady: isReady,
            DecisionReason: decisionReason,
            BabyLeafEntryDate: babyLeafEntry,
            ConventionalEntryDate: conventionalEntry,
            ConventionalHasMaturityData: await ConventionalHasMaturityDataAsync(context, lot, ct),
            Warnings: warnings);
    }

    /// <summary>Conteos de aptas y activas del lote (solo lectura): sin lote = sin plantas.</summary>
    private static async Task<(int RealAptaCount, int TotalActive)> AptaCountsAsync(
        HydroPilotDbContext context, Lot? lot, CancellationToken ct)
    {
        if (lot is null)
        {
            return (0, 0);
        }

        var plants = await context.Plants
            .Include(p => p.CommercialStage)
            .Where(p => p.LotId == lot.Id && p.OperationalState == PlantOperationalState.Activa)
            .Select(p => p.CommercialStage!.Name)
            .ToListAsync(ct);

        var apta = plants.Count(n => n == CommercialStageNames.BabyLeafApta);
        return (apta, plants.Count);
    }

    private async Task<(decimal Min, decimal Max)?> BabyLeafWindowAsync(
        HydroPilotDbContext context, CropType crop, bool onlyWhenEnabled, CancellationToken ct)
    {
        if (!onlyWhenEnabled)
        {
            return null;
        }

        var config = await context.BabyLeafConfigs
            .Where(c => c.CropTypeId == crop.Id && c.IsActive)
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync(ct);

        return config is null ? null : (config.GddMin, config.GddMax);
    }

    /// <summary>¿Hay análisis de imagen persistido para plantas activas del lote? (fase futura).</summary>
    private static async Task<bool> ConventionalHasMaturityDataAsync(
        HydroPilotDbContext context, Lot? lot, CancellationToken ct)
    {
        if (lot is null)
        {
            return false;
        }

        return await context.PlantImageAnalyses
            .AnyAsync(a => a.PlantImage != null
                           && a.PlantImage.Plant != null
                           && a.PlantImage.Plant.LotId == lot.Id
                           && a.PlantImage.Plant.OperationalState == PlantOperationalState.Activa, ct);
    }

    /// <summary>
    /// Id determinista del escenario (SIM: misma entrada → mismo id y mismo
    /// resultado). Hash SHA-256 del request serializado; no identifica runs
    /// persistidos (no los hay en esta entrega).
    /// </summary>
    public static string ComputeSimulationId(SimulationRequest request)
    {
        var json = JsonSerializer.Serialize(request);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash)[..16];
    }
}