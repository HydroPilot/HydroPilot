using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.Vision;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Services.Anomalies;

/// <summary>
/// Implementación real del contrato IPlantRiskProvider (ANO-07). Reemplaza al
/// NoPlantRiskProvider en DI (el registro del módulo de anomalías va después del
/// de lotes: último registro gana). Devuelve riesgo para cada planta ACTIVA del
/// lote cuando hay episodios abiertos/reconocidos de la solución compartida,
/// y riesgos específicos visuales por patógenos/deficiencias en plantas individuales.
/// </summary>
public sealed class AnomalyRiskProvider : IPlantRiskProvider
{
    private readonly AnomalyLotRiskEvaluator _riskEvaluator;
    private readonly IDbContextFactory<HydroPilotDbContext>? _dbFactory;

    public AnomalyRiskProvider(AnomalyLotRiskEvaluator riskEvaluator, IDbContextFactory<HydroPilotDbContext>? dbFactory = null)
    {
        _riskEvaluator = riskEvaluator;
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyDictionary<int, PlantRisk>> GetActiveRisksAsync(int lotId, CancellationToken ct = default)
    {
        var activePlantIds = await _riskEvaluator.GetActivePlantIdsAsync(lotId, ct);
        var risks = new Dictionary<int, PlantRisk>(activePlantIds.Count);

        var state = await _riskEvaluator.EvaluateAsync(lotId, ct);
        if (state.OpenEpisodes.Count > 0 && state.RiskReason is not null)
        {
            foreach (var plantId in activePlantIds)
            {
                risks[plantId] = new PlantRisk(plantId, state.RiskReason, Source: "anomalies");
            }
        }

        if (_dbFactory is not null && activePlantIds.Count > 0)
        {
            await using var context = await _dbFactory.CreateDbContextAsync(ct);

            var images = await context.PlantImages
                .AsNoTracking()
                .Include(i => i.Analysis)
                .Where(i => activePlantIds.Contains(i.PlantId) && i.Analysis != null)
                .OrderByDescending(i => i.CapturedAtUtc)
                .ThenByDescending(i => i.Id)
                .ToListAsync(ct);

            var latestByPlant = images
                .GroupBy(i => i.PlantId)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var (plantId, lastImg) in latestByPlant)
            {
                var analysis = lastImg.Analysis;
                if (analysis is null) continue;

                bool hasPathogen = analysis.HealthClass != null
                    && analysis.HealthClass != "Healthy"
                    && (analysis.HealthConfidence ?? 0m) >= 0.70m;

                bool hasDeficiency = analysis.PredominantClass != null
                    && analysis.PredominantClass != "FN"
                    && (analysis.Confidence ?? 0m) >= 0.70m;

                if (hasPathogen || hasDeficiency)
                {
                    string healthDesc = BabyLeafPipelineService.HealthDescriptions.TryGetValue(analysis.HealthClass!, out var hd) ? hd : analysis.HealthClass!;
                    string npkDesc = BabyLeafPipelineService.NpkDescriptions.TryGetValue(analysis.PredominantClass!, out var nd) ? nd : analysis.PredominantClass!;

                    string reason = (hasPathogen && hasDeficiency)
                        ? $"Patógeno ({healthDesc}) y deficiencia nutricional ({npkDesc})"
                        : hasPathogen
                            ? $"Patógeno detectado: {healthDesc}"
                            : $"Deficiencia nutricional: {npkDesc}";

                    risks[plantId] = new PlantRisk(plantId, reason, Source: "vision");
                }
            }
        }

        return risks;
    }
}