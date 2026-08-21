using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Services;

/// <summary>
/// Aplica el árbol de decisión (plan 09 / LOT-07) a una planta ACTIVA y persiste
/// el resultado (etapa comercial/fenológica + histórico). La lógica de decisión
/// vive en PlantDecisionEngine (pura); esta clase solo la orquesta sobre la base
/// de datos y respeta la regla de no tocar plantas cosechadas o descartadas.
///
/// Este flujo NO se ejecuta desde Razor: los componentes llaman a sus servicios
/// (LotAggregateService para leer; LotDailyFlowService para correr el flujo).
/// </summary>
public class PlantEvaluationService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;

    public PlantEvaluationService(IDbContextFactory<HydroPilotDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Resuelve los umbrales desde los catálogos (LOT-02): configuración Baby Leaf
    /// activa del cultivo + GddMax de la última etapa fenológica activa (sobremadurez).
    /// Retorna null si faltan catálogos (el flujo no inventa umbrales).
    /// </summary>
    public static async Task<PlantDecisionEngine.PlantDecisionThresholds?> ResolveThresholdsAsync(
        HydroPilotDbContext context,
        int cropTypeId,
        CancellationToken ct = default)
    {
        var config = await context.BabyLeafConfigs
            .Where(c => c.CropTypeId == cropTypeId && c.IsActive)
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync(ct);

        if (config is null)
            return null;

        var lastStage = await context.PhenologicalStages
            .Where(s => s.CropTypeId == cropTypeId && s.IsActive)
            .OrderByDescending(s => s.Order)
            .FirstOrDefaultAsync(ct);

        if (lastStage is null)
            return null;

        return new PlantDecisionEngine.PlantDecisionThresholds(
            config.GddMin,
            config.GddMax,
            lastStage.GddMax,
            config.ScoreMinCandidate,
            config.ScoreMinReady);
    }

    /// <summary>Etapa fenológica según GDD (ventanas del catálogo). Null si no hay catálogo.</summary>
    public static async Task<PhenologicalStage?> ResolvePhenologicalStageAsync(
        HydroPilotDbContext context,
        int cropTypeId,
        decimal gdd,
        CancellationToken ct = default)
    {
        var stages = await context.PhenologicalStages
            .Where(s => s.CropTypeId == cropTypeId && s.IsActive)
            .OrderBy(s => s.Order)
            .ToListAsync(ct);

        if (stages.Count == 0)
            return null;

        // GDD por debajo de la primera etapa → primera; por encima de la última → última
        // (con advertencia de sobremadurez que evalúa el motor de decisión).
        var match = stages.FirstOrDefault(s => gdd >= s.GddMin && gdd < s.GddMax)
                    ?? stages[^1];
        return match;
    }

    /// <summary>
    /// Evalúa una planta Activa dentro de un contexto transaccional provisto por el
    /// caller (flujo diario). No cambia plantas cosechadas/descartadas.
    /// Retorna true si se persistió algún cambio.
    /// </summary>
    public async Task<bool> EvaluateActivePlantAsync(
        HydroPilotDbContext context,
        Plant plant,
        int cropTypeId,
        int phenologicalStageId,
        decimal lotGdd,
        PlantDecisionEngine.PlantDecisionThresholds thresholds,
        PlantRisk? risk,
        CancellationToken ct = default)
    {
        if (plant.OperationalState != PlantOperationalState.Activa)
            return false; // Estado histórico congelado: no se vuelve a evaluar.

        var lastEvaluation = await context.BabyLeafEvaluations
            .Where(e => e.PlantId == plant.Id)
            .OrderByDescending(e => e.EvaluatedAtUtc)
            .FirstOrDefaultAsync(ct);

        var decision = PlantDecisionEngine.Evaluate(
            new PlantDecisionEngine.PlantEvaluationInput(
                LotGdd: lotGdd,
                BabyLeafScore: lastEvaluation?.BabyLeafScore,
                MandatoryCriteriaMet: lastEvaluation?.MandatoryCriteriaMet,
                HasSufficientMaturity: null, // madurez solo la confirma el análisis de imagen (fase futura)
                HasRisk: risk is not null,
                RiskReason: risk?.Reason),
            thresholds);

        var targetCommercialStage = await context.CommercialStages
            .Where(s => s.CropTypeId == cropTypeId && s.Name == decision.CommercialStageName && s.IsActive)
            .FirstOrDefaultAsync(ct);

        var phenoChanged = plant.PhenologicalStageId != phenologicalStageId;
        var commChanged = targetCommercialStage is not null
                          && plant.CommercialStageId != targetCommercialStage.Id;

        if (!phenoChanged && !commChanged)
            return false;

        var previousPheno = plant.PhenologicalStageId;
        var previousComm = plant.CommercialStageId;

        if (phenoChanged)
            plant.PhenologicalStageId = phenologicalStageId;
        if (commChanged)
            plant.CommercialStageId = targetCommercialStage!.Id;

        context.PlantStageHistories.Add(new PlantStageHistory
        {
            PlantId = plant.Id,
            PreviousPhenologicalStageId = previousPheno,
            PreviousCommercialStageId = previousComm,
            PreviousOperationalState = plant.OperationalState.ToString(),
            NewPhenologicalStageId = phenoChanged ? phenologicalStageId : null,
            NewCommercialStageId = commChanged ? targetCommercialStage!.Id : null,
            NewOperationalState = plant.OperationalState.ToString(),
            Source = "daily-flow",
            Reason = decision.Reason,
            ChangedAtUtc = DateTime.UtcNow
        });

        return true;
    }
}