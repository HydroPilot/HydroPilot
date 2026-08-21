using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

/// <summary>
/// Estado comercial de una planta: qué se puede hacer con ella hoy.
/// Capa de decisión sobre la etapa biológica; no reemplaza la fenología.
/// Estados de arranque: En desarrollo, Candidata Baby Leaf, Baby Leaf apta,
/// Cosecha convencional, Riesgo / fuera de ventana (plan 09).
/// </summary>
public class CommercialStage
{
    public int Id { get; set; }

    public int CropTypeId { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;

    public CropType? CropType { get; set; }
}