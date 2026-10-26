using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

/// <summary>Tipos de error de un sensor (<see cref="SensorFault.Kind"/>).</summary>
public static class SensorFaultKinds
{
    /// <summary>El sensor dejó de mandar lecturas; se resuelve sola cuando vuelven.</summary>
    public const string SinDatos = "sin-datos";

    /// <summary>Lectura fuera del rango normal del tipo de sensor; la resuelve una persona.</summary>
    public const string FueraDeRango = "fuera-de-rango";

    /// <summary>Dos lecturas seguidas que difieren más que el salto máximo; la resuelve una persona.</summary>
    public const string Salto = "salto";
}

/// <summary>
/// Error de un sensor (episodio): sin datos, lectura fuera de lo normal o salto entre lecturas. Abierto =
/// <see cref="ResolvedAtUtc"/> null. Mientras haya uno abierto de pH o CE, el control automático no corrige ese
/// parámetro (el otro sigue); los de temperatura y humedad solo avisan. Cada uno genera una alerta.
/// </summary>
public class SensorFault
{
    public int Id { get; set; }

    public int NodeId { get; set; }

    public int SensorId { get; set; }

    /// <summary>pH | CE | Temperatura | Humedad (nombre del tipo de sensor al detectarlo).</summary>
    [Required]
    [MaxLength(30)]
    public string SensorType { get; set; } = string.Empty;

    /// <summary>Uno de <see cref="SensorFaultKinds"/>.</summary>
    [Required]
    [MaxLength(20)]
    public string Kind { get; set; } = SensorFaultKinds.SinDatos;

    /// <summary>Lectura que lo disparó (fuera de rango o después del salto). Null si es falta de datos.</summary>
    public decimal? Value { get; set; }

    /// <summary>Lectura anterior a la que lo disparó (antes del salto).</summary>
    public decimal? PreviousValue { get; set; }

    /// <summary>El límite que se pasó, tal como estaba al detectarlo ("rango normal 5–7", "más de 0,5 entre lecturas").</summary>
    [MaxLength(200)]
    public string? Detail { get; set; }

    /// <summary>Lectura que lo disparó (fecha observada) o, si es falta de datos, cuándo se detectó.</summary>
    public DateTime DetectedAtUtc { get; set; }

    /// <summary>Falta de datos: última lectura antes del corte (null = el sensor nunca mandó).</summary>
    public DateTime? LastReadingAtUtc { get; set; }

    public DateTime? ResolvedAtUtc { get; set; }

    /// <summary>Quién lo marcó como resuelto; null con <see cref="ResolvedAtUtc"/> = se resolvió solo.</summary>
    [MaxLength(200)]
    public string? ResolvedBy { get; set; }

    public IotNode? Node { get; set; }

    public Sensor? Sensor { get; set; }
}
