using System.Text.Json;

namespace HydroPilotWeb.Services.Control.IotHub;

/// <summary>
/// Procesa un mensaje de la Pi que llegó por IoT Hub (D2C) con los mismos servicios que el transporte HTTP:
/// lecturas → <see cref="TelemetryIngestionService"/> (idempotente por batchId, así un reenvío del hub no
/// duplica nada), resultados → <see cref="ControlService.ReportResultAsync"/> (idempotente por commandId).
/// El nodo es el dispositivo autenticado por el hub: un dispositivo no puede mandar datos de otro nodo.
/// </summary>
public sealed class IotHubMessageHandler
{
    public enum Outcome { Telemetry, Result, NoDevice, NodeMismatch, Invalid, UnknownType }

    public sealed record HandleResult(Outcome Outcome, string? Detail = null);

    private readonly TelemetryIngestionService _ingestion;
    private readonly ControlService _control;
    private readonly ILogger<IotHubMessageHandler> _logger;

    public IotHubMessageHandler(TelemetryIngestionService ingestion, ControlService control, ILogger<IotHubMessageHandler> logger)
    {
        _ingestion = ingestion;
        _control = control;
        _logger = logger;
    }

    public async Task<HandleResult> HandleAsync(string? deviceId, string type, byte[] body, DateTime nowUtc, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return new(Outcome.NoDevice);

        // Cualquier mensaje del dispositivo dice que está conectado (equivale a la consulta HTTP de comandos).
        await _control.MarkDeviceContactAsync(deviceId, nowUtc, ct);

        switch (type)
        {
            case IotHubMessages.TypeTelemetry:
            {
                ParsedEnvelope? envelope;
                string? error;
                try
                {
                    using var document = JsonDocument.Parse(body);
                    var parse = TelemetryContractParser.Parse(document.RootElement);
                    (envelope, error) = (parse.Envelope, parse.Error);
                }
                catch (JsonException ex)
                {
                    (envelope, error) = (null, ex.Message);
                }

                if (envelope is null)
                {
                    _logger.LogWarning("IoT Hub: lecturas inválidas de {Device}: {Error}", deviceId, error);
                    return new(Outcome.Invalid, error);
                }
                if (!string.Equals(envelope.NodeId, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("IoT Hub: {Device} mandó lecturas del nodo {Node}; se descartan.", deviceId, envelope.NodeId);
                    return new(Outcome.NodeMismatch, envelope.NodeId);
                }

                var outcome = await _ingestion.IngestAsync(envelope, ct);
                return new(Outcome.Telemetry, outcome.Status.ToString());
            }

            case IotHubMessages.TypeCommandResult:
            {
                if (IotHubMessages.ParseResult(body) is not { } result)
                    return new(Outcome.Invalid, "resultado sin commandId");

                var report = await _control.ReportResultAsync(deviceId, result.CommandId, result.ToRequest(), nowUtc, ct);
                return new(Outcome.Result, report.Outcome.ToString());
            }

            default:
                _logger.LogWarning("IoT Hub: tipo de mensaje desconocido '{Type}' de {Device}.", type, deviceId);
                return new(Outcome.UnknownType, type);
        }
    }
}
