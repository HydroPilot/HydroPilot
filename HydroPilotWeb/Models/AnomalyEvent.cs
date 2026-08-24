using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

/// <summary>
/// Episodio de anomalía agronómica (plan 15 / ANO-03). Propiedad del módulo de
/// anomalías: la detección y el ciclo del episodio viven acá; notifications
/// consume el contrato compartido (<c>AnomalyEventDto</c>) sin acoplarse al detector.
///
/// Un episodio agrega las lecturas consecutivas fuera de banda operativa de un
/// (lote, sensor, regla): NO se crea una fila por lectura (ReadingCount acumula,
/// First/LastObservedAtUtc delimitan la corrida). Una sola lectura sospechosa
/// queda en Seguimiento (Advertencia) — "registro sin episodio crítico" — y al
/// alcanzar N consecutivas (2 por defecto, AnomalyOptions/con catálogo) el MISMO
/// evento pasa a Abierta (Crítica). Se resuelve con lecturas normales consecutivas
/// o por cierre manual.
/// </summary>
public class AnomalyEvent
{
    public int Id { get; set; }

    /// <summary>Tipo estable de anomalía (ver AnomalyContract.Type*).</summary>
    [Required]
    [MaxLength(60)]
    public string Type { get; set; } = string.Empty;

    /// <summary>Lote afectado (la banda operativa es por cultivo/etapa del lote).</summary>
    public int LotId { get; set; }

    public int? GreenhouseId { get; set; }
    public int? NodeId { get; set; }
    public int? SensorId { get; set; }

    /// <summary>Severidad estable: Advertencia | Crítica (AnomalyContract.Severity*).</summary>
    [Required]
    [MaxLength(20)]
    public string Severity { get; set; } = AnomalyContract.SeverityAdvertencia;

    /// <summary>Valor observado (última lectura agregada al episodio).</summary>
    public decimal ObservedValue { get; set; }

    /// <summary>Valor objetivo/target de la banda operativa que produjo el episodio (explicabilidad).</summary>
    public decimal? TargetValue { get; set; }

    /// <summary>Banda operativa aplicada (límites que produjeron el episodio, para explicar sin consultas extra).</summary>
    public decimal? OperationalMin { get; set; }
    public decimal? OperationalMax { get; set; }

    /// <summary>Código de la regla que produjo el episodio (AnomalyRuleCatalog.Code).</summary>
    [Required]
    [MaxLength(60)]
    public string RuleCode { get; set; } = string.Empty;

    /// <summary>Descripción explicable de la regla con los valores aplicados (qué y por qué).</summary>
    [MaxLength(500)]
    public string? RuleDescription { get; set; }

    public DateTime FirstObservedAtUtc { get; set; }

    public DateTime LastObservedAtUtc { get; set; }

    /// <summary>Cantidad de lecturas fuera de banda agregadas al episodio.</summary>
    public int ReadingCount { get; set; }

    /// <summary>Estado del episodio: Seguimiento | Abierta | Reconocida | Resuelta.</summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = AnomalyContract.StatusSeguimiento;

    /// <summary>Fingerprint de deduplicación: estable por corrida de lecturas (lote+sensor+regla+primera observación).</summary>
    [Required]
    [MaxLength(64)]
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>Origen de los datos: telemetria | manual (AnomalyContract.Origin*).</summary>
    [Required]
    [MaxLength(30)]
    public string Origin { get; set; } = AnomalyContract.OriginTelemetria;

    public DateTime? AcknowledgedAtUtc { get; set; }

    public DateTime? ResolvedAtUtc { get; set; }

    [MaxLength(200)]
    public string? ResolutionReason { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public Lot? Lot { get; set; }
}