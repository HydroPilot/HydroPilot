using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models.Control;

/// <summary>
/// Bomba de un circuito que actúa sobre su tanque: subir pH, bajar pH, nutrientes o agua. La ejecuta la Pi del
/// invernadero (<see cref="NodeId"/>): la nube manda la cantidad (ml o L) y la Pi la convierte a segundos de bomba
/// con su propia calibración.
/// </summary>
public class Actuator
{
    public int Id { get; set; }

    public int NodeId { get; set; }

    /// <summary>Circuito (tanque) sobre el que actúa.</summary>
    public int CircuitId { get; set; }

    /// <summary>Qué hace: ph-up | ph-down | nutrient | water (ControlContract.Kind*).</summary>
    [Required]
    [MaxLength(20)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Nombre de la bomba en la Pi; es el campo "actuator" del comando.</summary>
    [Required]
    [MaxLength(50)]
    public string TechnicalKey { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>ml | L.</summary>
    [Required]
    [MaxLength(5)]
    public string Unit { get; set; } = ControlContract.UnitMl;

    /// <summary>Máximo por comando, en la unidad del actuador.</summary>
    public decimal MaxPerCommand { get; set; }

    /// <summary>Máximo en 24 horas, en la unidad del actuador.</summary>
    public decimal MaxPerDay { get; set; }

    /// <summary>
    /// Cuánto mueve 1 ml de producto por litro de tanque: pH (pH Up/Down) o mS/cm (nutrientes). Null en el
    /// agua (diluye). Arranca con la estimación de ControlContract y se ajusta con el efecto medido de cada dosis.
    /// </summary>
    public decimal? EffectPerUnit { get; set; }

    /// <summary>Dosis con efecto medido que ajustaron <see cref="EffectPerUnit"/>.</summary>
    public int EffectSamples { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public IotNode? Node { get; set; }
    public Circuit? Circuit { get; set; }
}
