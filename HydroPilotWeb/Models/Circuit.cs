using System.ComponentModel.DataAnnotations;
using HydroPilotWeb.Models.Control;

namespace HydroPilotWeb.Models;

/// <summary>
/// Circuito hidropónico de un invernadero: tanque y canales físicos, independientes de los otros circuitos, con sus
/// sensores de pH y CE y sus 4 bombas. Los maneja la Pi del invernadero (<see cref="NodeId"/>). Tiene un solo lote en
/// curso por vez (<see cref="CircuitAssignment"/>). La temperatura y la humedad no son del circuito: son del
/// invernadero (sensores sin circuito).
/// </summary>
public class Circuit
{
    public const int NameMaxLength = 60;

    public int Id { get; set; }

    public int GreenhouseId { get; set; }

    /// <summary>La Pi que lee sus sensores y ejecuta sus bombas.</summary>
    public int NodeId { get; set; }

    /// <summary>"Circuito 1", "Mesada norte" (único dentro del invernadero).</summary>
    [Required]
    [MaxLength(NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Control de pH/CE del tanque: OFF | MANUAL | AUTO (ControlContract.Mode*). Arranca en MANUAL.</summary>
    [MaxLength(10)]
    public string ControlMode { get; set; } = ControlContract.ModeManual;

    /// <summary>Litros del tanque de solución del circuito.</summary>
    public decimal ReservoirLiters { get; set; } = ControlContract.DefaultReservoirLiters;

    /// <summary>CE del agua de red con que se diluye el tanque (mS/cm): la usa la dosis de agua.</summary>
    public decimal SourceWaterEc { get; set; } = ControlContract.DefaultSourceWaterEc;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Greenhouse? Greenhouse { get; set; }
    public IotNode? Node { get; set; }
    public ICollection<Sensor> Sensors { get; set; } = [];
    public ICollection<Actuator> Actuators { get; set; } = [];
    public ICollection<CircuitAssignment> Assignments { get; set; } = [];
}
