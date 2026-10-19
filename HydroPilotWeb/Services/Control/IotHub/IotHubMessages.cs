using System.Text;
using System.Text.Json;

namespace HydroPilotWeb.Services.Control.IotHub;

/// <summary>
/// Formato de los mensajes por Azure IoT Hub (mismo JSON que por HTTP, ver plan-control-ph-ce.md sección 3).
/// De la Pi a la nube (D2C), la propiedad de aplicación "type" dice qué es: "telemetry" (el contrato v2 de
/// lecturas; es el default si falta) o "command-result". De la nube a la Pi (C2D) viaja el dose-command con
/// MessageId = commandId y vencimiento = expiresAt. Lógica pura.
/// </summary>
public static class IotHubMessages
{
    public const string TypeProperty = "type";
    public const string TypeTelemetry = "telemetry";
    public const string TypeCommandResult = "command-result";

    /// <summary>Propiedad del sistema con el dispositivo autenticado que mandó el mensaje.</summary>
    public const string DeviceIdProperty = "iothub-connection-device-id";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string TypeOf(IDictionary<string, object> properties) =>
        properties.TryGetValue(TypeProperty, out var value) && value?.ToString() is { Length: > 0 } type
            ? type.Trim().ToLowerInvariant()
            : TypeTelemetry;

    public static string? DeviceIdOf(IReadOnlyDictionary<string, object> systemProperties) =>
        systemProperties.TryGetValue(DeviceIdProperty, out var value) ? value?.ToString() : null;

    /// <summary>Resultado de un comando por D2C: el contrato del POST …/resultado más el commandId.</summary>
    public sealed record CommandResultMessage(
        Guid CommandId,
        string? Status,
        decimal? ExecutedAmount,
        DateTimeOffset? StartedAt,
        DateTimeOffset? FinishedAt,
        string? Error)
    {
        public DeviceResultRequest ToRequest() => new(Status, ExecutedAmount, StartedAt, FinishedAt, Error);
    }

    /// <summary>Null si el cuerpo no es un resultado válido (sin commandId o JSON roto).</summary>
    public static CommandResultMessage? ParseResult(ReadOnlySpan<byte> body)
    {
        try
        {
            var message = JsonSerializer.Deserialize<CommandResultMessage>(body, Json);
            return message is { CommandId: var id } && id != Guid.Empty ? message : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Cuerpo del dose-command (C2D), idéntico al que devuelve la consulta HTTP.</summary>
    public static byte[] CommandBody(DeviceCommandDto command) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command, Json));
}
