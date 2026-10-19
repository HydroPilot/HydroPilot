using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models.Control;

/// <summary>
/// Receta aprendida de un lote en curso (fase 5): cuánto correr el objetivo de pH y CE de la etapa según los lotes
/// cerrados parecidos (temporada y clima) que dieron más Baby Leaf, y cuánto confiar en ese ajuste según cómo
/// viene el score diario del propio lote. Una fila por lote; se recalcula una vez por día. Sin historial, los
/// ajustes son 0 y rigen los niveles de la etapa.
/// </summary>
public class LotRecipe
{
    public int LotId { get; set; }

    /// <summary>Ajuste que sugiere el historial (antes de la confianza).</summary>
    public decimal HistoryPhOffset { get; set; }
    public decimal HistoryEcOffset { get; set; }

    /// <summary>Ajuste que se aplica: historial × confianza, redondeado a 0,05.</summary>
    public decimal PhOffset { get; set; }
    public decimal EcOffset { get; set; }

    /// <summary>0–1: baja a la mitad cada día que el score del lote viene cayendo con el ajuste puesto.</summary>
    public decimal Trust { get; set; } = 1m;

    /// <summary>Lotes cerrados parecidos que se usaron.</summary>
    public int SimilarLots { get; set; }

    /// <summary>Cuántos lotes "equivalentes" respaldan el ajuste (los parecidos pesan más).</summary>
    public decimal Evidence { get; set; }

    /// <summary>Tendencia del score Baby Leaf promedio del lote (puntos por día) al recalcular.</summary>
    public decimal? ScoreTrend { get; set; }

    [MaxLength(400)]
    public string Reason { get; set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>Última vez que cambió el ajuste aplicado.</summary>
    public DateTime? LastChangeAtUtc { get; set; }

    public Lot? Lot { get; set; }
}
