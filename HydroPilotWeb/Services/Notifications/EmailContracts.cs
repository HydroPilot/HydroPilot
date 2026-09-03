namespace HydroPilotWeb.Services.Notifications;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public string Provider { get; set; } = "Logging"; // "GmailSmtp" | "Smtp" | "Logging"
    public SmtpSettings Smtp { get; set; } = new();
}

public sealed class SmtpSettings
{
    public string Host { get; set; } = "smtp.gmail.com";
    public int Port { get; set; } = 587;
    public string SenderEmail { get; set; } = string.Empty;
    public string SenderName { get; set; } = "HydroPilot Alertas";
    public string Password { get; set; } = string.Empty;
    public bool EnableSsl { get; set; } = true;
}

public record EmailSendResult(bool Success, string? ErrorMessage = null);

public interface IEmailSender
{
    Task<EmailSendResult> SendEmailAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default);
}
