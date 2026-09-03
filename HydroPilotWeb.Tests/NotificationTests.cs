using System.Net;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Notifications;
using HydroPilotWeb.Services.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HydroPilotWeb.Tests;

[Collection("anomalies-sql")]
public class NotificationTests
{
    private readonly AnomaliesSqlFixture _fixture;

    public NotificationTests(AnomaliesSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<User> EnsureTestUserAsync()
    {
        await using var context = _fixture.NewContext();
        var user = await context.Users.FirstOrDefaultAsync(u => u.Email == "test@hydropilot.local");
        if (user == null)
        {
            user = new User
            {
                GoogleSub = "google-sub-test-123",
                Email = "test@hydropilot.local",
                GivenName = "Usuario",
                Surname = "Test",
                Role = "Operador",
                CreatedAt = DateTime.UtcNow,
                LastLoginAt = DateTime.UtcNow
            };
            context.Users.Add(user);
            await context.SaveChangesAsync();
        }
        return user;
    }

    [Fact]
    public async Task CreateAlert_DeduplicatesActiveAlertsByFingerprint()
    {
        await EnsureTestUserAsync();
        var service = new NotificationService(_fixture.Factory, NullLogger<NotificationService>.Instance);
        var fingerprint = $"test-dedup-{Guid.NewGuid():N}";

        var dto = new CreateAlertDto(
            NotificationTypes.DesbalanceQuimico,
            NotificationSeverities.Critica,
            "Alerta de prueba deduplicación",
            "Mensaje de prueba",
            fingerprint
        );

        var first = await service.CreateAlertAsync(dto);
        var second = await service.CreateAlertAsync(dto);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Id, second.Id);

        await using var context = _fixture.NewContext();
        var alertCount = await context.NotificationAlerts.CountAsync(a => a.Fingerprint == fingerprint);
        Assert.Equal(1, alertCount);
    }

    [Fact]
    public async Task UserPreferences_DefaultToDisabledEmail_AndPersistCorrectly()
    {
        var user = await EnsureTestUserAsync();
        var service = new NotificationService(_fixture.Factory, NullLogger<NotificationService>.Instance);

        await using var context = _fixture.NewContext();

        // Eliminar preferencia previa si existe para probar inicialización por defecto
        var prev = await context.NotificationPreferences.FirstOrDefaultAsync(p => p.UserId == user.Id);
        if (prev != null)
        {
            context.NotificationPreferences.Remove(prev);
            await context.SaveChangesAsync();
        }

        var prefs = await service.GetUserPreferencesAsync(user.Id);
        Assert.NotNull(prefs);
        Assert.False(prefs.EmailEnabled, "Por decisión operativa, el email debe estar desactivado por defecto.");
        Assert.Equal(NotificationSeverities.Alta, prefs.MinSeverityEmail);
        Assert.Equal(3, prefs.HarvestAlertDays);

        // Modificar y guardar
        prefs.EmailEnabled = true;
        prefs.MinSeverityEmail = NotificationSeverities.Media;
        prefs.HarvestAlertDays = 5;
        prefs.QuietHoursEnabled = true;

        await service.SaveUserPreferencesAsync(user.Id, prefs);

        var updated = await service.GetUserPreferencesAsync(user.Id);
        Assert.True(updated.EmailEnabled);
        Assert.Equal(NotificationSeverities.Media, updated.MinSeverityEmail);
        Assert.Equal(5, updated.HarvestAlertDays);
        Assert.True(updated.QuietHoursEnabled);

        // Restaurar a false por defecto
        updated.EmailEnabled = false;
        await service.SaveUserPreferencesAsync(user.Id, updated);
    }

