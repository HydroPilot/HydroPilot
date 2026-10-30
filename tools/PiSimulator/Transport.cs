using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace HydroPilot.PiSimulator;

/// <summary>Comando de dosis tal como lo manda la web (por HTTP o por IoT Hub, mismo JSON).</summary>
public sealed record DeviceCommand(
    string Type,
    Guid CommandId,
    string NodeId,
    string Actuator,
    decimal Amount,
    string Unit,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string Source);

/// <summary>Resultado de un comando. Actuator/Unit son solo para la consola (no viajan).</summary>
public sealed record CommandResult(
    string Status,
    decimal ExecutedAmount,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string? Error,
    string? Actuator,
    string? Unit);

/// <summary>Comando recibido; Handle es el mensaje C2D a completar (IoT Hub) o null (HTTP).</summary>
public sealed record ReceivedCommand(DeviceCommand Command, object? Handle);

public enum ReportOutcome { Accepted, Retry, Drop }

/// <summary>Cómo habla la Pi con la nube: HTTP (consulta) o Azure IoT Hub (MQTT).</summary>
public interface IPiTransport : IAsyncDisposable
{
    string Name { get; }

    /// <summary>Manda un batch de lecturas (contrato v2). Devuelve cuántas aceptó la web, si lo sabe.</summary>
    Task<int?> SendReadingsAsync(object batch, CancellationToken ct);

    /// <summary>Comandos que hay que ejecutar ahora.</summary>
    Task<IReadOnlyList<ReceivedCommand>> ReceiveCommandsAsync(CancellationToken ct);

    /// <summary>Confirma (o rechaza, si no se pudo leer) el comando recibido. HTTP: no hace nada.</summary>
    Task AcknowledgeAsync(ReceivedCommand command, bool accepted, CancellationToken ct);

    Task<(ReportOutcome Outcome, string? Detail)> ReportResultAsync(Guid commandId, CommandResult result, CancellationToken ct);

    /// <summary>Los circuitos de la Pi según la web; null si este transporte no lo sabe preguntar (IoT Hub).</summary>
    Task<DeviceConfig?> GetConfigAsync(CancellationToken ct);
}

/// <summary>Transporte HTTP de la fase 1: POST de lecturas, GET de comandos pendientes, POST de resultados.</summary>
public sealed class HttpPiTransport : IPiTransport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _node;

    public HttpPiTransport(HttpClient http, string node)
    {
        _http = http;
        _node = node;
    }

    public string Name => "HTTP";

    public async Task<int?> SendReadingsAsync(object batch, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("api/telemetria/lecturas", batch, Json, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {await Body(response, ct)}");

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        return result.TryGetProperty("inserted", out var inserted) ? inserted.GetInt32() : null;
    }

    public async Task<IReadOnlyList<ReceivedCommand>> ReceiveCommandsAsync(CancellationToken ct)
    {
        var response = await _http.GetFromJsonAsync<CommandsResponse>($"api/nodos/{_node}/comandos", Json, ct);
        return (response?.Commands ?? []).Select(c => new ReceivedCommand(c, null)).ToList();
    }

    public Task AcknowledgeAsync(ReceivedCommand command, bool accepted, CancellationToken ct) => Task.CompletedTask;

    public Task<DeviceConfig?> GetConfigAsync(CancellationToken ct) =>
        _http.GetFromJsonAsync<DeviceConfig>($"api/nodos/{_node}/circuitos", Json, ct);

    public async Task<(ReportOutcome Outcome, string? Detail)> ReportResultAsync(Guid commandId, CommandResult result, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(
            $"api/nodos/{_node}/comandos/{commandId}/resultado",
            new { result.Status, result.ExecutedAmount, result.StartedAt, result.FinishedAt, result.Error },
            Json, ct);

        if (response.IsSuccessStatusCode)
            return (ReportOutcome.Accepted, null);
        // La web no lo va a aceptar nunca: no se reintenta.
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            return (ReportOutcome.Drop, $"HTTP {(int)response.StatusCode} {await Body(response, ct)}");
        return (ReportOutcome.Retry, $"HTTP {(int)response.StatusCode}");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<string> Body(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        return body.Length > 200 ? body[..200] : body;
    }

    private sealed record CommandsResponse(string NodeId, DateTime ServerTimeUtc, List<DeviceCommand> Commands);
}
