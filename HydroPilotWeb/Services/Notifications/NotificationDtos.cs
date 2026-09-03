using HydroPilotWeb.Models.Notifications;

namespace HydroPilotWeb.Services.Notifications;

public sealed record CreateAlertDto(
    string Type,
    string Severity,
    string Title,
    string Message,
    string Fingerprint,
    int? LotId = null
);

public sealed record NotificationItemDto(
    int Id,
    int AlertId,
    string Type,
    string Severity,
    string Title,
    string Message,
    int? LotId,
    string? LotName,
    DateTime CreatedAtUtc,
    bool IsRead,
    bool IsResolved
);

public class NotificationPreferenceDto
{
    public bool EmailEnabled { get; set; }
    public string MinSeverityEmail { get; set; } = NotificationSeverities.Alta;
    public int HarvestAlertDays { get; set; } = 3;
    public bool QuietHoursEnabled { get; set; }
    public TimeSpan? QuietHoursStart { get; set; }
    public TimeSpan? QuietHoursEnd { get; set; }

    public NotificationPreferenceDto() { }

    public NotificationPreferenceDto(
        bool emailEnabled,
        string minSeverityEmail,
        int harvestAlertDays,
        bool quietHoursEnabled,
        TimeSpan? quietHoursStart,
        TimeSpan? quietHoursEnd)
    {
        EmailEnabled = emailEnabled;
        MinSeverityEmail = minSeverityEmail;
        HarvestAlertDays = harvestAlertDays;
        QuietHoursEnabled = quietHoursEnabled;
        QuietHoursStart = quietHoursStart;
        QuietHoursEnd = quietHoursEnd;
    }
}

public sealed record SendTestEmailResult(
    bool Success,
    string Message,
    int UsersQueued = 0
);
