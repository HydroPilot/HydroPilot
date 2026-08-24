namespace HydroPilotWeb.Services.Optimization;

/// <summary>
/// Hook de eventos hacia Notifications (OPT-06): cuando una recomendación
/// estratégica NUEVA supera el umbral (o es prioridad alta), Optimization emite
/// un <see cref="RecommendationEventDto"/> SIN enviar email ni crear entregas.
/// El módulo de notifications implementa este contrato (bandeja interna,
/// preferencias, outbox) y se registra después de <see cref="NoopOptimizationEventSink"/>
/// en DI (último registro gana).
/// </summary>
public interface IOptimizationEventSink
{
    Task OnRecommendationCreatedAsync(RecommendationEventDto recommendation, CancellationToken ct = default);
}

/// <summary>
/// Sink por defecto: registra el evento en el log. Optimization nunca envía
/// emails directamente (plan 16 OPT-06: "No enviar el email desde Optimization").
/// </summary>
public sealed class NoopOptimizationEventSink : IOptimizationEventSink
{
    private static readonly Action<ILogger, int, int, string, string, Exception?> LogEvent =
        LoggerMessage.Define<int, int, string, string>(
            LogLevel.Information,
            new EventId(60_001, "OptimizationRecommendationCreated"),
            "Recomendación {RecommendationId} del lote {LotId} ({Type}, {Status}) creada: pendiente de bandeja de notificaciones.");

    private readonly ILogger<NoopOptimizationEventSink> _logger;

    public NoopOptimizationEventSink(ILogger<NoopOptimizationEventSink> logger) => _logger = logger;

    public Task OnRecommendationCreatedAsync(RecommendationEventDto recommendation, CancellationToken ct = default)
    {
        LogEvent(_logger, recommendation.RecommendationId, recommendation.LotId,
            recommendation.RecommendationType, recommendation.Status, null);
        return Task.CompletedTask;
    }
}