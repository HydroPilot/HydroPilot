using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Lotes;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Services.Anomalies;

/// <summary>
/// Implementación real del contrato IPlantRiskProvider (ANO-07). Reemplaza al
/// NoPlantRiskProvider en DI (el registro del módulo de anomalías va después del
/// de lotes: último registro gana). Devuelve riesgo para cada planta ACTIVA del
/// lote cuando hay episodios abiertos/reconocidos de la solución compartida.
/// </summary>
public sealed class AnomalyRiskProvider : IPlantRiskProvider
{
    private readonly AnomalyLotRiskEvaluator _riskEvaluator;

    public AnomalyRiskProvider(AnomalyLotRiskEvaluator riskEvaluator)
    {
        _riskEvaluator = riskEvaluator;
    }

    public async Task<IReadOnlyDictionary<int, PlantRisk>> GetActiveRisksAsync(int lotId, CancellationToken ct = default)
    {
        var state = await _riskEvaluator.EvaluateAsync(lotId, ct);
        if (state.OpenEpisodes.Count == 0 || state.RiskReason is null)
            return new Dictionary<int, PlantRisk>();

        var activePlantIds = await _riskEvaluator.GetActivePlantIdsAsync(lotId, ct);

        var risks = new Dictionary<int, PlantRisk>(activePlantIds.Count);
        foreach (var plantId in activePlantIds)
        {
            risks[plantId] = new PlantRisk(plantId, state.RiskReason, Source: "anomalies");
        }
        return risks;
    }
}