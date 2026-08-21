using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

/// <summary>
/// Planta individual dentro de un lote (plan 09 / LOT-04).
/// La combinación (LotId, Row, Column) es única. Las métricas variables
/// (score, growth rate, imagen, etc.) se obtienen de la última evaluación,
/// no se duplican como columnas en esta tabla.
/// </summary>
public class Plant
{
    public int Id { get; set; }

    public int LotId { get; set; }

    /// <summary>Fila física dentro de la grilla configurada del lote.</summary>
    public int Row { get; set; }

    /// <summary>Columna física dentro de la grilla configurada del lote.</summary>
    public int Column { get; set; }

    /// <summary>Etapa fenológica actual estimada (deriva del GDD del lote).</summary>
    public int? PhenologicalStageId { get; set; }

    /// <summary>Etapa comercial actual (capa de decisión; se congela al cosechar/descartar).</summary>
    public int? CommercialStageId { get; set; }

    public PlantOperationalState OperationalState { get; set; } = PlantOperationalState.Activa;

    public DateOnly? HarvestDate { get; set; }

    public DateOnly? DiscardDate { get; set; }

    [MaxLength(300)]
    public string? DiscardReason { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public Lot? Lot { get; set; }
    public PhenologicalStage? PhenologicalStage { get; set; }
    public CommercialStage? CommercialStage { get; set; }

    public ICollection<PlantImage> Images { get; set; } = [];
    public ICollection<BabyLeafEvaluation> Evaluations { get; set; } = [];
    public ICollection<PlantStageHistory> History { get; set; } = [];
}