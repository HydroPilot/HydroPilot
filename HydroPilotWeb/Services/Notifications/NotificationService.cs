using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Notifications;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Services.Notifications;

public class NotificationService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        ILogger<NotificationService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>
    /// Crea una alerta del sistema deduplicada por huella digital (Fingerprint)
    /// y encola entregas internas y por correo según las preferencias del usuario.
    /// </summary>
    public async Task<NotificationAlert?> CreateAlertAsync(CreateAlertDto dto, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        // Deduplicación por fingerprint: si ya existe una alerta activa idéntica, no duplicar
        if (!string.IsNullOrWhiteSpace(dto.Fingerprint))
        {
            var existing = await context.NotificationAlerts
                .FirstOrDefaultAsync(a => a.Fingerprint == dto.Fingerprint && !a.IsResolved, ct);

            if (existing != null)
            {
                _logger.LogDebug("Alerta descartada por fingerprint duplicado activo: {Fingerprint}", dto.Fingerprint);
                return existing;
            }
        }

        var alert = new NotificationAlert
        {
            Type = dto.Type,
            Severity = dto.Severity,
            Title = dto.Title,
            Message = dto.Message,
            Fingerprint = dto.Fingerprint,
            LotId = dto.LotId,
            CreatedAtUtc = DateTime.UtcNow,
            IsResolved = false
        };

        context.NotificationAlerts.Add(alert);
        await context.SaveChangesAsync(ct);

        // Obtener todos los usuarios activos a notificar
        var users = await context.Users
            .Where(u => u.Role != "no-asignado")
            .ToListAsync(ct);

        var userIds = users.Select(u => u.Id).ToList();
        var existingPrefs = await context.NotificationPreferences
            .Where(p => userIds.Contains(p.UserId))
            .ToDictionaryAsync(p => p.UserId, ct);

        foreach (var user in users)
        {
            if (!existingPrefs.TryGetValue(user.Id, out var pref))
            {
                // Preferencia por defecto: email desactivado según requerimiento
                pref = new NotificationPreference
                {
                    UserId = user.Id,
                    EmailEnabled = false,
                    MinSeverityEmail = NotificationSeverities.Alta,
                    HarvestAlertDays = 3,
                    QuietHoursEnabled = false,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                context.NotificationPreferences.Add(pref);
            }

            // 1. Entrega Interna (Bandeja web)
            context.NotificationDeliveries.Add(new NotificationDelivery
            {
                AlertId = alert.Id,
                UserId = user.Id,
                Channel = NotificationChannels.Internal,
                Status = DeliveryStatuses.Sent,
                Recipient = user.Email,
                IsRead = false,
                SentAtUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow
            });

            // 2. Entrega por Email (Outbox) si el usuario la tiene habilitada y cumple severidad
            if (pref.EmailEnabled && ShouldSendEmailForSeverity(dto.Severity, pref.MinSeverityEmail) && !string.IsNullOrWhiteSpace(user.Email))
            {
                context.NotificationDeliveries.Add(new NotificationDelivery
                {
                    AlertId = alert.Id,
                    UserId = user.Id,
                    Channel = NotificationChannels.Email,
                    Status = DeliveryStatuses.Pending,
                    Recipient = user.Email,
                    CreatedAtUtc = DateTime.UtcNow
                });
            }
        }

        await context.SaveChangesAsync(ct);
        _logger.LogInformation("Alerta {AlertId} creada ({Type}, {Severity}): '{Title}'.",
            alert.Id, alert.Type, alert.Severity, alert.Title);

        return alert;
    }

    /// <summary>
    /// Consulta las notificaciones de la bandeja interna para un usuario.
    /// </summary>
    public async Task<List<NotificationItemDto>> GetInternalNotificationsAsync(
        int userId,
        bool unreadOnly = false,
        int take = 50,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var query = context.NotificationDeliveries
            .AsNoTracking()
            .Include(d => d.Alert)
                .ThenInclude(a => a!.Lot)
            .Where(d => d.UserId == userId && d.Channel == NotificationChannels.Internal);

        if (unreadOnly)
        {
            query = query.Where(d => !d.IsRead);
        }

        var items = await query
            .OrderByDescending(d => d.CreatedAtUtc)
            .Take(take)
            .Select(d => new NotificationItemDto(
                d.Id,
                d.AlertId,
                d.Alert!.Type,
                d.Alert.Severity,
                d.Alert.Title,
                d.Alert.Message,
                d.Alert.LotId,
                d.Alert.Lot != null ? d.Alert.Lot.Name : null,
                d.CreatedAtUtc,
                d.IsRead,
                d.Alert.IsResolved
            ))
            .ToListAsync(ct);

        return items;
    }

    /// <summary>
    /// Obtiene la cantidad de notificaciones internas no leídas de un usuario.
    /// </summary>
    public async Task<int> GetUnreadCountAsync(int userId, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        return await context.NotificationDeliveries
            .CountAsync(d => d.UserId == userId && d.Channel == NotificationChannels.Internal && !d.IsRead, ct);
    }

    /// <summary>
    /// Marca una notificación interna como leída.
    /// </summary>
    public async Task MarkAsReadAsync(int deliveryId, int userId, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var delivery = await context.NotificationDeliveries
            .FirstOrDefaultAsync(d => d.Id == deliveryId && d.UserId == userId, ct);

        if (delivery != null && !delivery.IsRead)
        {
            delivery.IsRead = true;
            delivery.ReadAtUtc = DateTime.UtcNow;
            await context.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Marca todas las notificaciones internas de un usuario como leídas.
    /// </summary>
    public async Task MarkAllAsReadAsync(int userId, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var unread = await context.NotificationDeliveries
            .Where(d => d.UserId == userId && d.Channel == NotificationChannels.Internal && !d.IsRead)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var delivery in unread)
        {
            delivery.IsRead = true;
            delivery.ReadAtUtc = now;
        }

        await context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Obtiene las preferencias de notificación de un usuario, creándolas con valores por defecto si no existen.
    /// </summary>
    public async Task<NotificationPreferenceDto> GetUserPreferencesAsync(int userId, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var pref = await context.NotificationPreferences.FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (pref == null)
        {
            pref = new NotificationPreference
            {
                UserId = userId,
                EmailEnabled = false,
                MinSeverityEmail = NotificationSeverities.Alta,
                HarvestAlertDays = 3,
                QuietHoursEnabled = false,
                UpdatedAtUtc = DateTime.UtcNow
            };
            context.NotificationPreferences.Add(pref);
            await context.SaveChangesAsync(ct);
        }

        return new NotificationPreferenceDto(
            pref.EmailEnabled,
            pref.MinSeverityEmail,
            pref.HarvestAlertDays,
            pref.QuietHoursEnabled,
            pref.QuietHoursStart,
            pref.QuietHoursEnd
        );
    }

    /// <summary>
    /// Guarda las preferencias de notificación de un usuario.
    /// </summary>
    public async Task SaveUserPreferencesAsync(int userId, NotificationPreferenceDto dto, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var pref = await context.NotificationPreferences.FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (pref == null)
        {
            pref = new NotificationPreference { UserId = userId };
            context.NotificationPreferences.Add(pref);
        }

        pref.EmailEnabled = dto.EmailEnabled;
        pref.MinSeverityEmail = dto.MinSeverityEmail;
        pref.HarvestAlertDays = dto.HarvestAlertDays;
        pref.QuietHoursEnabled = dto.QuietHoursEnabled;
        pref.QuietHoursStart = dto.QuietHoursStart;
        pref.QuietHoursEnd = dto.QuietHoursEnd;
        pref.UpdatedAtUtc = DateTime.UtcNow;

        await context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Acción de administración: encola un correo de prueba a todos los usuarios registrados
    /// con dirección de email para comprobar conectividad y entrega real vía Gmail SMTP.
    /// </summary>
    public async Task<SendTestEmailResult> SendTestEmailToAllUsersAsync(int triggeredByUserId, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var users = await context.Users
            .Where(u => !string.IsNullOrWhiteSpace(u.Email))
            .ToListAsync(ct);

        if (users.Count == 0)
        {
            return new SendTestEmailResult(false, "No hay usuarios registrados con dirección de correo.");
        }

        var testAlert = new NotificationAlert
        {
            Type = NotificationTypes.PruebaSistema,
            Severity = NotificationSeverities.Info,
            Title = "Prueba de despacho de correo HydroPilot",
            Message = $"Este es un correo de prueba enviado manualmente desde el panel de administración por el usuario #{triggeredByUserId} para validar la conectividad SMTP de Gmail.",
            Fingerprint = $"test-email-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTime.UtcNow,
            IsResolved = true
        };

        context.NotificationAlerts.Add(testAlert);
        await context.SaveChangesAsync(ct);

        foreach (var user in users)
        {
            // Se encola explícitamente sin importar que EmailEnabled esté en false
            context.NotificationDeliveries.Add(new NotificationDelivery
            {
                AlertId = testAlert.Id,
                UserId = user.Id,
                Channel = NotificationChannels.Email,
                Status = DeliveryStatuses.Pending,
                Recipient = user.Email,
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        await context.SaveChangesAsync(ct);
        _logger.LogInformation("Encolado email de prueba a {Count} usuarios.", users.Count);

        return new SendTestEmailResult(true, $"Se encoló el correo de prueba para {users.Count} usuarios registrados. El despachador lo enviará en los próximos segundos.", users.Count);
    }

    /// <summary>
    /// Sincroniza episodios críticos de anomalía abiertos con las alertas del sistema.
    /// Si un episodio se resolvió, marca la alerta como resuelta.
    /// </summary>
    public async Task SyncAnomalyAlertsAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var openCritical = await context.AnomalyEvents
            .Include(e => e.Lot)
            .Where(e => e.Status == AnomalyContract.StatusAbierta && e.Severity == AnomalyContract.SeverityCritica)
            .ToListAsync(ct);

        foreach (var ev in openCritical)
        {
            var fingerprint = $"anomaly-event-{ev.Id}";
            var lotName = ev.Lot?.Name ?? "Lote";
            var title = $"Anomalía crítica en {lotName}: {ev.Type}";
            var message = ev.RuleDescription ?? $"El parámetro {ev.Type} registró valor {ev.ObservedValue:F2} fuera de rango operativo ({ev.ReadingCount} lecturas consecutivas).";

            await CreateAlertAsync(new CreateAlertDto(
                NotificationTypes.DesbalanceQuimico,
                NotificationSeverities.Critica,
                title,
                message,
                fingerprint,
                ev.LotId
            ), ct);
        }

        // Marcar resueltas aquellas alertas cuyo evento de anomalía ya esté resuelto
        var resolvedEventIds = await context.AnomalyEvents
            .Where(e => e.Status == AnomalyContract.StatusResuelta)
            .Select(e => e.Id)
            .ToListAsync(ct);

        if (resolvedEventIds.Count > 0)
        {
            var activeAnomalyAlerts = await context.NotificationAlerts
                .Where(a => a.Type == NotificationTypes.DesbalanceQuimico && !a.IsResolved)
                .ToListAsync(ct);

            var changed = false;
            foreach (var alert in activeAnomalyAlerts)
            {
                if (alert.Fingerprint.StartsWith("anomaly-event-") &&
                    int.TryParse(alert.Fingerprint["anomaly-event-".Length..], out var evId) &&
                    resolvedEventIds.Contains(evId))
                {
                    alert.IsResolved = true;
                    alert.ResolvedAtUtc = DateTime.UtcNow;
                    changed = true;
                }
            }
            if (changed)
            {
                await context.SaveChangesAsync(ct);
            }
        }
    }

    /// <summary>
    /// Sincroniza alertas de cosecha inminente para lotes activos.
    /// </summary>
    public async Task SyncHarvestAlertsAsync(int lotId, string lotName, int daysRemaining, CancellationToken ct = default)
    {
        if (daysRemaining < 0) return;

        var fingerprint = $"harvest-imminent-lot-{lotId}";
        var title = $"Cosecha inminente: {lotName}";
        var message = $"El lote '{lotName}' alcanzará la madurez de cosecha en aproximadamente {daysRemaining} día(s). Se recomienda planificar logística.";

        await CreateAlertAsync(new CreateAlertDto(
            NotificationTypes.CosechaInminente,
            daysRemaining <= 1 ? NotificationSeverities.Critica : NotificationSeverities.Alta,
            title,
            message,
            fingerprint,
            lotId
        ), ct);
    }

    private static bool ShouldSendEmailForSeverity(string alertSeverity, string minSeverity)
    {
        int SeverityWeight(string s) => s switch
        {
            NotificationSeverities.Critica => 4,
            NotificationSeverities.Alta => 3,
            NotificationSeverities.Media => 2,
            _ => 1
        };

        return SeverityWeight(alertSeverity) >= SeverityWeight(minSeverity);
    }
}
