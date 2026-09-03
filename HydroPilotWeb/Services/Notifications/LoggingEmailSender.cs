namespace HydroPilotWeb.Services.Notifications;

public class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;

    public LoggingEmailSender(ILogger<LoggingEmailSender> logger)
    {
        _logger = logger;
    }

    public Task<EmailSendResult> SendEmailAsync(
        string toEmail,
        string subject,
        string htmlBody,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[LoggingEmailSender] Simulación de correo a {To}\nAsunto: {Subject}\nCuerpo HTML ({Length} bytes)",
            toEmail, subject, htmlBody.Length);

        return Task.FromResult(new EmailSendResult(true));
    }
}
