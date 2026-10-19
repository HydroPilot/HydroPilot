using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models.Control;

/// <summary>
/// Orden de dosis para una bomba y su resultado. Guarda además qué la disparó y qué nivel se buscaba en ese
/// momento: junto con <see cref="LotSetpointHistory"/> es "la receta usada" de cada lote.
/// </summary>
public class ActuatorCommand
{
    /// <summary>commandId del contrato con la Pi (clave de idempotencia).</summary>
    public Guid Id { get; set; }

    public int NodeId { get; set; }

    public int ActuatorId { get; set; }

    /// <summary>Lote asignado al nodo al crear el comando (null si el nodo no tenía lote).</summary>
    public int? LotId { get; set; }

    /// <summary>MANUAL | AUTO | EDGE.</summary>
    [Required]
    [MaxLength(10)]
    public string Source { get; set; } = ControlContract.SourceManual;

    [MaxLength(200)]
    public string? RequestedBy { get; set; }

    public decimal Amount { get; set; }

    [Required]
    [MaxLength(5)]
    public string Unit { get; set; } = ControlContract.UnitMl;

    /// <summary>ControlContract.Status*.</summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = ControlContract.StatusPending;

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Después de esta hora la Pi no lo ejecuta y la nube no lo entrega.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// Con Azure IoT Hub: cuándo se mandó a la cola del dispositivo (C2D). Null con HTTP, donde la Pi lo pide.
    /// Una vez mandado ya no se puede cancelar.
    /// </summary>
    public DateTime? DispatchedAtUtc { get; set; }

    /// <summary>Primera vez que la Pi lo pidió (HTTP) o confirmó haberlo recibido (IoT Hub).</summary>
    public DateTime? DeliveredAtUtc { get; set; }

    /// <summary>Cuándo se cerró: resultado de la Pi, vencimiento, sin confirmar o cancelación.</summary>
    public DateTime? CompletedAtUtc { get; set; }

    public decimal? ExecutedAmount { get; set; }

    [MaxLength(300)]
    public string? ResultError { get; set; }

    public DateTime? DeviceStartedAtUtc { get; set; }

    public DateTime? DeviceFinishedAtUtc { get; set; }

    // --- Disparo y nivel buscado ---

    /// <summary>pH | CE: la magnitud que corrige el actuador.</summary>
    [Required]
    [MaxLength(5)]
    public string Parameter { get; set; } = ControlContract.ParameterPh;

    /// <summary>Última lectura usable de la magnitud al crear el comando (sin FK: las lecturas se pueden limpiar).</summary>
    public int? TriggerReadingId { get; set; }

    public decimal? ValueBefore { get; set; }

    public decimal? BandMin { get; set; }

    public decimal? Target { get; set; }

    public decimal? BandMax { get; set; }

    /// <summary>Etapa fenológica aplicada del lote (de ahí sale la banda de CE).</summary>
    public int? StageId { get; set; }

    [Required]
    [MaxLength(30)]
    public string ControllerVersion { get; set; } = ControlContract.ControllerManual;

    /// <summary>Parte de la dosis calculada que aplicó el motor (lógica difusa, 0–1). Null en las manuales.</summary>
    public decimal? Strength { get; set; }

    // --- Efecto medido (después de la mezcla) ---

    /// <summary>Cuánto se esperaba mover la magnitud con esta dosis (con signo).</summary>
    public decimal? ExpectedEffect { get; set; }

    /// <summary>Primera lectura de la magnitud después de la mezcla.</summary>
    public decimal? ValueAfter { get; set; }

    /// <summary>Cambio atribuible a la dosis: después − antes, descontando la deriva que traía la solución.</summary>
    public decimal? ObservedEffect { get; set; }

    /// <summary>Movió al menos una parte de lo esperado en la dirección esperada. Null: sin lectura para medirlo.</summary>
    public bool? Effective { get; set; }

    public DateTime? EffectEvaluatedAtUtc { get; set; }

    public IotNode? Node { get; set; }
    public Actuator? Actuator { get; set; }
    public Lot? Lot { get; set; }
}
