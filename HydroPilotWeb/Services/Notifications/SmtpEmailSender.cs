using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Services.Notifications;

public class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(
        IOptions<EmailOptions> options,
        ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<EmailSendResult> SendEmailAsync(
        string toEmail,
        string subject,
        string htmlBody,
        CancellationToken ct = default)
    {
        var smtp = _options.Smtp;
        if (string.IsNullOrWhiteSpace(smtp.SenderEmail) || string.IsNullOrWhiteSpace(smtp.Password))
        {
            _logger.LogWarning("Envío de email omitido: credenciales SMTP no configuradas en 'Email:Smtp'.");
            return new EmailSendResult(false, "Credenciales SMTP no configuradas en Email:Smtp.");
        }

        try
        {
            using var client = new SmtpClient(smtp.Host, smtp.Port)
            {
                EnableSsl = smtp.EnableSsl,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(smtp.SenderEmail, smtp.Password),
                Timeout = 15000 // 15 segundos de timeout
            };

            using var message = new MailMessage
            {
                From = new MailAddress(smtp.SenderEmail, smtp.SenderName),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true
            };
            message.To.Add(new MailAddress(toEmail));

            _logger.LogInformation("Enviando email a {To} con asunto '{Subject}' vía {Host}:{Port}...",
                toEmail, subject, smtp.Host, smtp.Port);

            // SmtpClient.SendMailAsync acepta CancellationToken en .NET
            await client.SendMailAsync(message, ct);

            _logger.LogInformation("Email enviado con éxito a {To}.", toEmail);
            return new EmailSendResult(true);
        }
        catch (SmtpException ex)
        {
            _logger.LogError(ex, "Falla SMTP al enviar email a {To}: {Message} (StatusCode={Code})",
                toEmail, ex.Message, ex.StatusCode);
            return new EmailSendResult(false, $"Error SMTP ({ex.StatusCode}): {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inesperado al enviar email a {To}: {Message}", toEmail, ex.Message);
            return new EmailSendResult(false, $"Error inesperado: {ex.Message}");
        }
    }
}
