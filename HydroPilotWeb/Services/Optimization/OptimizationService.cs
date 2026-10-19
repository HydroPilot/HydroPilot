using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Optimization;
using HydroPilotWeb.Services.Anomalies;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.Simulation;

namespace HydroPilotWeb.Services.Optimization;

/// <summary>
/// Módulo de optimización: qué hacer en un lote para ganar más o gastar menos (R1–R6, ver
/// <see cref="OptimizationRules"/>). Consume la lógica de las otras vistas, sin recalcularla: Lotes
/// (fase, cortes, plantas, metas, pesos y precios), Simulación (estrategias) y Anomalías (pH/CE).
///
/// Ciclo de vida: una recomendación por problema del lote (huella). Mientras el problema sigue, sus números
/// se actualizan en el lugar; cuando desaparece se cierra: pendiente → ya no aplica; aceptada → cumplida
/// (o no cumplida si se cerró la ventana sin cortar); descartada → deja de silenciar el problema. Una
/// aceptada que pasa su fecha sin resolverse queda no cumplida. Se calcula al abrir la vista y cada
/// <see cref="OptimizationOptions.SweepIntervalMinutes"/> (OptimizationSweepHostedService).
/// Nunca envía órdenes a hardware.
/// </summary>
public class OptimizationService
{
    /// <summary>Una descartada vuelve a aparecer si el impacto creció al menos esta proporción.</summary>
    public const decimal ResurfaceGrowth = 1.5m;

    /// <summary>Recomendaciones cerradas que muestra el historial.</summary>
    public const int HistorySize = 20;

    // Una evaluación a la vez (vista y barrido): evita duplicar la recomendación de un mismo problema.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly LotOverviewService _lotOverview;
    private readonly SimulationService _simulation;
    private readonly SolutionStatusService _solution;
    private readonly SystemParametersService _parameters;
    private readonly IOptimizationEventSink _eventSink;
    private readonly ILogger<OptimizationService> _logger;