    [Fact]
    public async Task AlertGeneration_GeneratesInternalDelivery_AndRespectsEmailPreference()
    {
        var user = await EnsureTestUserAsync();
        var service = new NotificationService(_fixture.Factory, NullLogger<NotificationService>.Instance);

        await using var context = _fixture.NewContext();

        // 1. Con email desactivado (default)
        var userPrefs = await service.GetUserPreferencesAsync(user.Id);
        userPrefs.EmailEnabled = false;
        await service.SaveUserPreferencesAsync(user.Id, userPrefs);

        var fp1 = $"test-pref-disabled-{Guid.NewGuid():N}";
        var alert1 = await service.CreateAlertAsync(new CreateAlertDto(
            NotificationTypes.DesbalanceQuimico,
            NotificationSeverities.Critica,
            "Alerta con email desactivado",
            "Mensaje",
            fp1
        ));

        Assert.NotNull(alert1);

        var deliveries1 = await context.NotificationDeliveries
            .Where(d => d.AlertId == alert1.Id && d.UserId == user.Id)
            .ToListAsync();

        Assert.Contains(deliveries1, d => d.Channel == NotificationChannels.Internal);
        Assert.DoesNotContain(deliveries1, d => d.Channel == NotificationChannels.Email);

        // 2. Con email activado pero severidad mínima "Critica"
        userPrefs.EmailEnabled = true;
        userPrefs.MinSeverityEmail = NotificationSeverities.Critica;
        await service.SaveUserPreferencesAsync(user.Id, userPrefs);

        // Alerta Media -> no debe generar email
        var fp2 = $"test-pref-media-{Guid.NewGuid():N}";
        var alert2 = await service.CreateAlertAsync(new CreateAlertDto(
            NotificationTypes.RecomendacionEstrategica,
            NotificationSeverities.Media,
            "Recomendación media",
            "Mensaje",
            fp2
        ));

        var deliveries2 = await context.NotificationDeliveries
            .Where(d => d.AlertId == alert2!.Id && d.UserId == user.Id)
            .ToListAsync();

        Assert.Contains(deliveries2, d => d.Channel == NotificationChannels.Internal);
        Assert.DoesNotContain(deliveries2, d => d.Channel == NotificationChannels.Email);

        // Alerta Crítica -> DEBE generar entrega de email en outbox
        var fp3 = $"test-pref-critica-{Guid.NewGuid():N}";
        var alert3 = await service.CreateAlertAsync(new CreateAlertDto(
            NotificationTypes.DesbalanceQuimico,
            NotificationSeverities.Critica,
            "Anomalía Crítica",
            "Mensaje",
            fp3
        ));

        var deliveries3 = await context.NotificationDeliveries
            .Where(d => d.AlertId == alert3!.Id && d.UserId == user.Id)
            .ToListAsync();

        Assert.Contains(deliveries3, d => d.Channel == NotificationChannels.Internal);
        Assert.Contains(deliveries3, d => d.Channel == NotificationChannels.Email && d.Status == DeliveryStatuses.Pending);

        // Restaurar email a false
        userPrefs.EmailEnabled = false;
        await service.SaveUserPreferencesAsync(user.Id, userPrefs);
    }

    [Fact]
    public async Task InternalTray_MarksAsReadAccurately()
    {
        var user = await EnsureTestUserAsync();
        var service = new NotificationService(_fixture.Factory, NullLogger<NotificationService>.Instance);

        var alert = await service.CreateAlertAsync(new CreateAlertDto(
            NotificationTypes.Info,
            NotificationSeverities.Info,
            "Alerta lectura",
            "Mensaje lectura",
            $"read-test-{Guid.NewGuid():N}"
        ));

        var unreadBefore = await service.GetUnreadCountAsync(user.Id);
        Assert.True(unreadBefore > 0);

        var notifications = await service.GetInternalNotificationsAsync(user.Id, unreadOnly: true);
        var targetDelivery = notifications.FirstOrDefault(n => n.AlertId == alert!.Id);
        Assert.NotNull(targetDelivery);
        Assert.False(targetDelivery.IsRead);

        // Marcar leída individual
        await service.MarkAsReadAsync(targetDelivery.Id, user.Id);
        var unreadAfter = await service.GetUnreadCountAsync(user.Id);
        Assert.Equal(unreadBefore - 1, unreadAfter);

        // Marcar todas leídas
        await service.MarkAllAsReadAsync(user.Id);
        var unreadFinal = await service.GetUnreadCountAsync(user.Id);
        Assert.Equal(0, unreadFinal);
    }

    [Fact]
    public async Task SendTestEmailToAllUsers_QueuesOutboxDeliveries()
    {
        await EnsureTestUserAsync();
        var service = new NotificationService(_fixture.Factory, NullLogger<NotificationService>.Instance);

        await using var context = _fixture.NewContext();
        var userCountWithEmail = await context.Users.CountAsync(u => !string.IsNullOrWhiteSpace(u.Email));

        var result = await service.SendTestEmailToAllUsersAsync(1);
        Assert.True(result.Success);
        Assert.Equal(userCountWithEmail, result.UsersQueued);

        // Verificar que existan las entregas pendientes en NotificationDeliveries
        var testAlert = await context.NotificationAlerts
            .Where(a => a.Type == NotificationTypes.PruebaSistema)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync();

        Assert.NotNull(testAlert);
        var queuedDeliveries = await context.NotificationDeliveries
            .Where(d => d.AlertId == testAlert.Id && d.Channel == NotificationChannels.Email)
            .ToListAsync();

        Assert.Equal(userCountWithEmail, queuedDeliveries.Count);
        Assert.All(queuedDeliveries, d => Assert.Equal(DeliveryStatuses.Pending, d.Status));
    }

    [Fact]
    public void EmailTemplateBuilder_GeneratesValidHtml()
    {
        var html = EmailTemplateBuilder.BuildAlertEmail(
            "pH Critico en Lote Alpha",
            NotificationSeverities.Critica,
            NotificationTypes.DesbalanceQuimico,
            "El sensor registro 4.20 fuera de banda.",
            "Lote Alpha",
            "https://hydropilot.local/notifications"
        );

        Assert.NotNull(html);
        Assert.Contains("HydroPilot", html);
        Assert.Contains("CRITICA", html);
        Assert.Contains("pH Critico en Lote Alpha", html);
        Assert.Contains("Lote Alpha", html);
    }
}
