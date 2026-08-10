using HydroPilotWeb.Models;

namespace HydroPilotWeb.Services;

/// <summary>
/// Estima el rendimiento de un lote a partir del rendimiento base del cultivo
/// y el área plantada, con escenarios conservador/base/optimista.
/// </summary>
public class YieldService
{
    public record YieldEstimate(
        decimal Conservative,
        decimal Base,
        decimal Optimistic,
        int ConfidencePercent);

    private const decimal ConservativeFactor = 0.85m;
    private const decimal OptimisticFactor = 1.15m;

    public YieldEstimate Estimate(Lot lot, decimal accumulatedGdd)
    {
        var yieldPerM2 = lot.CropType?.YieldPerM2 ?? 3.0m;
        var target = lot.CropType?.GddTarget ?? 300m;
        var area = lot.PlantedAreaM2;

        var baseYield = yieldPerM2 * area;

        // Ajuste por avance del ciclo: a más GDD acumulado, más confianza
        var progress = target > 0 ? accumulatedGdd / target : 0m;
        var confidence = 80;
        if (progress >= 0.5m) confidence += 6;
        if (progress >= 0.8m) confidence += 6;

        return new YieldEstimate(
            Conservative: Math.Round(baseYield * ConservativeFactor, 2),
            Base: Math.Round(baseYield, 2),
            Optimistic: Math.Round(baseYield * OptimisticFactor, 2),
            ConfidencePercent: confidence);
    }
}
