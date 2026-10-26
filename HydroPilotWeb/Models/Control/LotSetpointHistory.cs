using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models.Control;

/// <summary>
/// Nivel de pH y CE que rige un lote desde <see cref="ValidFromUtc"/> hasta la fila siguiente. Se agrega una
/// fila cada vez que cambia (por etapa o porque se editaron los rangos), para saber después qué se buscó en
/// cada momento aunque las tablas de etapas cambien.
/// </summary>
public class LotSetpointHistory
{
    public int Id { get; set; }

    public int LotId { get; set; }

    public DateTime ValidFromUtc { get; set; }

    public decimal? PhMin { get; set; }
    public decimal? PhTarget { get; set; }
    public decimal? PhMax { get; set; }

    public decimal? EcMin { get; set; }
    public decimal? EcTarget { get; set; }
    public decimal? EcMax { get; set; }

    /// <summary>Ajuste de la receta aprendida incluido en el objetivo (objetivo de la etapa = objetivo − ajuste).</summary>
    public decimal? PhOffset { get; set; }
    public decimal? EcOffset { get; set; }

    /// <summary>De dónde sale el nivel: STAGE (cultivo + etapa aplicada); más adelante, receta aprendida.</summary>
    [Required]
    [MaxLength(20)]
    public string Source { get; set; } = ControlContract.SetpointSourceStage;

    public int? StageId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Lot? Lot { get; set; }
}
