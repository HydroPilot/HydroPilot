using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Forecasting;

namespace HydroPilotWeb.Services;

/// <summary>
/// Estima el rendimiento de un lote (F-05).
/// Con historial real (≥2 lotes COSECHADO del mismo cultivo, con área y
/// rendimiento válidos, excluyendo el lote actual): promedio de kg/m² con
/// escenarios ±1σ y confianza heurística por cantidad de ciclos.
/// Sin historial suficiente: baseline del cultivo con escenarios fijos ±15%.
/// Nunca negativos ni división por cero.
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

    public async Task<YieldEstimate> EstimateAsync(Lot lot, decimal accumulatedGdd, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        // F-05: SOLO lotes cosechados del mismo cultivo, con área y rendimiento
        // válidos (no negativos), y EXCLUYENDO el lote actual del historial.
        var history = await context.Lots
            .Where(l => l.Id != lot.Id)
            .Where(l => l.CropTypeId == lot.CropTypeId)
            .Where(l => l.Status!.Name == "COSECHADO")
            .Where(l => l.ActualYieldKg.HasValue && l.ActualYieldKg >= 0)
            .Where(l => l.PlantedAreaM2 > 0)
            .Select(l => new { l.ActualYieldKg, l.PlantedAreaM2 })
            .ToListAsync(ct);

        var area = lot.PlantedAreaM2 > 0 ? lot.PlantedAreaM2 : 0m;

        if (history.Count >= MinHistoryCycles)
        {
            var yieldsPerM2 = history
                .Select(h => h.ActualYieldKg!.Value / h.PlantedAreaM2)
                .ToList();

            return YieldMath.HistoryScenarios(yieldsPerM2, history.Count, area);
        }

        // Sin historial suficiente: baseline del cultivo (F-05: confianza heurística).
        var yieldPerM2 = lot.CropType?.YieldPerM2 ?? 3.0m;
        var target = lot.CropType?.GddTarget ?? 300m;
        var progress = target > 0 ? accumulatedGdd / target : 0m;
        return YieldMath.BaselineScenarios(yieldPerM2, area, progress);
    }
}

/// <summary>Matemática pura de escenarios de rendimiento (F-05), testeable sin I/O.</summary>
public static class YieldMath
{
    /// <summary>
    /// Escenarios con historial (≥2 ciclos): promedio kg/m² × área, ±1σ (pisos
    /// conservador/optimista ±10% para no colapsar el rango), confianza heurística
    /// 60 + 8×N (máx 95).
    /// </summary>
    public static YieldEstimate HistoryScenarios(
        IReadOnlyList<decimal> yieldsPerM2,
        int historyCycles,
        decimal area)
    {
        if (yieldsPerM2.Count == 0 || area <= 0)
            return BaselineScenarios(3.0m, area, 0m);

        var mean = (double)yieldsPerM2.Average();
        var stdDev = SampleStdDev(yieldsPerM2, mean);
        var baseYield = (decimal)mean * area;

        // ±1σ (con piso/máximo relativos para no colapsar el rango).
        var meanFactor = mean > 0 ? mean : 1d;
        var conservativeFactor = Math.Max(0.80m, 1m - (decimal)(stdDev / meanFactor));
        var optimisticFactor = Math.Min(1.20m, 1m + (decimal)(stdDev / meanFactor));

        // Confianza heurística: más ciclos cerrados = más confianza (máx 95).
        var confidence = Math.Min(95, 60 + historyCycles * 8);

        return new YieldEstimate(
            Conservative: Math.Round(Math.Max(0m, baseYield * conservativeFactor), 2, MidpointRounding.AwayFromZero),
            Base: Math.Round(Math.Max(0m, baseYield), 2, MidpointRounding.AwayFromZero),
            Optimistic: Math.Round(Math.Max(0m, baseYield * optimisticFactor), 2, MidpointRounding.AwayFromZero),
            ConfidencePercent: confidence,
            HistoryCycles: historyCycles);
    }

    /// <summary>
    /// Baseline del cultivo (0/1 ciclos): rendimiento base × área con escenarios
    /// fijos ±15%; confianza heurística por avance del ciclo (sin inventar ciclos).
    /// </summary>
    public static YieldEstimate BaselineScenarios(decimal yieldPerM2, decimal area, decimal progress)
    {
        var baseYield = yieldPerM2 * area;

        var confidence = 80;
        if (progress >= 0.5m) confidence += 6;
        if (progress >= 0.8m) confidence += 6;

        return new YieldEstimate(
            Conservative: Math.Round(Math.Max(0m, baseYield * 0.85m), 2, MidpointRounding.AwayFromZero),
            Base: Math.Round(Math.Max(0m, baseYield), 2, MidpointRounding.AwayFromZero),
            Optimistic: Math.Round(Math.Max(0m, baseYield * 1.15m), 2, MidpointRounding.AwayFromZero),
            ConfidencePercent: confidence,
            HistoryCycles: 0);
    }

    /// <summary>Desviación estándar muestral (n-1); 0 con menos de 2 valores.</summary>
    public static double SampleStdDev(IReadOnlyList<decimal> values, double mean)
    {
        if (values.Count < 2) return 0;
        var sumSq = values.Sum(v => Math.Pow((double)v - mean, 2));
        return Math.Sqrt(sumSq / (values.Count - 1));
    }
}