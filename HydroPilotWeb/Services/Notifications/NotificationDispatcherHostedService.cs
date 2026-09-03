using HydroPilotWeb.Data;
using HydroPilotWeb.Models.Notifications;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Services.Notifications;

public class NotificationDispatcherHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<NotificationDispatcherHostedService> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(15);

    public NotificationDispatcherHostedService(
        IServiceProvider serviceProvider,
        ILogger<NotificationDispatcherHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NotificationDispatcherHostedService iniciado (intervalo: {Interval}s).",
            _pollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingDeliveriesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error no controlado al despachar notificaciones outbox.");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("NotificationDispatcherHostedService detenido.");
    }

    private async Task ProcessPendingDeliveriesAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<HydroPilotDbContext>>();
        var emailSender = scope.ServiceProvider.GetRequiredService<IEmailSender>();

        await using var context = await dbFactory.CreateDbContextAsync(ct);

        var pending = await context.NotificationDeliveries
            .Include(d => d.Alert)
                .ThenInclude(a => a!.Lot)
            .Include(d => d.User)
            .Where(d => d.Status == DeliveryStatuses.Pending
                        && d.Channel == NotificationChannels.Email
                        && d.Attempts < d.MaxAttempts)
            .OrderBy(d => d.CreatedAtUtc)
            .Take(10)
            .ToListAsync(ct);

        if (pending.Count == 0) return;

        _logger.LogInformation("Procesando {Count} entregas de email pendientes en outbox...", pending.Count);

        var userIds = pending.Select(p => p.UserId).Distinct().ToList();
        var prefs = await context.NotificationPreferences
            .Where(p => userIds.Contains(p.UserId))
            .ToDictionaryAsync(p => p.UserId, ct);

        foreach (var delivery in pending)
        {
            if (delivery.Alert == null)
            {
                delivery.Status = DeliveryStatuses.Failed;
                delivery.LastError = "Alerta no encontrada.";
                continue;
            }

            if (!NotificationService.IsDeliverableEmail(delivery.Recipient))
            {
                delivery.Status = DeliveryStatuses.Failed;
                delivery.LastError = "Dirección de correo excluida (cuenta demo/admin local no entregable).";
                _logger.LogInformation("Entrega de email {DeliveryId} omitida: destinatario {Recipient} es de prueba interna.",
                    delivery.Id, delivery.Recipient);
                continue;
            }

            delivery.Attempts++;
            var attemptNumber = delivery.Attempts;

            // Verificar modo No Molestar (excepto para alertas Críticas o pruebas manuales)
            if (prefs.TryGetValue(delivery.UserId, out var pref) &&
                pref.QuietHoursEnabled &&
                delivery.Alert.Severity != NotificationSeverities.Critica &&
                delivery.Alert.Type != NotificationTypes.PruebaSistema &&
                IsWithinQuietHours(pref.QuietHoursStart, pref.QuietHoursEnd))
            {
                delivery.Status = DeliveryStatuses.Silenced;
                delivery.LastError = "Omitido por horario de silencio (No molestar).";
                _logger.LogInformation("Entrega {DeliveryId} silenciada por modo No Molestar.", delivery.Id);
                continue;
            }

            var htmlBody = EmailTemplateBuilder.BuildAlertEmail(
                delivery.Alert.Title,
                delivery.Alert.Severity,
                delivery.Alert.Type,
                delivery.Alert.Message,
                delivery.Alert.Lot?.Name
            );

            var sendResult = await emailSender.SendEmailAsync(
                delivery.Recipient,
                delivery.Alert.Title,
                htmlBody,
                ct
            );

            var attempt = new NotificationAttempt
            {
                DeliveryId = delivery.Id,
                AttemptNumber = attemptNumber,
                AttemptedAtUtc = DateTime.UtcNow,
                Success = sendResult.Success,
                ErrorMessage = sendResult.ErrorMessage
            };
            context.NotificationAttempts.Add(attempt);

            if (sendResult.Success)
            {
                delivery.Status = DeliveryStatuses.Sent;
                delivery.SentAtUtc = DateTime.UtcNow;
                delivery.LastError = null;
                _logger.LogInformation("Entrega de email {DeliveryId} completada a {Recipient}.",
                    delivery.Id, delivery.Recipient);
            }
            else
            {
                delivery.LastError = sendResult.ErrorMessage;
                if (delivery.Attempts >= delivery.MaxAttempts)
                {
                    delivery.Status = DeliveryStatuses.Failed;
                    _logger.LogWarning("Entrega de email {DeliveryId} falló definitivamente tras {Attempts} intentos: {Error}",
                        delivery.Id, delivery.Attempts, sendResult.ErrorMessage);
                }
                else
                {
                    _logger.LogInformation("Entrega de email {DeliveryId} reintentará (intento {Attempts}/{Max}): {Error}",
                        delivery.Id, delivery.Attempts, delivery.MaxAttempts, sendResult.ErrorMessage);
                }
            }
        }

        await context.SaveChangesAsync(ct);
    }

    private static bool IsWithinQuietHours(TimeSpan? start, TimeSpan? end)
    {
        if (start == null || end == null) return false;

        var nowTime = DateTime.UtcNow.TimeOfDay;
        var s = start.Value;
        var e = end.Value;

        if (s <= e)
        {
            return nowTime >= s && nowTime <= e;
        }
        else
        {
            // Cruza medianoche (ej: 22:00 a 07:00)
            return nowTime >= s || nowTime <= e;
        }
    }
}
