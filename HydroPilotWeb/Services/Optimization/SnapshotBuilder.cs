using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HydroPilotWeb.Models.Optimization;

namespace HydroPilotWeb.Services.Optimization;

/// <summary>
/// Construye el <see cref="OptimizationSnapshot"/> y su hash canónico (OPT-01/OPT-04).
///
/// El hash ignora GeneratedAtUtc y RuleVersion NO participa de la identidad de
/// entradas (solo la registra): repetir el cálculo con el mismo lote, lecturas,
/// GDD, rendimiento y catálogo produce el MISMO hash ⇒ la misma recomendación
/// (idempotencia: no se duplica sin cambio de snapshot). Cualquier cambio de
/// entrada (lectura nueva, GDD, precios...) cambia el hash ⇒ recomendación nueva.
/// </summary>
public static class OptimizationSnapshotBuilder
{
    /// <summary>
    /// Hash SHA-256 hex del árbol canónico (diccionarios ordenados, cultura
    /// invariante, decimals con formato estable). Determinista entre corridas.
    /// </summary>
    public static string ComputeHash(OptimizationSnapshot snapshot)
    {
        var canonical = BuildCanonical(snapshot);
        var json = JsonSerializer.Serialize(canonical, new JsonSerializerOptions { WriteIndented = false });
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>JSON canónico del snapshot (para persistir SnapshotJson con el mismo orden).</summary>
    public static string ToCanonicalJson(OptimizationSnapshot snapshot)
    {
        var canonical = BuildCanonical(snapshot);
        return JsonSerializer.Serialize(canonical, new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>
    /// Árbol SortedDictionary (claves ordenadas, cultura invariante): garantiza
    /// serialización estable sin depender del orden de declaración de los records.
    /// Todas las fechas en formato "yyyy-MM-dd HH:mm:ss 'Z'"/"yyyy-MM-dd".
    /// </summary>
    private static SortedDictionary<string, object?> BuildCanonical(OptimizationSnapshot s)
    {
        var readings = s.Readings
            .OrderBy(r => r.SensorType, StringComparer.Ordinal)
            .ThenBy(r => r.ObservedAtUtc)
            .Select(r => (object)new SortedDictionary<string, object?>
            {
                ["sensorType"] = r.SensorType,
                ["quality"] = r.Quality,
                ["value"] = N(r.Value),
                ["observedAtUtc"] = r.ObservedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            })
            .ToList();

        var priceCosts = s.PriceCosts
            .OrderBy(p => p.Destination, StringComparer.Ordinal)
            .ThenBy(p => p.Item, StringComparer.Ordinal)
            .Select(p => (object)new SortedDictionary<string, object?>
            {
                ["destination"] = p.Destination,
                ["item"] = p.Item,
                ["value"] = N(p.Value),
                ["currency"] = p.Currency,
                ["source"] = p.Source,
                ["validFrom"] = p.ValidFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["validUntil"] = p.ValidUntil?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            })
            .ToList();

        return new SortedDictionary<string, object?>
        {
            ["ruleVersion"] = s.RuleVersion,
            ["lotId"] = s.LotId,
            ["lotName"] = s.LotName,
            ["cropTypeName"] = s.CropTypeName,
            ["areaM2"] = N(s.AreaM2),
            ["statusName"] = s.StatusName,
            ["sowingDate"] = s.SowingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["readings"] = readings,
            ["currentPh"] = N(s.CurrentPh),
            ["currentEc"] = N(s.CurrentEc),
            ["phUsableCount"] = s.PhUsableCount,
            ["ecUsableCount"] = s.EcUsableCount,
            ["phAgeMinutes"] = s.PhAgeMinutes,
            ["ecAgeMinutes"] = s.EcAgeMinutes,
            ["gddAccumulated"] = N(s.GddAccumulated),
            ["gddTarget"] = N(s.GddTarget),
            ["phenologicalStageName"] = s.PhenologicalStageName,
            ["ecObjective"] = N(s.EcObjective),
            ["estimatedHarvestDate"] = s.EstimatedHarvestDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["daysRemaining"] = s.DaysRemaining,
            ["yieldBaseKgM2"] = N(s.YieldBaseKgM2),
            ["accuracyCycles"] = s.AccuracyCycles,
            ["dataSourceSummary"] = s.DataSourceSummary,
            ["sourceKind"] = s.SourceKind,
            ["activePlants"] = s.ActivePlants,
            ["riskPlantCount"] = s.RiskPlantCount,
            ["aptaPlantCount"] = s.AptaPlantCount,
            ["aptaPercent"] = N(s.AptaPercent),
            ["isCommercialStageMixed"] = s.IsCommercialStageMixed,
            ["priceCosts"] = priceCosts,
        };
    }

    private static object? N(decimal? value) => value is { } v ? v.ToString("0.0##########", CultureInfo.InvariantCulture) : null;
    private static object? N(int? value) => value?.ToString(CultureInfo.InvariantCulture);
}