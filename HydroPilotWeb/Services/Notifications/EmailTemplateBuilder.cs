using System.Net;

namespace HydroPilotWeb.Services.Notifications;

public static class EmailTemplateBuilder
{
    public static string BuildAlertEmail(
        string title,
        string severity,
        string type,
        string message,
        string? lotName = null,
        string? ctaUrl = null)
    {
        var encodedTitle = WebUtility.HtmlEncode(title);
        var encodedMessage = WebUtility.HtmlEncode(message);
        var encodedLot = string.IsNullOrWhiteSpace(lotName) ? "General / Sin lote" : WebUtility.HtmlEncode(lotName);
        var dateStr = DateTime.UtcNow.ToString("dd/MM/yyyy HH:mm 'UTC'");

        var (badgeColor, badgeBg) = severity switch
        {
            "Critica" => ("#b42318", "#fee4e2"),
            "Alta" => ("#b54708", "#fef0c7"),
            "Media" => ("#026aa2", "#e0f2fe"),
            _ => ("#344054", "#f2f4f7")
        };

        var ctaHtml = string.IsNullOrWhiteSpace(ctaUrl)
            ? string.Empty
            : $@"<div style=""margin-top: 24px; text-align: center;"">
                    <a href=""{ctaUrl}"" style=""background-color: #2e90fa; color: #ffffff; padding: 10px 20px; text-decoration: none; border-radius: 6px; font-weight: 600; display: inline-block;"">
                        Ver en HydroPilot
                    </a>
                 </div>";

        return $@"<!DOCTYPE html>
<html lang=""es"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>{encodedTitle}</title>
</head>
<body style=""margin: 0; padding: 20px; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #0d1520; color: #e4ecf6;"">
    <table align=""center"" border=""0"" cellpadding=""0"" cellspacing=""0"" width=""100%"" style=""max-width: 580px; background-color: #182230; border: 1px solid #223044; border-radius: 12px; overflow: hidden; margin: 0 auto;"">
        <!-- Encabezado -->
        <tr>
            <td style=""padding: 24px 28px; background-color: #121926; border-bottom: 1px solid #223044;"">
                <table width=""100%"" border=""0"" cellpadding=""0"" cellspacing=""0"">
                    <tr>
                        <td>
                            <strong style=""font-size: 20px; color: #ffffff; letter-spacing: 0.5px;"">HydroPilot</strong>
                            <span style=""font-size: 13px; color: #70869a; margin-left: 8px;"">Sistema de Alertas</span>
                        </td>
                        <td align=""right"">
                            <span style=""display: inline-block; padding: 4px 10px; font-size: 12px; font-weight: 700; border-radius: 12px; background-color: {badgeBg}; color: {badgeColor};"">
                                {severity.ToUpperInvariant()}
                            </span>
                        </td>
                    </tr>
                </table>
            </td>
        </tr>

        <!-- Contenido principal -->
        <tr>
            <td style=""padding: 28px;"">
                <h2 style=""margin-top: 0; margin-bottom: 12px; font-size: 18px; color: #ffffff;"">{encodedTitle}</h2>
                <p style=""font-size: 14px; line-height: 1.6; color: #d0dbe7; margin-bottom: 20px;"">{encodedMessage}</p>

                <table width=""100%"" border=""0"" cellpadding=""8"" cellspacing=""0"" style=""background-color: #1e2b3d; border-radius: 8px; font-size: 13px; color: #8ea1b4; margin-bottom: 16px;"">
                    <tr>
                        <td width=""35%"" style=""border-bottom: 1px solid #29384e;""><strong>Tipo de evento:</strong></td>
                        <td style=""border-bottom: 1px solid #29384e; color: #e4ecf6;"">{type}</td>
                    </tr>
                    <tr>
                        <td style=""border-bottom: 1px solid #29384e;""><strong>Lote asociado:</strong></td>
                        <td style=""border-bottom: 1px solid #29384e; color: #e4ecf6;"">{encodedLot}</td>
                    </tr>
                    <tr>
                        <td><strong>Fecha y hora:</strong></td>
                        <td style=""color: #e4ecf6;"">{dateStr}</td>
                    </tr>
                </table>

                {ctaHtml}
            </td>
        </tr>

        <!-- Pie -->
        <tr>
            <td style=""padding: 16px 28px; background-color: #121926; border-top: 1px solid #223044; text-align: center; font-size: 12px; color: #70869a;"">
                Este correo fue enviado automáticamente por el sistema HydroPilot.<br>
                Puedes gestionar tus preferencias de alertas en <a href=""https://hydropilot.local/settings/notifications"" style=""color: #2e90fa; text-decoration: none;"">Ajustes de Notificaciones</a>.
            </td>
        </tr>
    </table>
</body>
</html>";
    }
}
