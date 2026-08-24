using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Optimization;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Services.Optimization;

/// <summary>
/// Módulo de optimización (plan 16): recomendaciones explicables de pH/CE,
/// comparativa económica Baby Leaf vs convencional y estado comercial agregado.
///
/// Contratos consumidos (sin recalcular): lecturas válidas de telemetría
/// (TelemetryQualityPolicy), forecast (ForecastService) y agregado de lote
/// (LotAggregateService). La recomendación es INFORMATIVA: aceptar/descartar
/// registra una acción manual; nunca se envían órdenes a hardware ni se calculan
/// dosis exactas (falta volumen, concentración, receta y curva de respuesta).
///
/// Idempotencia (OPT-04): repetir el cálculo con el mismo snapshot (mismo hash
/// canónico) NO duplica recomendaciones; aceptar/descartar repetido es no-op.
/// </summary>
public class OptimizationService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly ForecastService _forecastService;
    private readonly LotAggregateService _lotAggregate;
    private readonly IPlantRiskProvider _plantRiskProvider;
    private readonly IOpenAnomalyProvider _anomalyProvider;
    private readonly IOptimizationEventSink _eventSink;
    private readonly IOptions<OptimizationOptions> _options;
    private readonly ILogger<OptimizationService> _logger;

    public OptimizationService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        ForecastService forecastService,
        LotAggregateService lotAggregate,
        IPlantRiskProvider plantRiskProvider,
        IOpenAnomalyProvider anomalyProvider,
        IOptimizationEventSink eventSink,
        IOptions<OptimizationOptions> options,
        ILogger<OptimizationService> logger)
    {
        _dbFactory = dbFactory;
        _forecastService = forecastService;
        _lotAggregate = lotAggregate;
        _plantRiskProvider = plantRiskProvider;
        _anomalyProvider = anomalyProvider;
        _eventSink = eventSink;
        _options = options;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // Estado (sin persistir) y generación (idempotente por snapshot)
    // ------------------------------------------------------------------

    /// <summary>Estado completo del lote para la UI/API sin persistir nada.</summary>
    public async Task<OptimizationStateDto?> GetStateAsync(int lotId, CancellationToken ct = default)
    {
        var core = await BuildStateCoreAsync(lotId, persist: false, ct);
        return core.State;
    }

    /// <summary>
    /// Genera (o reutiliza) las recomendaciones del lote. La repetición con el
    /// mismo snapshot no duplica; las PENDING de snapshots anteriores se expiran.
    /// Devuelve null si el lote no existe.
    /// </summary>
    public async Task<OptimizationGenerationResult?> GenerateAsync(int lotId, CancellationToken ct = default)
    {
        var expired = await ExpirePendingAsync(ct);

        var core = await BuildStateCoreAsync(lotId, persist: true, ct);
        if (core.State is null)
            return null;

        var created = await LoadRecommendationsAsync(lotId, core.CreatedRecommendationIds, ct);
        var active = await LoadRecommendationsAsync(lotId, null, ct, onlyActive: true);

        return new OptimizationGenerationResult(
            core.State with { ActiveRecommendations = active },
            created,
            expired,
            core.ReusedSnapshotCount > 0,
            core.State.Warnings);
    }

    private sealed record CoreBuild(
        OptimizationStateDto? State,
        IReadOnlyList<int> CreatedRecommendationIds,
        int ReusedSnapshotCount);

    private sealed record UpsertResult(int? CreatedId, bool ReusedExisting);

    private async Task<CoreBuild> BuildStateCoreAsync(int lotId, bool persist, CancellationToken ct)
    {
        var options = _options.Value;
        var warnings = new List<string>();

        var forecast = await _forecastService.GetForecastAsync(lotId, null, false, ct);
        if (forecast is null)
            return new CoreBuild(null, [], 0);

        var lotState = await _lotAggregate.GetLotStateAsync(lotId, ct: ct);
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var lot = await context.Lots
            .Include(l => l.CropType)
            .Include(l => l.Status)
            .Include(l => l.AppliedPhenologicalStage)
            .FirstOrDefaultAsync(l => l.Id == lotId, ct);
        if (lot is null)
            return new CoreBuild(null, [], 0);

        var crop = lot.CropType;
        var isClosed = forecast.StatusName is LotStatusNames.Cosechado or LotStatusNames.Descartado;
        if (isClosed)
        {
            warnings.Add($"Lote {forecast.StatusName}: sin proyección operativa ni recomendaciones nuevas (contexto histórico).");
        }

        // --- Lecturas recientes de pH/CE asociadas al lote (contrato IoT) ---
        var lookback = Math.Max(1, options.ReadingLookbackCount);
        var readings = await context.SensorReadings
            .AsNoTracking()
            .Include(r => r.Sensor)!
            .ThenInclude(s => s!.SensorType)
            .Where(r => r.LotId == lotId)
            .Where(TelemetryQualityPolicy.OperationallyUsableReading)
            .Where(r => r.Sensor != null
                        && (r.Sensor!.SensorType!.Name == "pH" || r.Sensor!.SensorType!.Name == "CE"))
            .OrderByDescending(r => r.ObservedAtUtc)
            .Take(lookback)
            .Select(r => new SnapshotReading(r.Sensor!.SensorType!.Name, r.Quality, r.Value, r.ObservedAtUtc))
            .ToListAsync(ct);

        var phReadings = readings.Where(r => r.SensorType == "pH").ToList();
        var ecReadings = readings.Where(r => r.SensorType == "CE").ToList();

        // --- Riesgos por planta y anomalías abiertas del lote (hooks) ---
        var risks = await _plantRiskProvider.GetActiveRisksAsync(lotId, ct);
        var riskDtos = risks.Select(r => new PlantRiskDto(r.Key, r.Value.Reason)).ToList();
        var hasOpenAnomaly = await _anomalyProvider.HasOpenAnomaliesAsync(lotId, ct);
        if (hasOpenAnomaly)
            warnings.Add("Hay anomalías abiertas en el lote: las recomendaciones químicas quedan bloqueadas hasta su revisión.");

        // --- Catálogo de costos/precios vigente (resolución lote > cultivo > global) ---
        var priceCosts = await ResolveActivePriceCostsAsync(context, lot, ct);
        if (priceCosts.Any(p => p.Source.Equals("demo", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add("Los precios/costos provienen del catálogo DEMO (etiquetado): la comparativa económica es ilustrativa y no usa precios reales.");
        }

        // --- Evaluaciones en vivo ---
        var nowUtc = DateTime.UtcNow;
        var phAssessment = EvaluatePhWithFallback(lot, crop, phReadings, nowUtc, options, warnings);
        var ecAssessment = await EvaluateEcWithFallbackAsync(context, lot, crop, ecReadings, nowUtc, options, warnings, ct);

        // --- Comparativa económica (consume el yield del forecast; no recalcula) ---
        EconomicComparisonDto? economic = null;
        var yieldBase = forecast.Yield.Base;
        if (!isClosed)
        {
            economic = EconomicRules.Compare(yieldBase, priceCosts, options.ProfitabilityThresholdPercent, nowUtc);
        }

        // --- Agregado comercial (OPT-08/OPT-09) ---
        var babyLeafConfig = await context.BabyLeafConfigs
            .AsNoTracking()
            .Where(c => c.CropTypeId == lot.CropTypeId && c.IsActive)
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync(ct);

        var activePlants = lotState?.ActivePlants ?? 0;
        var aptaCount = CountStage(lotState, CommercialStageNames.BabyLeafApta);
        var riskCount = CountStage(lotState, CommercialStageNames.RiesgoFueraDeVentana);
        var aptaPercent = activePlants > 0 ? Math.Round(aptaCount * 100m / activePlants, 1) : 0m;
        var commercial = CommercialRules.Evaluate(new CommercialRules.Input(
            activePlants, aptaCount, riskCount, aptaPercent,
            lotState?.IsCommercialStageMixed ?? false,
            lot.BabyLeafHarvestTargetPercent,
            forecast.GddAccumulated,
            babyLeafConfig?.GddMin,
            babyLeafConfig?.GddMax));

        // --- Snapshot (OPT-01) ---
        var snapshot = new OptimizationSnapshot(
            OptimizationContract.RuleVersion,
            lot.Id, lot.Name, crop?.Name ?? "Desconocido", lot.PlantedAreaM2,
            forecast.StatusName, lot.SowingDate,
            readings,
            phAssessment?.CurrentValue, ecAssessment?.CurrentValue,
            phReadings.Count, ecReadings.Count,
            LatestAge(phReadings, nowUtc), LatestAge(ecReadings, nowUtc),
            forecast.GddAccumulated, forecast.GddTarget,
            forecast.PhenologicalStageName, ecAssessment?.TargetValue,
            forecast.EstimatedHarvestDate, forecast.DaysRemaining,
            yieldBase, forecast.AccuracyCycles,
            forecast.DataSourceSummary, forecast.SourceKind.ToString(),
            activePlants, riskCount, aptaCount, aptaPercent,
            lotState?.IsCommercialStageMixed ?? false,
            priceCosts);

        var snapshotHash = OptimizationSnapshotBuilder.ComputeHash(snapshot);
        var snapshotJson = OptimizationSnapshotBuilder.ToCanonicalJson(snapshot);

        // --- Estado DTO ---
        var state = new OptimizationStateDto(
            lot.Id, lot.Name, crop?.Name ?? "Desconocido", forecast.StatusName,
            lot.SowingDate, lot.PlantedAreaM2,
            forecast.GddAccumulated, forecast.GddTarget,
            forecast.PhenologicalStageName, lotState?.EcObjective,
            forecast.EstimatedHarvestDate, forecast.DaysRemaining,
            phAssessment?.CurrentValue, ecAssessment?.CurrentValue,
            LatestAge(phReadings, nowUtc), LatestAge(ecReadings, nowUtc),
            phReadings.Count, ecReadings.Count,
            forecast.DataSourceSummary, forecast.SourceKind,
            forecast.SourceKind == ForecastSourceKind.Mock,
            activePlants, aptaCount, aptaPercent,
            lot.BabyLeafHarvestTargetPercent,
            lotState?.IsCommercialStageMixed ?? false,
            riskDtos,
            [new ChemicalAssessmentDto("pH", phAssessment!), new ChemicalAssessmentDto("CE", ecAssessment!)],
            economic,
            [],
            warnings.Distinct().ToList());

        if (!persist)
            return new CoreBuild(state, [], 0);

        // --- Persistencia idempotente por (lote, tipo, hash de snapshot) ---
        var createdIds = new List<int>();
        var reused = 0;

        if (!isClosed)
        {
            var chemical = await UpsertChemicalAsync(context, snapshot, snapshotHash, snapshotJson, lot.Id,
                phAssessment!, ecAssessment!, hasOpenAnomaly, ct);
            if (chemical.CreatedId is { } chemicalId) createdIds.Add(chemicalId);
            if (chemical.ReusedExisting) reused++;

            var economicOutcome = await UpsertEconomicAsync(context, snapshot, snapshotHash, snapshotJson, lot.Id,
                economic, ct);
            if (economicOutcome.CreatedId is { } economicId) createdIds.Add(economicId);
            if (economicOutcome.ReusedExisting) reused++;

            if (commercial.HasFinding)
            {
                var commercialOutcome = await UpsertCommercialAsync(context, snapshot, snapshotHash, snapshotJson, lot.Id,
                    commercial, ct);
                if (commercialOutcome.CreatedId is { } commercialId) createdIds.Add(commercialId);
                if (commercialOutcome.ReusedExisting) reused++;
            }
        }

        await context.SaveChangesAsync(ct);

        // OPT-06: emitir evento a Notifications solo para recomendaciones NUEVAS
        // y estratégicas (prioridad alta / comparativa que supera el umbral).
        foreach (var id in createdIds)
        {
            var dto = await LoadRecommendationAsync(id, ct);
            if (dto is not null && IsStrategic(dto))
            {
                await _eventSink.OnRecommendationCreatedAsync(new RecommendationEventDto(
                    dto.Id, dto.LotId, dto.RecommendationType, dto.Priority, dto.Status,
                    dto.Direction, dto.Explanation, dto.EstimatedImpact, dto.ImpactUnit,
                    dto.ConfidencePercent, dto.GeneratedAtUtc, dto.SnapshotHash), ct);
            }
        }

        return new CoreBuild(state, createdIds, reused);
    }

    private static bool IsStrategic(RecommendationDto dto) =>
        dto.Priority == OptimizationContractPriority.High;

    // ------------------------------------------------------------------
    // Evaluación química con respaldo del snapshot manual del lote
    // ------------------------------------------------------------------

    private static ChemicalAssessment EvaluatePhWithFallback(
        Lot lot,
        CropType? crop,
        IReadOnlyList<SnapshotReading> phReadings,
        DateTime nowUtc,
        OptimizationOptions options,
        List<string> warnings)
    {
        var bounds = new ChemicalRules.PhBounds(crop?.OptimalPhMin, crop?.OptimalPhTarget, crop?.OptimalPhMax);
        var rangeLabel = ChemicalRules.PhRangeLabel(bounds);

        if (phReadings.Count > 0)
            return ChemicalRules.EvaluatePh(phReadings, bounds, nowUtc, options.MaxReadingAgeMinutes, options.ConsistentReadingCount);

        if (lot.CurrentPh is { } manualPh)
        {
            // Respaldo etiquetado: snapshot manual del lote sin timestamp.
            warnings.Add("Sin lecturas frescas de sensor de pH: se usa el valor manual del lote (CurrentPh) como referencia, dirección 'verificar'.");
            var dir = ChemicalRules.DirectionForPh(manualPh, bounds);
            var target = ChemicalRules.PhTargetForValue(manualPh, bounds);
            var explanation = dir == OptimizationContractDirection.Maintain
                ? $"pH manual del lote {manualPh:0.0#} dentro del rango {rangeLabel}; sin lectura fresca de sensor no se emite recomendación firme."
                : $"pH manual del lote {manualPh:0.0#} fuera del rango {rangeLabel}: verificar con una lectura fresca antes de ajustar.";
            return new ChemicalAssessment(
                Evaluable: bounds.Min is not null,
                dir == OptimizationContractDirection.Maintain ? OptimizationContractDirection.Maintain : OptimizationContractDirection.Verify,
                manualPh, target, rangeLabel, explanation, 35, warnings);
        }

        warnings.Add("Sin lecturas frescas de pH ni valor manual del lote: pH no evaluable.");
        return new ChemicalAssessment(false, OptimizationContractDirection.Verify, null, null, rangeLabel,
            "pH: sin datos utilizables (sin lecturas frescas ni snapshot del lote).", 0, warnings);
    }

    private static async Task<ChemicalAssessment> EvaluateEcWithFallbackAsync(
        HydroPilotDbContext context,
        Lot lot,
        CropType? crop,
        IReadOnlyList<SnapshotReading> ecReadings,
        DateTime nowUtc,
        OptimizationOptions options,
        List<string> warnings,
        CancellationToken ct)
    {
        // Etapa fenológica: snapshot del lote o resuelta por GDD (no se recalcula).
        var stage = lot.AppliedPhenologicalStage;
        if (stage is null && lot.AccumulatedGdd is { } gdd)
        {
            stage = await PlantEvaluationService.ResolvePhenologicalStageAsync(context, lot.CropTypeId, gdd, ct);
        }

        var bounds = stage is null
            ? null
            : new ChemicalRules.EcBounds(stage.EcMin, stage.EcObjective, stage.EcMax);
        var stageName = stage?.Name;

        if (ecReadings.Count > 0)
            return ChemicalRules.EvaluateEc(ecReadings, bounds, stageName, nowUtc, options.MaxReadingAgeMinutes, options.ConsistentReadingCount);

        if (lot.CurrentEc is { } manualEc && stage is not null && bounds is not null)
        {
            warnings.Add("Sin lecturas frescas de sensor de CE: se usa el valor manual del lote (CurrentEc) como referencia, dirección 'verificar'.");
            var dir = ChemicalRules.DirectionForEc(manualEc, bounds);
            var target = ChemicalRules.EcTargetForValue(manualEc, bounds);
            return new ChemicalAssessment(
                Evaluable: true,
                dir == OptimizationContractDirection.Maintain ? OptimizationContractDirection.Maintain : OptimizationContractDirection.Verify,
                manualEc, target,
                ChemicalRules.EcRangeLabel(bounds, stageName),
                $"CE manual del lote {manualEc:0.0#} vs objetivo de la etapa '{stageName}' ({target:0.0#}): verificar con una lectura fresca.",
                35, warnings);
        }

        warnings.Add("Sin lecturas frescas de CE, valor manual ni etapa fenológica: CE no evaluable.");
        return new ChemicalAssessment(false, OptimizationContractDirection.Verify, null, null,
            stage is null ? "CE (sin etapa)" : ChemicalRules.EcRangeLabel(bounds!, stageName),
            "CE: sin datos utilizables (sin lecturas frescas, valor manual o etapa fenológica).", 0, warnings);
    }

    // ------------------------------------------------------------------
    // Upserts idempotentes por tipo de recomendación
    // ------------------------------------------------------------------

    private async Task<UpsertResult> UpsertChemicalAsync(
        HydroPilotDbContext context,
        OptimizationSnapshot snapshot,
        string hash,
        string snapshotJson,
        int lotId,
        ChemicalAssessment ph,
        ChemicalAssessment ec,
        bool hasOpenAnomaly,
        CancellationToken ct)
    {
        // Regla: dentro de rango (MAINTAIN) no genera recomendación química.
        // Desvío (RAISE/LOWER), dato a verificar (VERIFY) o bloqueo sí.
        var hasDeviation = ph.Evaluable && (ph.Direction is OptimizationContractDirection.Raise
                                              or OptimizationContractDirection.Lower
                                              or OptimizationContractDirection.Verify)
                        || ec.Evaluable && (ec.Direction is OptimizationContractDirection.Raise
                                              or OptimizationContractDirection.Lower
                                              or OptimizationContractDirection.Verify);
        var blocked = hasOpenAnomaly || !ph.Evaluable || !ec.Evaluable;
        if (!hasDeviation && !blocked)
            return new UpsertResult(null, false);

        // Idempotencia: mismo (lote, tipo, snapshot) ⇒ misma recomendación.
        var existing = await context.OptimizationRecommendations
            .FirstOrDefaultAsync(r => r.LotId == lotId
                                      && r.RecommendationType == OptimizationContractType.ChemicalSolution
                                      && r.SnapshotHash == hash, ct);
        if (existing is not null)
            return new UpsertResult(null, true);

        // Snapshot nuevo: las PENDING del snapshot anterior se expiran (historial).
        await ExpireReplacedAsync(context, lotId, OptimizationContractType.ChemicalSolution, hash, ct);

        var (direction, priority, current, target, targetLabel, explanation) =
            ComposeChemical(ph, ec, hasOpenAnomaly);

        var status = blocked
            ? OptimizationContractStatus.Blocked
            : OptimizationContractStatus.Pending;

        var confidence = blocked
            ? 0
            : Math.Max(ph.Evaluable ? ph.ConfidencePercent : 0, ec.Evaluable ? ec.ConfidencePercent : 0);

        var rec = new OptimizationRecommendation
        {
            LotId = lotId,
            RecommendationType = OptimizationContractType.ChemicalSolution,
            Status = status,
            Direction = direction,
            Priority = priority,
            CurrentValue = current,
            TargetValue = target,
            TargetLabel = targetLabel,
            Explanation = explanation,
            ConfidencePercent = confidence,
            DataSourceSummary = ComposeChemicalSource(ph, ec),
            IsMockData = false,
            RuleVersion = OptimizationContract.RuleVersion,
            GeneratedAtUtc = DateTime.UtcNow,
            SnapshotJson = snapshotJson,
            SnapshotHash = hash,
        };
        AddChemicalDetails(rec, ph, ec);
        context.OptimizationRecommendations.Add(rec);
        await context.SaveChangesAsync(ct);
        return new UpsertResult(rec.Id, false);
    }

    private async Task<UpsertResult> UpsertEconomicAsync(
        HydroPilotDbContext context,
        OptimizationSnapshot snapshot,
        string hash,
        string snapshotJson,
        int lotId,
        EconomicComparisonDto? economic,
        CancellationToken ct)
    {
        if (economic is null)
            return new UpsertResult(null, false);

        // Sin hallazgo recomendable (calculable sin superar el umbral): la
        // comparativa se muestra en el panel de estado, no genera tarjeta.
        var blocked = !economic.Calculable;
        var recommend = economic.Calculable && economic.ExceedsThreshold;
        if (!blocked && !recommend)
            return new UpsertResult(null, false);

        var existing = await context.OptimizationRecommendations
            .FirstOrDefaultAsync(r => r.LotId == lotId
                                      && r.RecommendationType == OptimizationContractType.EconomicAlternative
                                      && r.SnapshotHash == hash, ct);
        if (existing is not null)
            return new UpsertResult(null, true);

        await ExpireReplacedAsync(context, lotId, OptimizationContractType.EconomicAlternative, hash, ct);

        var percentageDiff = economic.ProfitabilityDifferencePct is { } diff ? Math.Round(diff, 1) : (decimal?)null;
        var rec = new OptimizationRecommendation
        {
            LotId = lotId,
            RecommendationType = OptimizationContractType.EconomicAlternative,
            Status = blocked ? OptimizationContractStatus.Blocked : OptimizationContractStatus.Pending,
            Direction = recommend ? OptimizationContractDirection.Switch
                : OptimizationContractDirection.Verify,
            Priority = recommend ? OptimizationContractPriority.High : OptimizationContractPriority.Medium,
            CurrentValue = decimal.Round(economic.BabyLeaf?.MarginPercent ?? 0m, 1),
            TargetValue = recommend && economic.RecommendedDestination is not null
                ? (economic.RecommendedDestination == OptimizationContractDestination.BabyLeaf
                    ? economic.BabyLeaf?.MarginPercent
                    : economic.Conventional?.MarginPercent)
                : null,
            TargetLabel = recommend && economic.RecommendedDestination is not null
                ? $"Destino recomendado: {EconomicRules.DestinationLabel(economic.RecommendedDestination)}"
                : "Sin cambio de destino",
            Explanation = blocked
                ? $"{economic.NotCalculableReason} Supuestos: {string.Join(" ", economic.Assumptions)}"
                : $"La diferencia de rentabilidad ({percentageDiff:0.#} pts) supera el umbral del {economic.ThresholdPercent:0.#}%: " +
                  $"el destino '{EconomicRules.DestinationLabel(economic.RecommendedDestination!)}' presenta mayor margen. " +
                  "La comparación es informativa y no decide la cosecha.",
            EstimatedImpact = percentageDiff,
            ImpactUnit = "pts de margen",
            ConfidencePercent = 75,
            DataSourceSummary = "Catálogo de costos/precios + rendimiento del forecast",
            IsMockData = economic.Assumptions.Any(a => a.Contains("demo", StringComparison.OrdinalIgnoreCase)),
            RuleVersion = OptimizationContract.RuleVersion,
            GeneratedAtUtc = DateTime.UtcNow,
            SnapshotJson = snapshotJson,
            SnapshotHash = hash,
        };
        AddEconomicDetails(rec, economic);
        context.OptimizationRecommendations.Add(rec);
        await context.SaveChangesAsync(ct);
        return new UpsertResult(rec.Id, false);
    }

    private async Task<UpsertResult> UpsertCommercialAsync(
        HydroPilotDbContext context,
        OptimizationSnapshot snapshot,
        string hash,
        string snapshotJson,
        int lotId,
        CommercialRules.Result commercial,
        CancellationToken ct)
    {
        var existing = await context.OptimizationRecommendations
            .FirstOrDefaultAsync(r => r.LotId == lotId
                                      && r.RecommendationType == OptimizationContractType.CommercialGrowout
                                      && r.SnapshotHash == hash, ct);
        if (existing is not null)
            return new UpsertResult(null, true);

        await ExpireReplacedAsync(context, lotId, OptimizationContractType.CommercialGrowout, hash, ct);

        var rec = new OptimizationRecommendation
        {
            LotId = lotId,
            RecommendationType = OptimizationContractType.CommercialGrowout,
            Status = OptimizationContractStatus.Pending,
            Direction = commercial.Direction,
            Priority = commercial.Priority,
            Explanation = commercial.Explanation,
            ConfidencePercent = commercial.Priority == OptimizationContractPriority.High ? 70 : 60,
            DataSourceSummary = "Agregado de plantas del lote (conteos por estado comercial)",
            IsMockData = false,
            RuleVersion = OptimizationContract.RuleVersion,
            GeneratedAtUtc = DateTime.UtcNow,
            SnapshotJson = snapshotJson,
            SnapshotHash = hash,
        };
        foreach (var (detail, index) in commercial.Details.Select((d, i) => (d, i)))
        {
            rec.Details.Add(new RecommendationDetail
            {
                Label = $"Hallazgo {index + 1}",
                Kind = "finding",
                Note = detail,
                Order = index,
            });
        }
        context.OptimizationRecommendations.Add(rec);
        await context.SaveChangesAsync(ct);
        return new UpsertResult(rec.Id, false);
    }

    /// <summary>
    /// Expira recomendaciones PENDING del mismo (lote, tipo) cuyo snapshot fue
    /// reemplazado por uno más nuevo (los datos cambiaron): quedan EXPIRED en el
    /// historial con la razón visible. NUNCA toca ACCEPTED/DISCARDED.
    /// </summary>
    private static async Task ExpireReplacedAsync(
        HydroPilotDbContext context, int lotId, string type, string newHash, CancellationToken ct)
    {
        var pending = await context.OptimizationRecommendations
            .Where(r => r.LotId == lotId
                        && r.RecommendationType == type
                        && r.Status == OptimizationContractStatus.Pending
                        && r.SnapshotHash != newHash)
            .ToListAsync(ct);

        foreach (var rec in pending)
        {
            rec.Status = OptimizationContractStatus.Expired;
            rec.DecidedAtUtc = DateTime.UtcNow;
            rec.DecisionNote = "Reemplazada por un snapshot de datos más nuevo (los insumos cambiaron).";
        }
    }

    // ------------------------------------------------------------------
    // Composición de recomendación química (explicación y detalles)
    // ------------------------------------------------------------------

    private static (string Direction, string Priority, decimal? Current, decimal? Target, string? TargetLabel, string Explanation)
        ComposeChemical(ChemicalAssessment ph, ChemicalAssessment ec, bool hasOpenAnomaly)
    {
        if (hasOpenAnomaly)
        {
            return (OptimizationContractDirection.Verify, OptimizationContractPriority.High, null, null, null,
                "Hay anomalías abiertas en el lote: la recomendación química queda bloqueada hasta revisar el episodio.");
        }

        if (!ph.Evaluable || !ec.Evaluable)
        {
            var reasons = new List<string>();
            if (!ph.Evaluable) reasons.Add(ph.Explanation);
            if (!ec.Evaluable) reasons.Add(ec.Explanation);
            return (OptimizationContractDirection.Verify, OptimizationContractPriority.Medium, null, null, null,
                "Recomendación química bloqueada por datos insuficientes: " + string.Join(" ", reasons));
        }

        var directions = new[] { ph.Direction, ec.Direction };
        var primary = ph.Direction != OptimizationContractDirection.Maintain ? ph : ec;
        var hasFirm = directions.Contains(OptimizationContractDirection.Raise)
                   || directions.Contains(OptimizationContractDirection.Lower);
        var hasVerify = directions.Contains(OptimizationContractDirection.Verify);

        string direction;
        string priority;
        if (hasFirm && directions.Distinct().Count() == 1 && directions[0] is OptimizationContractDirection.Raise or OptimizationContractDirection.Lower)
        {
            direction = directions[0];
            priority = OptimizationContractPriority.High;
        }
        else if (hasFirm)
        {
            direction = OptimizationContractDirection.Verify;
            priority = OptimizationContractPriority.High;
        }
        else if (hasVerify)
        {
            direction = OptimizationContractDirection.Verify;
            priority = OptimizationContractPriority.Medium;
        }
        else
        {
            direction = OptimizationContractDirection.Maintain;
            priority = OptimizationContractPriority.Low;
        }

        var explanation = direction switch
        {
            OptimizationContractDirection.Raise =>
                $"Subir la solución: {ph.Explanation} {ec.Explanation}".TrimEnd(),
            OptimizationContractDirection.Lower =>
                $"Bajar la solución: {ph.Explanation} {ec.Explanation}".TrimEnd(),
            OptimizationContractDirection.Verify =>
                $"Verificar la solución: {ph.Explanation} {ec.Explanation}".TrimEnd(),
            _ => "Mantener la solución dentro de los rangos objetivo.",
        };

        return (direction, priority, primary.CurrentValue, primary.TargetValue, primary.TargetLabel, explanation);
    }

    private static void AddChemicalDetails(OptimizationRecommendation rec, ChemicalAssessment ph, ChemicalAssessment ec)
    {
        void Add(string label, ChemicalAssessment a, int order)
        {
            rec.Details.Add(new RecommendationDetail
            {
                Label = label,
                Kind = "metric",
                Value = a.CurrentValue,
                Target = a.TargetValue,
                Unit = label == "pH" ? "pH" : "mS/cm",
                Note = a.Explanation,
                Order = order,
            });
        }

        Add("pH", ph, 1);
        Add("CE", ec, 2);
    }

    private static string ComposeChemicalSource(ChemicalAssessment ph, ChemicalAssessment ec)
    {
        var parts = new List<string>();
        if (ph.Evaluable) parts.Add($"pH sensor ({ph.ConfidencePercent}%)");
        if (ec.Evaluable) parts.Add($"CE sensor ({ec.ConfidencePercent}%)");
        return parts.Count > 0 ? string.Join(" + ", parts) : "sin lecturas utilizables (bloqueado)";
    }

    private static void AddEconomicDetails(OptimizationRecommendation rec, EconomicComparisonDto economic)
    {
        void AddScenario(string label, EconomicScenarioDto s, int order)
        {
            var note = s.Calculable
                ? $"Ingresos ${s.RevenueM2:0.0#} - costos ${s.TotalCostM2:0.0#} = ${s.ResultM2:0.0#}/m² (margen {s.MarginPercent:0.#}%)"
                : s.NotCalculableReason;
            rec.Details.Add(new RecommendationDetail
            {
                Label = label,
                Kind = "scenario",
                Value = s.ResultM2,
                Unit = "$/m²",
                Note = note,
                Order = order,
            });
        }

        if (economic.BabyLeaf is not null) AddScenario("Escenario Baby Leaf", economic.BabyLeaf, 1);
        if (economic.Conventional is not null) AddScenario("Escenario convencional", economic.Conventional, 2);
        foreach (var (assumption, index) in economic.Assumptions.Select((a, i) => (a, i)))
        {
            rec.Details.Add(new RecommendationDetail
            {
                Label = "Supuesto",
                Kind = "assumption",
                Note = assumption,
                Order = index + 3,
            });
        }
    }

    // ------------------------------------------------------------------
    // Acciones (aceptar/descartar) idempotentes y expiración
    // ------------------------------------------------------------------

    /// <summary>Aceptar una recomendación: registra acción manual; repetido es no-op.</summary>
    public async Task<DecisionResult> AcceptAsync(int recommendationId, string? note, string? performedBy, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var rec = await context.OptimizationRecommendations
            .Include(r => r.Actions)
            .FirstOrDefaultAsync(r => r.Id == recommendationId, ct);
        if (rec is null)
            return new DecisionResult(false, false, $"Recomendación '{recommendationId}' no encontrada.", null);

        if (rec.Status == OptimizationContractStatus.Accepted)
            return new DecisionResult(true, true, "La recomendación ya estaba aceptada (acción repetida, sin cambios).", ToDto(rec));

        if (rec.Status == OptimizationContractStatus.Discarded)
            return new DecisionResult(false, false, $"La recomendación fue descartada ({rec.DecisionNote ?? "sin motivo"}) y no puede aceptarse.", ToDto(rec));

        if (rec.Status == OptimizationContractStatus.Expired)
            return new DecisionResult(false, false, "La recomendación venció sin decisión: regenerá el cálculo con el snapshot actual.", ToDto(rec));

        if (rec.Status == OptimizationContractStatus.Blocked)
            return new DecisionResult(false, false, "La recomendación está bloqueada por datos insuficientes: no hay acción que aceptar.", ToDto(rec));

        rec.Status = OptimizationContractStatus.Accepted;
        rec.DecidedAtUtc = DateTime.UtcNow;
        rec.DecisionNote = note;
        rec.Actions.Add(new RecommendationAction
        {
            Action = OptimizationContractAction.Accept,
            Note = note,
            PerformedBy = performedBy,
            ActionedAtUtc = DateTime.UtcNow,
        });
        await context.SaveChangesAsync(ct);

        _logger.LogInformation("Recomendación {RecommendationId} aceptada (nota: {Note})", recommendationId, note);
        return new DecisionResult(true, false, "Recomendación aceptada: se registró la acción manual.", ToDto(rec));
    }

    /// <summary>
    /// Descartar una recomendación con motivo (obligatorio): conserva historial.
    /// Repetido es no-op. No reabre recomendaciones aceptadas/vencidas/bloqueadas.
    /// </summary>
    public async Task<DecisionResult> DiscardAsync(int recommendationId, string reason, string? performedBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return new DecisionResult(false, false, "Motivo de descarte obligatorio (para conservar trazabilidad).", null);

        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var rec = await context.OptimizationRecommendations
            .Include(r => r.Actions)
            .FirstOrDefaultAsync(r => r.Id == recommendationId, ct);
        if (rec is null)
            return new DecisionResult(false, false, $"Recomendación '{recommendationId}' no encontrada.", null);

        if (rec.Status == OptimizationContractStatus.Discarded)
            return new DecisionResult(true, true, "La recomendación ya estaba descartada (acción repetida, sin cambios).", ToDto(rec));

        if (rec.Status != OptimizationContractStatus.Pending)
            return new DecisionResult(false, false,
                $"Solo se descartan recomendaciones pendientes (estado actual: {rec.Status}).", ToDto(rec));

        rec.Status = OptimizationContractStatus.Discarded;
        rec.DecidedAtUtc = DateTime.UtcNow;
        rec.DecisionNote = reason;
        rec.Actions.Add(new RecommendationAction
        {
            Action = OptimizationContractAction.Discard,
            Note = reason,
            PerformedBy = performedBy,
            ActionedAtUtc = DateTime.UtcNow,
        });
        await context.SaveChangesAsync(ct);

        _logger.LogInformation("Recomendación {RecommendationId} descartada (motivo: {Reason})", recommendationId, reason);
        return new DecisionResult(true, false, "Recomendación descartada y conservada en el historial.", ToDto(rec));
    }

    /// <summary>Historial de recomendaciones (opcional filtrar por lote).</summary>
    public async Task<IReadOnlyList<RecommendationDto>> GetHistoryAsync(int? lotId = null, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        return await HistoryQuery(context, lotId, ct);
    }

    /// <summary>
    /// Marca EXPIRED las PENDING sin decisión después de PendingExpirationHours.
    /// Devuelve la cantidad expirada.
    /// </summary>
    public async Task<int> ExpirePendingAsync(CancellationToken ct = default)
    {
        var options = _options.Value;
        var threshold = DateTime.UtcNow.AddHours(-options.PendingExpirationHours);

        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var pending = await context.OptimizationRecommendations
            .Where(r => r.Status == OptimizationContractStatus.Pending && r.GeneratedAtUtc < threshold)
            .ToListAsync(ct);

        foreach (var rec in pending)
        {
            rec.Status = OptimizationContractStatus.Expired;
            rec.DecidedAtUtc = DateTime.UtcNow;
            rec.DecisionNote = $"Vencida por antigüedad (sin decisión dentro de {options.PendingExpirationHours} h).";
        }

        await context.SaveChangesAsync(ct);
        if (pending.Count > 0)
            _logger.LogInformation("Optimization: {Count} recomendaciones pendientes vencidas (EXPIRED).", pending.Count);
        return pending.Count;
    }

    // ------------------------------------------------------------------
    // Carga y DTOs
    // ------------------------------------------------------------------

    private async Task<IReadOnlyList<RecommendationDto>> LoadRecommendationsAsync(
        int lotId,
        IReadOnlyList<int>? ids,
        CancellationToken ct,
        bool onlyActive = false)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var query = context.OptimizationRecommendations
            .Include(r => r.Details)
            .Include(r => r.Actions)
            .Where(r => r.LotId == lotId);
        if (ids is { Count: > 0 })
            query = query.Where(r => ids.Contains(r.Id));
        if (onlyActive)
        {
            query = query.Where(r => r.Status == OptimizationContractStatus.Pending
                                  || r.Status == OptimizationContractStatus.Blocked);
        }

        var rows = await query.OrderByDescending(r => r.GeneratedAtUtc).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    private async Task<RecommendationDto?> LoadRecommendationAsync(int id, CancellationToken ct)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var rec = await context.OptimizationRecommendations
            .Include(r => r.Details)
            .Include(r => r.Actions)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        return rec is null ? null : ToDto(rec);
    }

    private static async Task<IReadOnlyList<RecommendationDto>> HistoryQuery(
        HydroPilotDbContext context, int? lotId, CancellationToken ct)
    {
        var query = context.OptimizationRecommendations
            .Include(r => r.Details)
            .Include(r => r.Actions)
            .AsQueryable();
        if (lotId is { } id)
            query = query.Where(r => r.LotId == id);

        var rows = await query.OrderByDescending(r => r.GeneratedAtUtc).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    internal static RecommendationDto ToDto(OptimizationRecommendation r) => new(
        r.Id,
        r.LotId,
        r.RecommendationType,
        r.Status,
        r.Direction,
        r.Priority,
        r.CurrentValue,
        r.TargetValue,
        r.TargetLabel,
        r.Explanation,
        r.EstimatedImpact,
        r.ImpactUnit,
        r.ConfidencePercent,
        r.DataSourceSummary,
        r.IsMockData,
        r.RuleVersion,
        r.GeneratedAtUtc,
        r.DecidedAtUtc,
        r.DecisionNote,
        r.SnapshotHash,
        r.Details.OrderBy(d => d.Order).Select(d => new RecommendationDetailDto(d.Id, d.Label, d.Kind, d.Value, d.Target, d.Unit, d.Note, d.Order)).ToList(),
        r.Actions.OrderBy(a => a.ActionedAtUtc).Select(a => new RecommendationActionDto(a.Id, a.Action, a.Note, a.PerformedBy, a.ActionedAtUtc)).ToList());

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static int CountStage(LotStateDto? lotState, string stageName) =>
        lotState?.CommercialCounts.FirstOrDefault(c => c.StageName == stageName)?.Count ?? 0;

    private static int? LatestAge(IReadOnlyList<SnapshotReading> readings, DateTime nowUtc) =>
        readings.Count > 0 ? (int)(nowUtc - readings.Max(r => r.ObservedAtUtc)).TotalMinutes : null;

    /// <summary>
    /// Resuelve el catálogo vigente (hoy) con precedencia de scope:
    /// lote específico &gt; cultivo &gt; global. Devuelve el SnapshotPriceCost
    /// para el snapshot; los valores demo se conservan etiquetados.
    /// </summary>
    private static async Task<IReadOnlyList<SnapshotPriceCost>> ResolveActivePriceCostsAsync(
        HydroPilotDbContext context, Lot lot, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = await context.CostPriceCatalogs
            .AsNoTracking()
            .Where(c => c.ValidFrom <= today
                        && (c.ValidUntil == null || c.ValidUntil >= today)
                        && (c.CropTypeId == lot.CropTypeId || c.CropTypeId == null)
                        && (c.LotId == lot.Id || c.LotId == null))
            .ToListAsync(ct);

        var lotSpecific = rows.Where(c => c.LotId == lot.Id).ToList();
        var cropWide = rows.Where(c => c.LotId == null && c.CropTypeId == lot.CropTypeId).ToList();
        var global = rows.Where(c => c.LotId == null && c.CropTypeId == null).ToList();

        var result = new List<SnapshotPriceCost>();
        foreach (var destination in new[] { OptimizationContractDestination.BabyLeaf, OptimizationContractDestination.Conventional })
        {
            foreach (var item in new[] {
                OptimizationContractCostItem.PricePerKg,
                OptimizationContractCostItem.SeedCostM2,
                OptimizationContractCostItem.NutrientCostM2,
                OptimizationContractCostItem.EnergyCostM2,
                OptimizationContractCostItem.TransplantCostM2 })
            {
                var row = lotSpecific.FirstOrDefault(c => c.Destination == destination && c.Item == item)
                       ?? cropWide.FirstOrDefault(c => c.Destination == destination && c.Item == item)
                       ?? global.FirstOrDefault(c => c.Destination == destination && c.Item == item);
                if (row is not null)
                {
                    result.Add(new SnapshotPriceCost(
                        row.Destination, row.Item, row.Value, row.Currency, row.Source,
                        row.ValidFrom, row.ValidUntil));
                }
            }
        }

        return result;
    }
}