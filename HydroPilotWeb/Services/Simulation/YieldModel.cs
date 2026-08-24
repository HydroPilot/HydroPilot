using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Forecasting;

namespace HydroPilotWeb.Services.Simulation;

/// <summary>
/// Modelo de rendimiento del escenario (SIM-04): escenarios conservador/base/
/// optimista reutilizando el núcleo de forecasting. Con lote: YieldService
/// (historial de ciclos cerrados del cultivo, o baseline con confianza
/// heurística). Escenario libre: baseline del cultivo con escenarios fijos ±15%
/// (YieldMath), siempre etiquetado como heurístico si no hay ciclos cerrados.
/// </summary>
public class YieldModel
{
    private readonly YieldService _yieldService;

    public YieldModel(YieldService yieldService)
    {
        _yieldService = yieldService;
    }

    public async Task<(YieldEstimate Estimate, IReadOnlyList<string> Warnings)> EstimateAsync(
        Lot? lot,
        CropType? crop,
        decimal accumulatedGdd,
        decimal areaM2,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();
        YieldEstimate estimate;

        if (lot is not null)
        {
            // Reutiliza el historial real de ciclos cerrados del mismo cultivo (F-05).
            estimate = await _yieldService.EstimateAsync(lot, accumulatedGdd, ct);
            if (estimate.HistoryCycles == 0)
            {
                warnings.Add("Sin historial de rendimiento suficiente (menos de 2 ciclos cerrados del cultivo): escenarios ±15% sobre el baseline con confianza heurística.");
            }
        }
        else
        {
            var target = crop?.GddTarget ?? 300m;
            var progress = target > 0 ? Math.Clamp(accumulatedGdd / target, 0m, 1m) : 0m;
            estimate = YieldMath.BaselineScenarios(crop?.YieldPerM2 ?? 3.0m, areaM2, progress);
            if (estimate.HistoryCycles == 0)
            {
                warnings.Add("Escenario libre: sin historial de ciclos cerrados: rendimiento = baseline del cultivo en escenarios fijos ±15% (confianza heurística).");
            }
        }

        return (estimate, warnings);
    }
}