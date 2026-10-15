using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Optimization;

namespace HydroPilotWeb.Services.Optimization;

/// <summary>
/// Resuelve el catálogo de costos/precios vigente para un lote con precedencia de scope:
/// lote específico &gt; cultivo &gt; global (una fila por destino + ítem). Lo usan
/// Optimización (comparativa económica) y Lotes (valor del rendimiento por destino).
/// El orden del resultado es fijo (destino, ítem): el snapshot de optimización lo hashea.
/// </summary>
public static class CostPriceResolver
{
    /// <summary>Origen de los valores de ejemplo que siembra DbInitializer (CostPriceCatalog.Source).</summary>
    public const string DemoSource = "demo";

    /// <summary>Origen de los valores cargados por el usuario.</summary>
    public const string ManualSource = "manual";

    private static readonly string[] Destinations =
        [OptimizationContractDestination.BabyLeaf, OptimizationContractDestination.Conventional];

    private static readonly string[] Items =
    [
        OptimizationContractCostItem.PricePerKg,
        OptimizationContractCostItem.SeedCostM2,
        OptimizationContractCostItem.NutrientCostM2,
        OptimizationContractCostItem.EnergyCostM2,
        OptimizationContractCostItem.TransplantCostM2,
    ];

    /// <param name="includeDemo">
    /// false = ignora los valores demo (Lotes muestra "sin definir" en vez de un precio inventado);
    /// Optimización los usa etiquetados.
    /// </param>
    public static async Task<IReadOnlyList<CostPriceCatalog>> ResolveActiveAsync(
        HydroPilotDbContext context, Lot lot, DateOnly date, CancellationToken ct, bool includeDemo = true)
    {
        var rows = await context.CostPriceCatalogs
            .AsNoTracking()
            .Where(c => c.ValidFrom <= date
                        && (c.ValidUntil == null || c.ValidUntil >= date)
                        && (c.CropTypeId == lot.CropTypeId || c.CropTypeId == null)
                        && (c.LotId == lot.Id || c.LotId == null)
                        && (includeDemo || c.Source != DemoSource))
            .ToListAsync(ct);

        var lotSpecific = rows.Where(c => c.LotId == lot.Id).ToList();
        var cropWide = rows.Where(c => c.LotId == null && c.CropTypeId == lot.CropTypeId).ToList();
        var global = rows.Where(c => c.LotId == null && c.CropTypeId == null).ToList();

        var result = new List<CostPriceCatalog>();
        foreach (var destination in Destinations)
        {
            foreach (var item in Items)
            {
                var row = lotSpecific.FirstOrDefault(c => c.Destination == destination && c.Item == item)
                       ?? cropWide.FirstOrDefault(c => c.Destination == destination && c.Item == item)
                       ?? global.FirstOrDefault(c => c.Destination == destination && c.Item == item);
                if (row is not null)
                    result.Add(row);
            }
        }

        return result;
    }
}
