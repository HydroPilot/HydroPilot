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

    /// <summary>Envía una fotografía de planta para análisis de visión por la API web.</summary>
    Task<string?> SendPlantImageAsync(int plantId, string filePath, CancellationToken ct);
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

    public async Task<string?> SendPlantImageAsync(int plantId, string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
            return $"archivo no encontrado: {Path.GetFileName(filePath)}";

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var contentType = ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };

        using var content = new MultipartFormDataContent();
        await using var stream = File.OpenRead(filePath);
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", Path.GetFileName(filePath));

        using var response = await _http.PostAsync($"api/plantas/{plantId}/imagenes", content, ct);
        if (!response.IsSuccessStatusCode)
            return $"HTTP {(int)response.StatusCode} {await Body(response, ct)}";

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;
        var status = (root.TryGetProperty("status", out var s) || root.TryGetProperty("Status", out s)) ? s.GetString() : "?";
        var bls = (root.TryGetProperty("babyLeafScore", out var b) || root.TryGetProperty("BabyLeafScore", out b)) && b.ValueKind == JsonValueKind.Number ? b.GetDouble() : 0.0;
        string? npk = null;
        string? health = null;
        if (root.TryGetProperty("visualState", out var vs) || root.TryGetProperty("VisualState", out vs))
        {
            if (vs.ValueKind == JsonValueKind.Object)
            {
                if (vs.TryGetProperty("topNpkClass", out var n) || vs.TryGetProperty("TopNpkClass", out n))
                    npk = n.GetString();
                if (vs.TryGetProperty("topHealthClass", out var h) || vs.TryGetProperty("TopHealthClass", out h))
                    health = h.GetString();
            }
        }
        npk ??= "-";
        health ??= "-";
        return $"score {bls:0.#} ({status}) · NPK {npk} · {health}";
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<string> Body(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        return body.Length > 200 ? body[..200] : body;
    }

    private sealed record CommandsResponse(string NodeId, DateTime ServerTimeUtc, List<DeviceCommand> Commands);
}
