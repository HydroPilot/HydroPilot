using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Notifications;
using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.Notifications;
using HydroPilotWeb.Services.Storage;
using HydroPilotWeb.Services.Vision;

namespace HydroPilotWeb.Controllers;

[ApiController]
[Route("api/telemetria/imagenes")]
public class PlantImageController : ControllerBase
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly IPlantImageStorageService _storageService;
    private readonly BabyLeafPipelineService _pipelineService;
    private readonly GddService _gddService;
    private readonly NotificationService _notificationService;
    private readonly LotDailyFlowService? _lotDailyFlowService;
    private readonly PlantEvaluationService? _plantEvaluationService;
    private readonly ILogger<PlantImageController> _logger;

    public PlantImageController(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        IPlantImageStorageService storageService,
        BabyLeafPipelineService pipelineService,
        GddService gddService,
        NotificationService notificationService,
        ILogger<PlantImageController> logger,
        LotDailyFlowService? lotDailyFlowService = null,
        PlantEvaluationService? plantEvaluationService = null)
    {
        _dbFactory = dbFactory;
        _storageService = storageService;
        _pipelineService = pipelineService;
        _gddService = gddService;
        _notificationService = notificationService;
        _logger = logger;
        _lotDailyFlowService = lotDailyFlowService;
        _plantEvaluationService = plantEvaluationService;
    }

    /// <summary>
    /// Recibe una fotografía de planta, la almacena, ejecuta el pipeline de visión y Baby Leaf Score,
    /// persiste los resultados y emite alertas de anomalías o patógenos si corresponde.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(25_000_000)]
    public async Task<IActionResult> UploadImage(
        [FromForm] IFormFile? file,
        [FromForm] int? lotId,
        [FromForm] int? plantId,
        [FromForm] int? row,
        [FromForm] int? column,
        [FromForm] DateTime? capturedAtUtc,
        [FromForm] double? gddOverride,
        CancellationToken ct = default,
        [FromForm] int? circuitId = null)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { error = "No se proporcionó ningún archivo de imagen válido." });
        }

        var contentType = file.ContentType?.ToLowerInvariant() ?? string.Empty;
        if (!contentType.StartsWith("image/") &&
            !file.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
            !file.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !file.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) &&
            !file.FileName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "El archivo enviado no tiene un formato de imagen soportado (PNG, JPEG o WebP requerido)." });
        }

        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        // Resolver la planta asociada
        Plant? targetPlant = null;
        if (plantId.HasValue && plantId.Value > 0)
        {
            targetPlant = await context.Plants
                .Include(p => p.Lot)
                .ThenInclude(l => l!.CropType)
                .FirstOrDefaultAsync(p => p.Id == plantId.Value, ct);
        }
        else if (lotId.HasValue && row.HasValue && column.HasValue)
        {
            var plantQuery = context.Plants
                .Include(p => p.Lot)
                .ThenInclude(l => l!.CropType)
                .Where(p => p.LotId == lotId.Value && p.Row == row.Value && p.Column == column.Value);

            if (circuitId.HasValue)
            {
                plantQuery = plantQuery.Where(p => p.CircuitId == circuitId.Value);
                targetPlant = await plantQuery.FirstOrDefaultAsync(ct);
            }
            else
            {
                targetPlant = await plantQuery
                    .OrderByDescending(p => p.CircuitId == p.Lot!.CircuitId || p.CircuitId == null)
                    .FirstOrDefaultAsync(ct);
            }
        }
        else if (lotId.HasValue)
        {
            targetPlant = await context.Plants
                .Include(p => p.Lot)
                .ThenInclude(l => l!.CropType)
                .FirstOrDefaultAsync(p => p.LotId == lotId.Value && p.OperationalState == PlantOperationalState.Activa, ct)
                ?? await context.Plants.Include(p => p.Lot).FirstOrDefaultAsync(p => p.LotId == lotId.Value, ct);
        }

        if (targetPlant == null || targetPlant.Lot == null)
        {
            return BadRequest(new { error = "No se encontró una planta o lote válido para asociar la fotografía." });
        }

        // 1. Guardar la imagen en almacenamiento físico
        await using var uploadStream = file.OpenReadStream();
        var storagePath = await _storageService.SaveImageAsync(uploadStream, file.FileName, contentType, ct);

        // 2. Determinar contexto de GDD y configuración Baby Leaf del lote
        var lot = targetPlant.Lot;
        decimal lotGdd;
        if (gddOverride.HasValue)
        {
            lotGdd = (decimal)gddOverride.Value;
        }
        else
        {
            var calculatedGdd = await _gddService.GetAccumulatedGddAsync(lot, null, ct);
            if (calculatedGdd == 0m && lot.AccumulatedGdd is > 0m)
            {
                lotGdd = lot.AccumulatedGdd.Value;
            }
            else
            {
                lotGdd = calculatedGdd;
            }
        }

        var babyConfig = await context.BabyLeafConfigs
            .Where(c => c.CropTypeId == lot.CropTypeId && c.IsActive)
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync(ct);

        double gddMin = babyConfig != null ? (double)babyConfig.GddMin : 250.0;
        double gddMax = babyConfig != null ? (double)babyConfig.GddMax : 450.0;

        // Buscar última evaluación para calcular GrowthRate
        var previousEval = await context.BabyLeafEvaluations
            .Where(e => e.PlantId == targetPlant.Id)
            .OrderByDescending(e => e.EvaluatedAtUtc)
            .FirstOrDefaultAsync(ct);

        double? prevArea = previousEval?.FoliarAreaUsed.HasValue == true ? (double?)previousEval.FoliarAreaUsed.Value : null;
        double? prevGdd = previousEval != null ? (double?)previousEval.GddAtEvaluation : null;

        // 3. Ejecutar el pipeline de visión y árbol de decisión
        await using var inferenceStream = file.OpenReadStream();
        var evalResult = await _pipelineService.EvaluatePlantAsync(
            imageStream: inferenceStream,
            accumulatedGdd: (double)lotGdd,
            gddMin: gddMin,
            gddMax: gddMax,
            cmPerPixel: 0.045,
            previousAreaCm2: prevArea,
            previousGdd: prevGdd,
            ct: ct,
            scoreMinReady: babyConfig != null ? (double)babyConfig.ScoreMinReady : 70.0,
            scoreMinCandidate: babyConfig != null ? (double)babyConfig.ScoreMinCandidate : 45.0);

        // 4. Persistir PlantImage
        var plantImage = new PlantImage
        {
            PlantId = targetPlant.Id,
            CapturedAtUtc = capturedAtUtc ?? DateTime.UtcNow,
            Path = storagePath,
            FileName = file.FileName,
            Width = BabyLeafImageAnalyzer.ModelInputWidth,
            Height = BabyLeafImageAnalyzer.ModelInputHeight,
            Processed = true,
            ProcessedAtUtc = DateTime.UtcNow,
            ModelVersion = "BabyLeafMultiHead-2.0"
        };
        context.PlantImages.Add(plantImage);
        await context.SaveChangesAsync(ct);

        // 5. Persistir PlantImageAnalysis
        var visual = evalResult.VisualState;
        var morph = evalResult.Morphology;
        var quality = evalResult.ImageQuality;

        var analysis = new PlantImageAnalysis
        {
            PlantImageId = plantImage.Id,
            PlantArea = morph != null ? (decimal)morph.LeafAreaCm2 : null,
            FoliarArea = morph != null ? (decimal)morph.LeafAreaCm2 : null,
            LeafLength = morph != null ? (decimal)morph.MaxLeafLengthCm : null,
            RosetteDiameter = morph != null ? (decimal)morph.RosetteDiameterCm : null,
            LeafCount = morph != null ? morph.EstimatedLeafCount : null,
            CanopySolidity = morph != null ? (decimal)morph.CanopySolidity : null,
            SizeRatio = morph?.SizeRatio.HasValue == true ? (decimal)morph.SizeRatio.Value : null,
            LaplacianVariance = quality != null ? (decimal)quality.LaplacianVariance : null,
            VegetationCoverageRatio = quality != null ? (decimal)quality.VegetationCoverageRatio : null,
            ImageQuality = quality != null ? (decimal)(quality.IsReliable ? 1.0 : 0.0) : null,
            GrowthRate = evalResult.GrowthRateCm2PerGdd.HasValue ? (decimal)evalResult.GrowthRateCm2PerGdd.Value : null,
            PredominantClass = visual?.TopNpkClass,
            Confidence = visual != null ? (decimal)visual.NpkProbabilities.GetValueOrDefault(visual.TopNpkClass, 0f) : null,
            ProbabilitiesJson = visual != null ? JsonSerializer.Serialize(visual.NpkProbabilities) : null,
            HealthClass = visual?.TopHealthClass,
            HealthConfidence = visual != null ? (decimal)visual.HealthProbabilities.GetValueOrDefault(visual.TopHealthClass, 0f) : null,
            HealthProbabilitiesJson = visual != null ? JsonSerializer.Serialize(visual.HealthProbabilities) : null,
            BabyLeafScore = (decimal)evalResult.BabyLeafScore,
            BabyLeafStatus = evalResult.Status.ToString(),
            ModelVersion = "BabyLeafMultiHead-2.0",
            AnalyzedAtUtc = DateTime.UtcNow
        };
        context.PlantImageAnalyses.Add(analysis);

        // 6. Si estamos en ventana Baby Leaf y la imagen fue confiable, persistir BabyLeafEvaluation
        bool isBabyLeafApta = evalResult.Status == BabyLeafStageStatus.Apta;
        bool isCandidate = evalResult.Status == BabyLeafStageStatus.Candidata;

        if (evalResult.IsInTemporalWindow && quality?.IsReliable == true && babyConfig != null)
        {
            var evaluation = new BabyLeafEvaluation
            {
                PlantId = targetPlant.Id,
                BabyLeafConfigId = babyConfig.Id,
                PlantImageId = plantImage.Id,
                EvaluatedAtUtc = DateTime.UtcNow,
                BabyLeafScore = (decimal)evalResult.BabyLeafScore,
                Result = evalResult.Status switch
                {
                    BabyLeafStageStatus.Apta => "Baby Leaf",
                    BabyLeafStageStatus.Candidata => "Candidata Baby Leaf",
                    BabyLeafStageStatus.NoApta => "No Apta",
                    _ => "En desarrollo"
                },
                Confidence = visual != null ? (decimal)visual.CombinedVisualAptitude : 0.85m,
                ModelVersion = "BabyLeafMultiHead-2.0",
                GddAtEvaluation = lotGdd,
                MandatoryCriteriaMet = isBabyLeafApta,
                SizeRatio = morph?.SizeRatio.HasValue == true ? (decimal)morph.SizeRatio.Value : null,
                FoliarAreaUsed = morph != null ? (decimal)morph.LeafAreaCm2 : null,
                LeafLengthUsed = morph != null ? (decimal)morph.MaxLeafLengthCm : null,
                GrowthRateUsed = evalResult.GrowthRateCm2PerGdd.HasValue ? (decimal)evalResult.GrowthRateCm2PerGdd.Value : null
            };
            context.BabyLeafEvaluations.Add(evaluation);
        }

        // 7. Alertas de Anomalías Nutricionales o Fitosanitarias
        bool anomalyRaised = false;
        if (visual != null)
        {
            bool hasNpkDeficiency = visual.TopNpkClass != "FN" && visual.NpkProbabilities.GetValueOrDefault(visual.TopNpkClass, 0f) >= 0.70f;
            bool hasPathogen = visual.TopHealthClass != "Healthy" && visual.HealthProbabilities.GetValueOrDefault(visual.TopHealthClass, 0f) >= 0.70f;

            if (hasNpkDeficiency || hasPathogen)
            {
                string alertTitle = hasPathogen
                    ? $"Patógeno detectado: {BabyLeafPipelineService.HealthDescriptions.GetValueOrDefault(visual.TopHealthClass, visual.TopHealthClass)}"
                    : $"Deficiencia nutricional: {BabyLeafPipelineService.NpkDescriptions.GetValueOrDefault(visual.TopNpkClass, visual.TopNpkClass)}";

                if (targetPlant.OperationalState == PlantOperationalState.Activa)
                {
                    var riskStage = await context.CommercialStages
                        .FirstOrDefaultAsync(s => s.CropTypeId == lot.CropTypeId && s.Name == "Riesgo / fuera de ventana", ct)
                        ?? await context.CommercialStages.FirstOrDefaultAsync(s => s.Name == "Riesgo / fuera de ventana", ct);
                    if (riskStage != null && targetPlant.CommercialStageId != riskStage.Id)
                    {
                        var prevComm = targetPlant.CommercialStageId;
                        targetPlant.CommercialStageId = riskStage.Id;
                        context.PlantStageHistories.Add(new PlantStageHistory
                        {
                            PlantId = targetPlant.Id,
                            PreviousCommercialStageId = prevComm,
                            PreviousPhenologicalStageId = targetPlant.PhenologicalStageId,
                            PreviousOperationalState = targetPlant.OperationalState.ToString(),
                            NewCommercialStageId = riskStage.Id,
                            NewOperationalState = targetPlant.OperationalState.ToString(),
                            Source = "vision",
                            Reason = alertTitle,
                            ChangedAtUtc = DateTime.UtcNow
                        });
                    }
                }

                string ruleCode = hasPathogen ? $"PATHOGEN_{visual.TopHealthClass.ToUpperInvariant()}" : $"DEFICIENCY_{visual.TopNpkClass.TrimStart('-')}";
                decimal confidenceVal = (decimal)(hasPathogen ? visual.HealthProbabilities.GetValueOrDefault(visual.TopHealthClass, 0f) : visual.NpkProbabilities.GetValueOrDefault(visual.TopNpkClass, 0f));

                var anomaly = new AnomalyEvent
                {
                    Type = hasPathogen ? "PATOGENO_FOLIAR_VISUAL" : "DEFICIENCIA_NUTRICIONAL_VISUAL",
                    LotId = targetPlant.LotId,
                    Severity = confidenceVal >= 0.88m ? AnomalyContract.SeverityCritica : AnomalyContract.SeverityAdvertencia,
                    ObservedValue = confidenceVal,
                    RuleCode = ruleCode,
                    RuleDescription = $"{alertTitle} en planta #{targetPlant.Id} (F{targetPlant.Row}-C{targetPlant.Column}, Lote #{targetPlant.LotId}).",
                    FirstObservedAtUtc = DateTime.UtcNow,
                    LastObservedAtUtc = DateTime.UtcNow,
                    ReadingCount = 1,
                    Status = AnomalyContract.StatusAbierta
                };
                context.AnomalyEvents.Add(anomaly);
                anomalyRaised = true;

                var alertType = hasPathogen ? NotificationTypes.AlertaSanitaria : NotificationTypes.DesbalanceQuimico;
                var severityType = confidenceVal >= 0.88m ? NotificationSeverities.Critica : NotificationSeverities.Alta;
                var fingerprint = $"visual-alert-{targetPlant.Id}-{ruleCode}-{DateTime.UtcNow:yyyyMMdd}";

                await _notificationService.CreateAlertAsync(new CreateAlertDto(
                    alertType,
                    severityType,
                    alertTitle,
                    $"Diagnóstico visual ONNX en planta #{targetPlant.Id}: {evalResult.Recommendation} (Certeza: {confidenceVal:P1}).",
                    fingerprint,
                    targetPlant.LotId
                ), ct);
            }
            else
            {
                // Planta sana: si no quedan otras plantas activas con patógeno o deficiencia, cerrar anomalías visuales
                var activePlantIds = await context.Plants
                    .Where(p => p.LotId == targetPlant.LotId && p.OperationalState == PlantOperationalState.Activa)
                    .Select(p => p.Id)
                    .ToListAsync(ct);

                var otherImages = await context.PlantImages
                    .AsNoTracking()
                    .Include(i => i.Analysis)
                    .Where(i => activePlantIds.Contains(i.PlantId) && i.PlantId != targetPlant.Id && i.Analysis != null)
                    .OrderByDescending(i => i.CapturedAtUtc)
                    .ThenByDescending(i => i.Id)
                    .ToListAsync(ct);

                var latestByOtherPlant = otherImages
                    .GroupBy(i => i.PlantId)
                    .ToDictionary(g => g.Key, g => g.First());

                bool anyOtherHasVisualRisk = latestByOtherPlant.Values.Any(img =>
                {
                    var a = img.Analysis;
                    if (a == null) return false;
                    bool path = a.HealthClass != null && a.HealthClass != "Healthy" && (a.HealthConfidence ?? 0m) >= 0.70m;
                    bool def = a.PredominantClass != null && a.PredominantClass != "FN" && (a.Confidence ?? 0m) >= 0.70m;
                    return path || def;
                });

                if (!anyOtherHasVisualRisk)
                {
                    var openVisualAnomalies = await context.AnomalyEvents
                        .Where(e => e.LotId == targetPlant.LotId
                            && (e.Status == AnomalyContract.StatusAbierta || e.Status == AnomalyContract.StatusReconocida)
                            && (e.Type == "PATOGENO_FOLIAR_VISUAL" || e.Type == "DEFICIENCIA_NUTRICIONAL_VISUAL"))
                        .ToListAsync(ct);

                    foreach (var anomaly in openVisualAnomalies)
                    {
                        anomaly.Status = AnomalyContract.StatusResuelta;
                        anomaly.ResolvedAtUtc = DateTime.UtcNow;
                        anomaly.ResolutionReason = "Cerrada automáticamente: la planta analizada y las demás plantas activas del lote se encuentran sanas.";
                    }
                }

                // Recalcular estado comercial de la planta y del lote
                if (_lotDailyFlowService != null)
                {
                    await context.SaveChangesAsync(ct);
                    await _lotDailyFlowService.RunDailyAsync(targetPlant.LotId, null, ct);
                }

                // Evaluar targetPlant con PlantDecisionEngine.Evaluate usando el lotGdd efectivo
                if (targetPlant.OperationalState == PlantOperationalState.Activa)
                {
                    var thresholds = await PlantEvaluationService.ResolveThresholdsAsync(context, lot.CropTypeId, ct)
                        ?? new PlantDecisionEngine.PlantDecisionThresholds(
                            BabyLeafGddMin: babyConfig != null ? babyConfig.GddMin : 250m,
                            BabyLeafGddMax: babyConfig != null ? babyConfig.GddMax : 450m,
                            OvermaturityGdd: 750m,
                            ScoreMinCandidate: babyConfig != null ? babyConfig.ScoreMinCandidate : 45m,
                            ScoreMinReady: babyConfig != null ? babyConfig.ScoreMinReady : 70m);

                    var phenoStage = await PlantEvaluationService.ResolvePhenologicalStageAsync(context, lot.CropTypeId, lotGdd, ct);

                    var decision = PlantDecisionEngine.Evaluate(
                        new PlantDecisionEngine.PlantEvaluationInput(
                            LotGdd: lotGdd,
                            BabyLeafScore: (decimal)evalResult.BabyLeafScore,
                            MandatoryCriteriaMet: isBabyLeafApta,
                            HasRisk: false,
                            RiskReason: null,
                            IsAdultRoute: targetPlant.IsAdultRoute,
                            SizeRatio: morph?.SizeRatio.HasValue == true ? (decimal)morph.SizeRatio.Value : null),
                        thresholds);

                    var targetCommercialStage = await context.CommercialStages
                        .Where(s => s.CropTypeId == lot.CropTypeId && s.Name == decision.CommercialStageName && s.IsActive)
                        .FirstOrDefaultAsync(ct);

                    int? newCommId = targetCommercialStage?.Id;
                    int? newPhenoId = phenoStage?.Id;

                    bool commChanged = newCommId.HasValue && targetPlant.CommercialStageId != newCommId.Value;
                    bool phenoChanged = newPhenoId.HasValue && targetPlant.PhenologicalStageId != newPhenoId.Value;

                    int? prevComm = targetPlant.CommercialStageId;
                    int? prevPheno = targetPlant.PhenologicalStageId;

                    if (commChanged)
                    {
                        targetPlant.CommercialStageId = newCommId!.Value;
                    }
                    if (phenoChanged)
                    {
                        targetPlant.PhenologicalStageId = newPhenoId!.Value;
                    }

                    if (commChanged || phenoChanged)
                    {
                        context.PlantStageHistories.Add(new PlantStageHistory
                        {
                            PlantId = targetPlant.Id,
                            PreviousCommercialStageId = prevComm,
                            PreviousPhenologicalStageId = prevPheno,
                            PreviousOperationalState = targetPlant.OperationalState.ToString(),
                            NewCommercialStageId = commChanged ? newCommId : null,
                            NewPhenologicalStageId = phenoChanged ? newPhenoId : null,
                            NewOperationalState = targetPlant.OperationalState.ToString(),
                            Source = "vision",
                            Reason = decision.Reason,
                            ChangedAtUtc = DateTime.UtcNow
                        });
                    }

                    // Actualizar predominancia comercial del lote sobre plantas activas
                    var activePlants = await context.Plants
                        .Where(p => p.LotId == targetPlant.LotId && p.OperationalState == PlantOperationalState.Activa)
                        .ToListAsync(ct);

                    var stageNames = await context.CommercialStages
                        .Where(s => s.CropTypeId == lot.CropTypeId)
                        .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

                    var activeCounts = new Dictionary<string, int>();
                    foreach (var p in activePlants)
                    {
                        var name = p.CommercialStageId is { } sId && stageNames.TryGetValue(sId, out var resolved)
                            ? resolved
                            : "(sin estado)";
                        activeCounts[name] = activeCounts.GetValueOrDefault(name) + 1;
                    }

                    var (predominantName, isMixed) = Predominance.Resolve(activeCounts);
                    var predominantStage = !isMixed && predominantName is not null
                        ? await context.CommercialStages
                            .Where(s => s.CropTypeId == lot.CropTypeId && s.Name == predominantName)
                            .FirstOrDefaultAsync(ct)
                        : null;

                    lot.PredominantCommercialStageId = predominantStage?.Id;
                    lot.IsCommercialStageMixed = isMixed;
                }
            }
        }

        await context.SaveChangesAsync(ct);

        LotViewCache.InvalidatePlant(targetPlant.Id, targetPlant.LotId);

        _logger.LogInformation("Imagen #{ImageId} analizada para planta #{PlantId}: Status={Status}, BLS={BLS}, NPK={NPK}, Health={Health}",
            plantImage.Id, targetPlant.Id, evalResult.Status, evalResult.BabyLeafScore, visual?.TopNpkClass, visual?.TopHealthClass);

        return StatusCode(StatusCodes.Status201Created, new
        {
            imageId = plantImage.Id,
            plantId = targetPlant.Id,
            lotId = targetPlant.LotId,
            storageUrl = _storageService.GetPublicUrl(storagePath),
            status = evalResult.Status.ToString(),
            babyLeafScore = evalResult.BabyLeafScore,
            accumulatedGdd = evalResult.AccumulatedGdd,
            isInTemporalWindow = evalResult.IsInTemporalWindow,
            quality = evalResult.ImageQuality,
            morphology = evalResult.Morphology,
            growthRate = evalResult.GrowthRateCm2PerGdd,
            growthFactor = evalResult.GrowthFactor,
            visualState = evalResult.VisualState,
            recommendation = evalResult.Recommendation,
            anomalyRaised = anomalyRaised
        });
    }

    [HttpPost("/api/plantas/{plantId:int}/imagenes")]
    [RequestSizeLimit(25_000_000)]
    public Task<IActionResult> UploadPlantImage(
        int plantId,
        [FromForm] IFormFile? file,
        [FromForm] DateTime? capturedAtUtc,
        [FromForm] double? gddOverride,
        CancellationToken ct = default,
        [FromForm] int? circuitId = null)
    {
        return UploadImage(file, null, plantId, null, null, capturedAtUtc, gddOverride, ct, circuitId);
    }
}
