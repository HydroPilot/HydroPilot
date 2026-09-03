using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HydroPilotWeb.Models.Notifications;

public static class NotificationChannels
{
    public const string Internal = "Internal";
    public const string Email = "Email";
}

public static class NotificationSeverities
{
    public const string Critica = "Critica";
    public const string Alta = "Alta";
    public const string Media = "Media";
    public const string Info = "Info";
}

public static class NotificationTypes
{
    public const string DesbalanceQuimico = "DesbalanceQuimico";
    public const string CosechaInminente = "CosechaInminente";
    public const string RecomendacionEstrategica = "RecomendacionEstrategica";
    public const string PruebaSistema = "PruebaSistema";
    public const string Info = "Info";
}

public static class DeliveryStatuses
{
    public const string Pending = "Pending";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
    public const string NotConfigured = "NotConfigured";
    public const string Silenced = "Silenced";
}

public class NotificationAlert
{
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string Type { get; set; } = NotificationTypes.Info;

    [Required]
    [MaxLength(30)]
    public string Severity { get; set; } = NotificationSeverities.Info;

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(2000)]
    public string Message { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Fingerprint { get; set; } = string.Empty;

    public int? LotId { get; set; }
    public Lot? Lot { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAtUtc { get; set; }
    public bool IsResolved { get; set; }

    public ICollection<NotificationDelivery> Deliveries { get; set; } = new List<NotificationDelivery>();
}

public class NotificationPreference
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>
    /// Desactivado por defecto según requerimiento operativo.
    /// </summary>
    public bool EmailEnabled { get; set; } = false;

    [Required]
    [MaxLength(30)]
    public string MinSeverityEmail { get; set; } = NotificationSeverities.Alta;

    public int HarvestAlertDays { get; set; } = 3;

    public bool QuietHoursEnabled { get; set; } = false;
    public TimeSpan? QuietHoursStart { get; set; } = new TimeSpan(22, 0, 0);
    public TimeSpan? QuietHoursEnd { get; set; } = new TimeSpan(7, 0, 0);

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class NotificationDelivery
{
    public int Id { get; set; }

    public int AlertId { get; set; }
    public NotificationAlert? Alert { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    [Required]
    [MaxLength(30)]
    public string Channel { get; set; } = NotificationChannels.Internal;

    [Required]
    [MaxLength(30)]
    public string Status { get; set; } = DeliveryStatuses.Pending;

    [Required]
    [MaxLength(256)]
    public string Recipient { get; set; } = string.Empty;

    public int Attempts { get; set; } = 0;
    public int MaxAttempts { get; set; } = 3;

    [MaxLength(1000)]
    public string? LastError { get; set; }

    public DateTime? SentAtUtc { get; set; }

    public bool IsRead { get; set; } = false;
    public DateTime? ReadAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<NotificationAttempt> AttemptsList { get; set; } = new List<NotificationAttempt>();
}

public class NotificationAttempt
{
    public int Id { get; set; }

    public int DeliveryId { get; set; }
    public NotificationDelivery? Delivery { get; set; }

    public int AttemptNumber { get; set; }
    public DateTime AttemptedAtUtc { get; set; } = DateTime.UtcNow;

    public bool Success { get; set; }

    [MaxLength(1000)]
    public string? ErrorMessage { get; set; }
}