    public OptimizationService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        LotOverviewService lotOverview,
        SimulationService simulation,
        SolutionStatusService solution,
        SystemParametersService parameters,
        IOptimizationEventSink eventSink,
        ILogger<OptimizationService> logger)
    {
        _dbFactory = dbFactory;
        _lotOverview = lotOverview;
        _simulation = simulation;
        _solution = solution;
        _parameters = parameters;
        _eventSink = eventSink;
        _logger = logger;
    }

    /// <summary>Lotes en curso (Activo / En pausa) para el selector, más recientes primero.</summary>
    public async Task<IReadOnlyList<LotOption>> GetLotOptionsAsync(CancellationToken ct = default) =>
        (await _lotOverview.GetLotOptionsAsync(ct)).Where(l => l.IsOperational).ToList();

    /// <summary>Evalúa el lote (si se pide) y arma la vista. Null si el lote no existe.</summary>
    public async Task<OptimizationViewDto?> GetViewAsync(int lotId, bool evaluate = true, CancellationToken ct = default)
    {
        if (evaluate)
            await EvaluateLotAsync(lotId, ct);

        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var lot = await context.Lots.AsNoTracking()
            .Include(l => l.Status)
            .FirstOrDefaultAsync(l => l.Id == lotId, ct);
        if (lot is null)
            return null;

        var rows = await context.OptimizationRecommendations.AsNoTracking()
            .Include(r => r.Details)
            .Include(r => r.Actions)
            .Where(r => r.LotId == lotId)
            .ToListAsync(ct);
        var all = rows.Select(ToDto).ToList();

        // Activas: las de la lógica actual pendientes o en curso (las de la lógica anterior van al historial).
        var current = rows.Where(r => r.RuleVersion == OptimizationContract.RuleVersion).Select(r => r.Id).ToHashSet();
        var active = all
            .Where(r => current.Contains(r.Id))
            .Where(r => r.Status is OptimizationContractStatus.Pending or OptimizationContractStatus.Accepted)
            .OrderBy(r => PriorityOrder(r.Priority))
            .ThenBy(r => r.Status == OptimizationContractStatus.Accepted ? 1 : 0)
            .ThenByDescending(r => r.Impact ?? 0m)
            .ToList();
        var activeIds = active.Select(r => r.Id).ToHashSet();
        var history = all
            .Where(r => !activeIds.Contains(r.Id))
            .OrderByDescending(r => r.ClosedAtUtc ?? r.DecidedAtUtc ?? r.GeneratedAtUtc)
            .Take(HistorySize)
            .ToList();
        var done = all.Where(r => r.Status == OptimizationContractStatus.Done).ToList();

        var kpis = new OptimizationKpis(
            active.Count(r => r.Status == OptimizationContractStatus.Pending),
            active.Count(r => r.Status == OptimizationContractStatus.Pending && r.Priority == OptimizationContractPriority.High),
            SumOrNull(active.Where(r => r.ImpactKind == OptimizationImpactKind.Capture).Select(r => r.Impact)),
            SumOrNull(active.Where(r => r.ImpactKind == OptimizationImpactKind.Saving).Select(r => r.Impact)),
            SumOrNull(done.Select(r => r.RealizedAmount)),
            done.Count);

        var isClosed = lot.Status?.Name is LotStatusNames.Cosechado or LotStatusNames.Descartado;
        IReadOnlyList<string> missing = [];
        var currency = YieldPrices.None.Currency;
        if (!isClosed)
            (missing, currency) = await MissingParametersAsync(context, lot, ct);

        return new OptimizationViewDto(
            lot.Id,
            string.IsNullOrWhiteSpace(lot.Name) ? $"Lote #{lot.Id}" : lot.Name!,
            isClosed,
            currency,
            kpis,
            active,
            history,
            missing);
    }

    /// <summary>
    /// Evalúa el lote con las reglas R1–R6 y sincroniza sus recomendaciones (una por problema). Devuelve
    /// cuántas aparecieron. Las nuevas de prioridad alta se avisan a Notificaciones una sola vez.
    /// </summary>
    public async Task<int> EvaluateLotAsync(int lotId, CancellationToken ct = default)
    {
        var overview = await _lotOverview.GetOverviewAsync(lotId, ct: ct);
        if (overview is null)
            return 0;

        var lotName = overview.State.Name;
        var evaluation = overview.IsClosed
            ? OptimizationEvaluation.ClosedLot
            : OptimizationRules.Evaluate(await SituationAsync(lotId, overview, ct));

        List<OptimizationRecommendation> created;
        await Gate.WaitAsync(ct);
        try
        {
            created = await SyncAsync(lotId, overview, evaluation, ct);
        }
        finally
        {
            Gate.Release();
        }

        foreach (var rec in created.Where(r => r.Priority == OptimizationContractPriority.High))
        {
            try
            {
                await _eventSink.OnRecommendationCreatedAsync(new RecommendationEventDto(
                    rec.Id, rec.LotId, lotName, rec.RecommendationType, rec.Title ?? string.Empty,
                    rec.Explanation, rec.Priority, rec.EstimatedImpact, rec.ImpactUnit, rec.GeneratedAtUtc), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo avisar la recomendación {RecommendationId}", rec.Id);
            }
        }

        return created.Count;
    }

    /// <summary>
    /// Barrido: evalúa los lotes en curso y los que todavía tienen recomendaciones abiertas (un lote que se
    /// cerró las cierra). Devuelve cuántas recomendaciones aparecieron.
    /// </summary>
    public async Task<int> EvaluateOpenLotsAsync(CancellationToken ct = default)
    {
        var lotIds = (await GetLotOptionsAsync(ct)).Select(l => l.Id).ToList();
        await using (var context = await _dbFactory.CreateDbContextAsync(ct))
        {
            lotIds.AddRange(await context.OptimizationRecommendations.AsNoTracking()
                .Where(r => r.Fingerprint != null && r.ClosedAtUtc == null)
                .Select(r => r.LotId)
                .Distinct()
                .ToListAsync(ct));
        }

        var created = 0;
        foreach (var lotId in lotIds.Distinct())
        {
            try
            {
                created += await EvaluateLotAsync(lotId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Optimización: no se pudo evaluar el lote {LotId}", lotId);
            }
        }
        return created;
    }

    // ------------------------------------------------------------------
    // Datos de entrada de las reglas
    // ------------------------------------------------------------------

    private async Task<LotSituation> SituationAsync(int lotId, LotOverviewDto overview, CancellationToken ct)
    {
        var today = overview.Forecast?.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var stillDeciding = overview.Timeline?.Phase is CyclePhase.Desarrollo or CyclePhase.VentanaBabyLeaf;

        return new LotSituation(
            today,
            overview.Timeline,
            overview.Plants,
            overview.Goals,
            overview.Objectives.BabyLeafGoalPercent,
            overview.Objectives.MaxLossPercent,
            overview.Yield.Reference,
            overview.Yield.Prices,
            await _parameters.GetNutrientPricePerLiterAsync(ct),
            await _solution.GetLotAsync(lotId, ct),
            stillDeciding && overview.Plants.Active > 0
                ? await CompareStrategiesAsync(lotId, overview.Objectives.BabyLeafGoalPercent, ct)
                : null,
            await _parameters.GetStrategyThresholdsAsync(ct),
            await AutoControlledAsync(lotId, ct));
    }

    /// <summary>pH y CE que el control automático ya mantiene en el lote (nodo en AUTO, sin pausa).</summary>
    private async Task<IReadOnlySet<string>> AutoControlledAsync(int lotId, CancellationToken ct)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        return await Control.ControlService.AutoControlledForLotAsync(context, lotId, DateTime.UtcNow, ct);
    }

    /// <summary>
    /// Estrategias del lote con Simulación (clima de Lotes, pesos y precios del sistema). El plan es la meta
    /// Baby Leaf del lote o, sin meta, máximo Baby Leaf (lo que hace Lotes: en el óptimo se corta lo apto).
    /// Semillas a $0: son las mismas en las tres estrategias y no cambian la diferencia.
    /// </summary>
    private async Task<StrategyComparison?> CompareStrategiesAsync(int lotId, decimal? goalPercent, CancellationToken ct)
    {
        var planKind = goalPercent is null ? SimulationStrategyKind.MaxBabyLeaf : SimulationStrategyKind.BabyLeafGoal;
        var preview = await _simulation.PreviewAsync(new SimulationRequest(
            lotId, null, null, null,
            new SimulationClimateInput(SimulationClimateMode.Forecast, null, null, null),
            new SimulationCostsInput(PricePerSeed: 0m, NutrientPricePerLiter: null),
            new SimulationHarvestInput(planKind, goalPercent),
            null), ct);

        if (preview.Result is not { } result)
            return null;

        var options = result.Strategies
            .Select(st => new StrategyOption(st.Kind, st.Name, st.Available ? st.Margin : null))
            .ToList();
        var plan = options.FirstOrDefault(o => o.Kind == planKind);
        return plan is null ? null : new StrategyComparison(plan, options, result.Outlook.History.Lots.Count);
    }

    // ------------------------------------------------------------------
    // Sincronización: una recomendación por problema
    // ------------------------------------------------------------------

    private async Task<List<OptimizationRecommendation>> SyncAsync(
        int lotId, LotOverviewDto overview, OptimizationEvaluation evaluation, CancellationToken ct)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var nowUtc = DateTime.UtcNow;
        var today = overview.Forecast?.AsOfDate ?? DateOnly.FromDateTime(nowUtc);

        var open = await context.OptimizationRecommendations
            .Include(r => r.Details)
            .Where(r => r.LotId == lotId && r.Fingerprint != null && r.ClosedAtUtc == null)
            .ToListAsync(ct);

        // Con "Todo convencional" aceptado no se corta Baby Leaf ni se persigue la meta Baby Leaf.
        var allConventional = OptimizationRules.Fingerprint(
            OptimizationContractType.HarvestStrategy, OptimizationRules.StrategyKey(SimulationStrategyKind.AllConventional));
        var suppressed = open.Any(r => r.Fingerprint == allConventional && r.Status == OptimizationContractStatus.Accepted)
            ? new HashSet<string>
            {
                OptimizationRules.Fingerprint(OptimizationContractType.BabyLeafCut, OptimizationRules.KeyBabyLeafCut),
                OptimizationRules.Fingerprint(OptimizationContractType.CycleGoals, OptimizationRules.KeyGoal),
            }
            : [];

        var findings = evaluation.Findings.Where(f => !suppressed.Contains(f.Fingerprint)).ToList();
        var created = new List<OptimizationRecommendation>();

        foreach (var finding in findings)
        {
            var mine = open.Where(r => r.Fingerprint == finding.Fingerprint).ToList();
            var active = mine.FirstOrDefault(r => r.Status is OptimizationContractStatus.Pending or OptimizationContractStatus.Accepted);
            if (active is not null)
            {
                Apply(context, active, finding, nowUtc);
                if (active.Status == OptimizationContractStatus.Accepted && active.DueDate is { } due && due < today)
                    Close(active, OptimizationContractStatus.Missed, "Venció sin resolverse.", nowUtc);
                continue;
            }

            // Descartada: silencia el problema mientras siga, salvo que el impacto crezca mucho.
            var discarded = mine.FirstOrDefault(r => r.Status == OptimizationContractStatus.Discarded);
            if (discarded is not null)
            {
                discarded.LastEvaluatedAtUtc = nowUtc;
                if (!(finding.Impact is { } impact && discarded.EstimatedImpact is { } before
                      && before > 0 && impact >= before * ResurfaceGrowth))
                {
                    continue;
                }
                discarded.ClosedAtUtc = nowUtc;
            }

            var rec = new OptimizationRecommendation
            {
                LotId = lotId,
                RecommendationType = finding.Type,
                Fingerprint = finding.Fingerprint,
                Status = OptimizationContractStatus.Pending,
                RuleVersion = OptimizationContract.RuleVersion,
                GeneratedAtUtc = nowUtc,
            };
            Apply(context, rec, finding, nowUtc);
            context.OptimizationRecommendations.Add(rec);
            created.Add(rec);
        }

        // Problemas evaluados que ya no están: se cierran (los suprimidos, como reemplazados).
        var present = findings.Select(f => f.Fingerprint).ToHashSet();
        foreach (var rec in open.Where(r => !present.Contains(r.Fingerprint!)))
        {
            if (suppressed.Contains(rec.Fingerprint!))
            {
                if (rec.Status is OptimizationContractStatus.Pending or OptimizationContractStatus.Accepted)
                    Close(rec, OptimizationContractStatus.Expired, "Se eligió Todo convencional.", nowUtc);
                else
                    rec.ClosedAtUtc = nowUtc;
                continue;
            }

            if (!evaluation.WasEvaluated(rec.Fingerprint!))
                continue; // sin datos para saberlo: queda como está

            switch (rec.Status)
            {
                case OptimizationContractStatus.Pending:
                    Close(rec, OptimizationContractStatus.Expired, "Ya no aplica.", nowUtc);
                    break;
                case OptimizationContractStatus.Accepted:
                    await ResolveAcceptedAsync(context, rec, overview, nowUtc, ct);
                    break;
                default:
                    rec.ClosedAtUtc = nowUtc; // descartada: el problema terminó, deja de silenciarlo
                    break;
            }
        }

        await context.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>
    /// Aceptada cuyo problema desapareció. Cortes: cumplida si se cosecharon plantas de ese destino desde que
    /// se aceptó (lo logrado = esas plantas × peso × precio de hoy); si no, no cumplida. Estrategia y CE:
    /// cumplida con el último impacto estimado. Riesgos (solución, metas): cumplida, sin $ logrado.
    /// </summary>
    private async Task ResolveAcceptedAsync(
        HydroPilotDbContext context, OptimizationRecommendation rec, LotOverviewDto overview, DateTime nowUtc, CancellationToken ct)
    {
        var since = DateOnly.FromDateTime(rec.DecidedAtUtc ?? rec.GeneratedAtUtc);
        var weights = overview.Yield.Reference;
        var prices = overview.Yield.Prices;

        if (rec.RecommendationType is OptimizationContractType.BabyLeafCut or OptimizationContractType.ConventionalCut)
        {
            var babyLeaf = rec.RecommendationType == OptimizationContractType.BabyLeafCut;
            var harvested = await context.Plants.AsNoTracking()
                .Where(p => p.LotId == rec.LotId
                            && p.OperationalState == PlantOperationalState.Cosechada
                            && p.HarvestDate != null && p.HarvestDate >= since)
                .Select(p => p.CommercialStage != null ? p.CommercialStage.Name : null)
                .ToListAsync(ct);
            var count = harvested.Count(stage => IsBabyLeafStage(stage) == babyLeaf);

            if (count == 0)
            {
                Close(rec, OptimizationContractStatus.Missed,
                    babyLeaf ? "Se cerró la ventana sin cortar Baby Leaf." : "No se cosechó convencional.", nowUtc);
                return;
            }

            var value = babyLeaf
                ? OptimizationRules.PlantValue(weights.BabyLeafGramsPerPlant, prices.BabyLeafPerKg)
                : OptimizationRules.PlantValue(weights.ConventionalGramsPerPlant, prices.ConventionalPerKg);
            rec.RealizedAmount = value is { } v ? Math.Round(count * v, 0) : null;
            Close(rec, OptimizationContractStatus.Done, $"{count} plantas cosechadas.", nowUtc);
            return;
        }

        rec.RealizedAmount = rec.ImpactUnit is OptimizationImpactKind.Capture or OptimizationImpactKind.Saving
            ? rec.EstimatedImpact
            : null;
        Close(rec, OptimizationContractStatus.Done, "Resuelta.", nowUtc);
    }

    private static bool IsBabyLeafStage(string? stage) =>
        stage is CommercialStageNames.BabyLeafApta or CommercialStageNames.CandidataBabyLeaf;

    /// <summary>Copia los datos del problema en la recomendación (nueva o abierta).</summary>
    private static void Apply(HydroPilotDbContext context, OptimizationRecommendation rec, OptimizationFinding finding, DateTime nowUtc)
    {
        rec.Title = finding.Title;
        rec.Explanation = finding.Summary;
        rec.Priority = finding.Priority;
        rec.Direction = finding.Direction;
        rec.EstimatedImpact = finding.Impact;
        rec.ImpactUnit = finding.ImpactKind;
        rec.IsMockData = finding.IsEstimate;
        // Una aceptada conserva su fecha más temprana: el compromiso no se corre solo.
        rec.DueDate = rec.Status == OptimizationContractStatus.Accepted && rec.DueDate is { } previous
                      && (finding.DueDate is not { } next || previous < next)
            ? previous
            : finding.DueDate;
        rec.CurrentValue = finding.CurrentValue;
        rec.TargetValue = finding.TargetValue;
        rec.DataSourceSummary = SourceFor(finding.Type);
        rec.LastEvaluatedAtUtc = nowUtc;
        rec.SnapshotJson = JsonSerializer.Serialize(finding);
        rec.SnapshotHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rec.SnapshotJson))).ToLowerInvariant();

        if (rec.Details.Count > 0)
        {
            context.RecommendationDetails.RemoveRange(rec.Details);
            rec.Details.Clear();
        }
        foreach (var (figure, index) in finding.Figures.Select((f, i) => (f, i)))
        {
            rec.Details.Add(new RecommendationDetail
            {
                Label = figure.Label,
                Kind = figure.Kind,
                Value = figure.Value,
                Unit = figure.Unit,
                Note = figure.Date?.ToString("yyyy-MM-dd") ?? figure.Text,
                Order = index,
            });
        }
    }

    private static void Close(OptimizationRecommendation rec, string status, string note, DateTime nowUtc)
    {
        rec.Status = status;
        rec.ClosedAtUtc = nowUtc;
        rec.DecisionNote = note;
    }

    private static string SourceFor(string type) => type switch
    {
        OptimizationContractType.BabyLeafCut or OptimizationContractType.ConventionalCut => "Lotes: próximo corte y plantas",
        OptimizationContractType.HarvestStrategy => "Simulación: estrategias del lote",
        OptimizationContractType.NutrientOverspend or OptimizationContractType.SolutionRisk => "Anomalías: pH y CE",
        _ => "Lotes: metas del ciclo",
    };

    // ------------------------------------------------------------------
    // Acciones (aceptar / descartar), idempotentes
    // ------------------------------------------------------------------

    /// <summary>Aceptar ("lo hago"): queda en curso y el sistema verifica solo si se cumplió. Repetido es no-op.</summary>
    public async Task<DecisionResult> AcceptAsync(int recommendationId, string? performedBy, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var rec = await context.OptimizationRecommendations
            .Include(r => r.Details)
            .Include(r => r.Actions)
            .FirstOrDefaultAsync(r => r.Id == recommendationId, ct);
        if (rec is null)
            return new DecisionResult(false, false, "La recomendación no existe.", null);

        if (rec.Status == OptimizationContractStatus.Accepted)
            return new DecisionResult(true, true, "Ya estaba aceptada.", ToDto(rec));
        if (rec.Status != OptimizationContractStatus.Pending)
            return new DecisionResult(false, false, "Solo se aceptan recomendaciones pendientes.", ToDto(rec));

        rec.Status = OptimizationContractStatus.Accepted;
        rec.DecidedAtUtc = DateTime.UtcNow;
        rec.Actions.Add(new RecommendationAction
        {
            Action = OptimizationContractAction.Accept,
            PerformedBy = performedBy,
            ActionedAtUtc = rec.DecidedAtUtc.Value,
        });
        await context.SaveChangesAsync(ct);

        _logger.LogInformation("Recomendación {RecommendationId} aceptada por {User}", recommendationId, performedBy);
        return new DecisionResult(true, false, "Aceptada.", ToDto(rec));
    }

    /// <summary>
    /// Descartar con motivo (obligatorio): el problema no vuelve a aparecer mientras siga, salvo que su
    /// impacto crezca al menos un 50 %. Repetido es no-op. Solo pendientes.
    /// </summary>
    public async Task<DecisionResult> DiscardAsync(int recommendationId, string reason, string? performedBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return new DecisionResult(false, false, "Elegí un motivo para descartar.", null);

        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var rec = await context.OptimizationRecommendations
            .Include(r => r.Details)
            .Include(r => r.Actions)
            .FirstOrDefaultAsync(r => r.Id == recommendationId, ct);
        if (rec is null)
            return new DecisionResult(false, false, "La recomendación no existe.", null);

        if (rec.Status == OptimizationContractStatus.Discarded)
            return new DecisionResult(true, true, "Ya estaba descartada.", ToDto(rec));
        if (rec.Status != OptimizationContractStatus.Pending)
            return new DecisionResult(false, false, "Solo se descartan recomendaciones pendientes.", ToDto(rec));

        rec.Status = OptimizationContractStatus.Discarded;
        rec.DecidedAtUtc = DateTime.UtcNow;
        rec.DecisionNote = reason.Trim();
        rec.Actions.Add(new RecommendationAction
        {
            Action = OptimizationContractAction.Discard,
            Note = rec.DecisionNote,
            PerformedBy = performedBy,
            ActionedAtUtc = rec.DecidedAtUtc.Value,
        });
        await context.SaveChangesAsync(ct);

        _logger.LogInformation("Recomendación {RecommendationId} descartada por {User} (motivo: {Reason})",
            recommendationId, performedBy, reason);
        return new DecisionResult(true, false, "Descartada.", ToDto(rec));
    }

    // ------------------------------------------------------------------
    // DTOs y helpers
    // ------------------------------------------------------------------

    internal static RecommendationDto ToDto(OptimizationRecommendation r)
    {
        var lastAction = r.Actions.OrderByDescending(a => a.ActionedAtUtc).FirstOrDefault();
        var isCurrent = r.RuleVersion == OptimizationContract.RuleVersion;
        return new RecommendationDto(
            r.Id,
            r.LotId,
            r.RecommendationType,
            r.Title ?? LegacyTitle(r.RecommendationType),
            r.Status,
            r.Priority,
            r.Direction,
            isCurrent ? r.EstimatedImpact : null,
            isCurrent ? r.ImpactUnit : null,
            r.IsMockData && isCurrent,
            r.DueDate,
            r.Details.OrderBy(d => d.Order).Select(ToFigure).ToList(),
            r.Explanation,
            r.GeneratedAtUtc,
            r.DecidedAtUtc,
            r.DecisionNote,
            lastAction?.PerformedBy,
            r.RealizedAmount,
            r.ClosedAtUtc);
    }

    private static RecommendationFigure ToFigure(RecommendationDetail d) =>
        d.Kind == "date" && DateOnly.TryParse(d.Note, out var date)
            ? new RecommendationFigure(d.Label, d.Kind, d.Value, d.Unit, Date: date)
            : new RecommendationFigure(d.Label, d.Kind, d.Value, d.Unit, Text: d.Note);

    private static string LegacyTitle(string type) => type switch
    {
        OptimizationContractType.ChemicalSolution => "Solución nutritiva (lógica anterior)",
        OptimizationContractType.EconomicAlternative => "Comparativa económica (lógica anterior)",
        OptimizationContractType.CommercialGrowout => "Estado comercial (lógica anterior)",
        _ => type,
    };

    private static int PriorityOrder(string priority) => priority switch
    {
        OptimizationContractPriority.High => 0,
        OptimizationContractPriority.Medium => 1,
        _ => 2,
    };

    private static decimal? SumOrNull(IEnumerable<decimal?> values)
    {
        var known = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        return known.Count > 0 ? known.Sum() : null;
    }

    /// <summary>Pesos y precios sin definir que dejan impactos sin calcular (como "Sin definir" de Lotes).</summary>
    private async Task<(IReadOnlyList<string> Missing, string Currency)> MissingParametersAsync(
        HydroPilotDbContext context, Lot lot, CancellationToken ct)
    {
        var weights = await _parameters.GetYieldReferenceAsync(ct);
        var prices = await SystemParametersService.ResolveLotPricesAsync(context, lot, DateOnly.FromDateTime(DateTime.UtcNow), ct);
        var missing = new List<string>();
        if (weights.BabyLeafGramsPerPlant is null) missing.Add("peso Baby Leaf");
        if (weights.ConventionalGramsPerPlant is null) missing.Add("peso convencional");
        if (prices.BabyLeafPerKg is null) missing.Add("precio Baby Leaf");
        if (prices.ConventionalPerKg is null) missing.Add("precio convencional");
        if (await _parameters.GetNutrientPricePerLiterAsync(ct) is null) missing.Add("precio de nutrientes");
        return (missing, prices.Currency);
    }
}
