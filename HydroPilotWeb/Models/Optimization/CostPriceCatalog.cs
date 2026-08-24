using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models.Optimization;

/// <summary>
/// Catálogo de costos y precios (plan 16, OPT-03/OPT-04). Fuente autoritativa
/// de la comparativa económica Baby Leaf vs convencional. Sin precios vigentes
/// la comparación es "No calculable" (regla OPT-03), nunca un resultado
/// inventado. Los valores demo se marcan con Source = "demo" y la UI lo muestra.
/// </summary>
public class CostPriceCatalog
{
    public int Id { get; set; }

    /// <summary>Cultivo al que aplica (null = global).</summary>
    public int? CropTypeId { get; set; }

    /// <summary>Lote específico (null = aplica a todos los del cultivo).</summary>
    public int? LotId { get; set; }

    /// <summary>Destino comercial: baby_leaf | conventional (ver OptimizationContract).</summary>
    [Required]
    [MaxLength(30)]
    public string Destination { get; set; } = string.Empty;

    /// <summary>
    /// Ítem: price_per_kg | seed_cost_m2 | nutrient_cost_m2 | energy_cost_m2 |
    /// transplant_cost_m2 (ver OptimizationContract).
    /// </summary>
    [Required]
    [MaxLength(30)]
    public string Item { get; set; } = string.Empty;

    public decimal Value { get; set; }

    [Required]
    [MaxLength(10)]
    public string Currency { get; set; } = "ARS";

    public DateOnly ValidFrom { get; set; }

    /// <summary>Null = vigente hasta nuevo aviso.</summary>
    public DateOnly? ValidUntil { get; set; }

    /// <summary>Origen del dato: manual | demo | fixture. Demo nunca se oculta.</summary>
    [Required]
    [MaxLength(30)]
    public string Source { get; set; } = "manual";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public CropType? CropType { get; set; }
    public Lot? Lot { get; set; }
}