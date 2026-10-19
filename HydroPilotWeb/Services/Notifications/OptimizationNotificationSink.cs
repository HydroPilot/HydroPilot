using System.Globalization;
using HydroPilotWeb.Models.Notifications;
using HydroPilotWeb.Models.Optimization;
using HydroPilotWeb.Services.Optimization;

namespace HydroPilotWeb.Services.Notifications;

public class OptimizationNotificationSink : IOptimizationEventSink
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-AR");

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
            OptimizationContractPriority.High => NotificationSeverities.Alta,
            OptimizationContractPriority.Medium => NotificationSeverities.Media,
            _ => NotificationSeverities.Info
        };

        var title = string.IsNullOrWhiteSpace(recommendation.LotName)
            ? recommendation.Title
            : $"{recommendation.LotName}: {recommendation.Title}";
        var message = recommendation.Impact is { } impact
            ? $"{recommendation.Summary} {ImpactText(impact, recommendation.ImpactKind)}"
            : recommendation.Summary;

        // Una alerta por recomendación: la recomendación ya es una por problema (no se repite).
        await _notificationService.CreateAlertAsync(new CreateAlertDto(
            NotificationTypes.RecomendacionEstrategica,
            severity,
            title,
            message,
            $"opt-rec-{recommendation.RecommendationId}",
            recommendation.LotId
        ), ct);

        _logger.LogInformation("Recomendación {RecommendationId} transformada en alerta de notificación.",
            recommendation.RecommendationId);
    }

    private static string ImpactText(decimal impact, string? kind)
    {
        var money = "$" + impact.ToString("N0", Culture);
        return kind switch
        {
            OptimizationImpactKind.Saving => $"Ahorro ≈ {money} por semana.",
            OptimizationImpactKind.Risk => $"En riesgo: {money}.",
            _ => $"A capturar: {money}.",
        };
    }
}
