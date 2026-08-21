using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

public class Lot
{
    public int Id { get; set; }

    public int GreenhouseId { get; set; }

    public int CropTypeId { get; set; }

    /// <summary>Estado operativo del lote (catalogo LotStatus: ACTIVO/COSECHADO/DESCARTADO/EN_PAUSA).</summary>
    public int StatusId { get; set; }

    [MaxLength(150)]
    public string? Name { get; set; }

    public DateOnly SowingDate { get; set; }

    public decimal PlantedAreaM2 { get; set; }

    public decimal? ActualYieldKg { get; set; }

    public DateOnly? ActualHarvestDate { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // --- LOT-03: dimensiones de grilla (configurables por lote) ---
    // Las dimensiones no obligan a crear una planta en cada posición.
    public int? GridRows { get; set; }
    public int? GridColumns { get; set; }

    // --- LOT-03: parámetros compartidos de la solución nutritiva ---
    public decimal? CurrentPh { get; set; }
    public decimal? CurrentEc { get; set; }

    // --- LOT-03: GDD base y etapas predominantes ---
    /// <summary>
    /// GDD base acumulado del lote como SNAPSHOT persistido.
    /// El cálculo es propiedad del módulo de forecasting (GddService); este campo
    /// es el contrato persistido que forecasting actualizará. Si es null, las
    /// vistas usan GddService (fuente canónica) sin duplicar el cálculo.
    /// </summary>
    public decimal? AccumulatedGdd { get; set; }

    /// <summary>Etapa fenológica aplicada (receta EC vigente según GDD del lote).</summary>
    public int? AppliedPhenologicalStageId { get; set; }

    /// <summary>Etapa fenológica predominante del lote (snapshot del flujo diario).</summary>
    public int? PredominantPhenologicalStageId { get; set; }

    /// <summary>Etapa comercial predominante (snapshot). Null si hay empate (IsCommercialStageMixed).</summary>
    public int? PredominantCommercialStageId { get; set; }

    /// <summary>Empate de conteos: el lote muestra "Mixto" (plan 09).</summary>
    public bool IsCommercialStageMixed { get; set; }

    /// <summary>
    /// Porcentaje configurable (0-100) de plantas "Baby Leaf apta" para declarar la
    /// cosecha del lote (regla híbrida GDD + porcentaje). Null = decisión pendiente:
    /// NO se reemplaza por una constante oculta; la vista expone la advertencia.
    /// </summary>
    public decimal? BabyLeafHarvestTargetPercent { get; set; }

    public Greenhouse? Greenhouse { get; set; }
    public CropType? CropType { get; set; }
    public LotStatus? Status { get; set; }
    public PhenologicalStage? AppliedPhenologicalStage { get; set; }
    public PhenologicalStage? PredominantPhenologicalStage { get; set; }
    public CommercialStage? PredominantCommercialStage { get; set; }
    public ICollection<Prediction> Predictions { get; set; } = [];
    public ICollection<Plant> Plants { get; set; } = [];
}
