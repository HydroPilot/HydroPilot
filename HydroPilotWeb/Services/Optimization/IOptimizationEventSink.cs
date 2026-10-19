namespace HydroPilotWeb.Services.Optimization;

/// <summary>
/// Hook de eventos hacia Notifications: cuando aparece una recomendación NUEVA de prioridad alta,
/// Optimization emite un <see cref="RecommendationEventDto"/> (una vez por problema) SIN enviar email
/// ni crear entregas. El módulo de notifications implementa este contrato.
/// </summary>
public interface IOptimizationEventSink
{
    Task OnRecommendationCreatedAsync(RecommendationEventDto recommendation, CancellationToken ct = default);
}

/// <summary>Sink por defecto (tests): registra el evento en el log.</summary>
public sealed class NoopOptimizationEventSink : IOptimizationEventSink
{
    private static readonly Action<ILogger, int, int, string, Exception?> LogEvent =
        LoggerMessage.Define<int, int, string>(
            LogLevel.Information,
            new EventId(60_001, "OptimizationRecommendationCreated"),
            "Recomendación {RecommendationId} del lote {LotId} creada: {Title}.");

    private readonly ILogger<NoopOptimizationEventSink> _logger;

    public NoopOptimizationEventSink(ILogger<NoopOptimizationEventSink> logger) => _logger = logger;

    public Task OnRecommendationCreatedAsync(RecommendationEventDto recommendation, CancellationToken ct = default)
    {
        LogEvent(_logger, recommendation.RecommendationId, recommendation.LotId, recommendation.Title, null);
        return Task.CompletedTask;
    }
}
