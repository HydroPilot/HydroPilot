using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

public class IotNode
{
    public int Id { get; set; }

    public int GreenhouseId { get; set; }

    [Required]
    [MaxLength(100)]
    public string Identifier { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? FirmwareVersion { get; set; }

    [MaxLength(30)]
    public string Status { get; set; } = "ACTIVO";

    /// <summary>Último contacto HTTP recibido del nodo (cualquier batch, aceptado o no).</summary>
    public DateTime? LastConnection { get; set; }

    /// <summary>Intervalo esperado entre telemetrías, en segundos (define ONLINE/DEGRADED/OFFLINE).</summary>
    public int ExpectedIntervalSeconds { get; set; } = 300;

    /// <summary>Estado calculado de conectividad: NEVER_CONNECTED | ONLINE | DEGRADED | OFFLINE.</summary>
    [MaxLength(20)]
    public string ConnectionState { get; set; } = TelemetryContract.ConnectionNeverConnected;

    /// <summary>Última vez que se aceptó telemetría del nodo (base del estado de conexión).</summary>
    public DateTime? LastAcceptedAt { get; set; }

    /// <summary>Última vez que un batch del nodo fue rechazado o generó rechazos.</summary>
    public DateTime? LastRejectedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Greenhouse? Greenhouse { get; set; }
    public ICollection<Sensor> Sensors { get; set; } = [];
    public ICollection<TelemetryBatch> Batches { get; set; } = [];
    public ICollection<TelemetryRejection> Rejections { get; set; } = [];
    public ICollection<NodeLotAssignment> LotAssignments { get; set; } = [];
}
