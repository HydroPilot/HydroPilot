using HydroPilotWeb.Models.Notifications;
using HydroPilotWeb.Services.Optimization;

namespace HydroPilotWeb.Services.Notifications;

public class OptimizationNotificationSink : IOptimizationEventSink
{
    private readonly NotificationService _notificationService;
    private readonly ILogger<OptimizationNotificationSink> _logger;

    public OptimizationNotificationSink(
        NotificationService notificationService,
        ILogger<OptimizationNotificationSink> logger)
    {
        _notificationService = notificationService;
        _logger = logger;
    }

    public async Task OnRecommendationCreatedAsync(RecommendationEventDto recommendation, CancellationToken ct = default)
    {
        var severity = recommendation.Priority switch
        {
            "alta" or "critical" or "critica" => NotificationSeverities.Alta,
            "media" => NotificationSeverities.Media,
            _ => NotificationSeverities.Info
        };

        var title = $"Recomendación de Optimización: {recommendation.Direction}";
        var fingerprint = $"opt-rec-{recommendation.RecommendationId}";

        await _notificationService.CreateAlertAsync(new CreateAlertDto(
            NotificationTypes.RecomendacionEstrategica,
            severity,
            title,
            recommendation.Explanation,
            fingerprint,
            recommendation.LotId
        ), ct);

        _logger.LogInformation("Recomendación {RecommendationId} transformada en alerta de notificación.",
            recommendation.RecommendationId);
    }
}
