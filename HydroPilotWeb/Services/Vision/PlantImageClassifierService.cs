namespace HydroPilotWeb.Services.Vision;

/// <summary>
/// Adaptador de compatibilidad para inferencia de visión NPK (Plan 19).
/// Reenvía la ejecución al servicio unificado BabyLeafPipelineService.
/// </summary>
public class PlantImageClassifierService
{
    private readonly BabyLeafPipelineService _pipeline;

    public PlantImageClassifierService(BabyLeafPipelineService pipeline)
    {
        _pipeline = pipeline;
    }

    public Task<ClassificationResult> ClassifyAsync(Stream imageStream, CancellationToken ct = default)
        => _pipeline.ClassifyAsync(imageStream, ct);
}
