using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;

namespace HydroPilotWeb.Services;

/// <summary>
/// Estima el rendimiento de un lote.
/// Si hay historial real (≥2 lotes COSECHADO del mismo cultivo con rendimiento registrado),
/// usa el rendimiento por m² promedio con escenarios ±desviación estándar.
/// Sin historial, usa el rendimiento base del cultivo con escenarios fijos ±15%.
/// </summary>
public class YieldService
{
    private const int MinHistoryCycles = 2;
    private const decimal BaseConservativeFactor = 0.85m;
    private const decimal BaseOptimisticFactor = 1.15m;

    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;

    public YieldService(IDbContextFactory<HydroPilotDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public record YieldEstimate(
        decimal Conservative,
        decimal Base,
        decimal Optimistic,
        int ConfidencePercent,
        int HistoryCycles);

    public async Task<YieldEstimate> EstimateAsync(Lot lot, decimal accumulatedGdd, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var history = await context.Lots
            .Where(l => l.Id != lot.Id)
            .Where(l => l.CropTypeId == lot.CropTypeId)
            .Where(l => l.ActualYieldKg.HasValue && l.PlantedAreaM2 > 0)
            .Select(l => new { l.ActualYieldKg, l.PlantedAreaM2 })
            .ToListAsync(ct);

        var area = lot.PlantedAreaM2;

        if (history.Count >= MinHistoryCycles)
        {
            // Rendimiento por m² de cada ciclo histórico
            var yieldsPerM2 = history
                .Select(h => h.ActualYieldKg!.Value / h.PlantedAreaM2)
                .ToList();

            var mean = (double)yieldsPerM2.Average();
            var stdDev = StdDev(yieldsPerM2, mean);
            var baseYield = (decimal)mean * area;

            // Escenarios: ±1 desviación estándar (mínimo ±10% para no colapsar el rango)
            var meanFactor = mean > 0 ? mean : 1d;
            var conservativeFactor = Math.Max(0.85m, 1m - (decimal)(stdDev / meanFactor));
            var optimisticFactor = Math.Min(1.15m, 1m + (decimal)(stdDev / meanFactor));

            // Confianza: más ciclos = más confianza (60% + 8% por ciclo extra, máx 95%)
            var confidence = Math.Min(95, 60 + history.Count * 8);

            return new YieldEstimate(
                Conservative: Math.Round(baseYield * conservativeFactor, 2),
                Base: Math.Round(baseYield, 2),
                Optimistic: Math.Round(baseYield * optimisticFactor, 2),
                ConfidencePercent: confidence,
                HistoryCycles: history.Count);
        }

        // Sin historial: constante del cultivo con escenarios fijos
        var yieldPerM2 = lot.CropType?.YieldPerM2 ?? 3.0m;
        var baseYieldNoHistory = yieldPerM2 * area;

        var target = lot.CropType?.GddTarget ?? 300m;
        var progress = target > 0 ? accumulatedGdd / target : 0m;
        var confidenceNoHistory = 80;
        if (progress >= 0.5m) confidenceNoHistory += 6;
        if (progress >= 0.8m) confidenceNoHistory += 6;

        return new YieldEstimate(
            Conservative: Math.Round(baseYieldNoHistory * BaseConservativeFactor, 2),
            Base: Math.Round(baseYieldNoHistory, 2),
            Optimistic: Math.Round(baseYieldNoHistory * BaseOptimisticFactor, 2),
            ConfidencePercent: confidenceNoHistory,
            HistoryCycles: 0);
    }

    private static double StdDev(List<decimal> values, double mean)
    {
        if (values.Count < 2) return 0;
        var sumSq = values.Sum(v => Math.Pow((double)v - mean, 2));
        return Math.Sqrt(sumSq / (values.Count - 1));
    }
}
