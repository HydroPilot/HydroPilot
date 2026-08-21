using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HydroPilotWeb.Models;

namespace HydroPilotWeb.Services;

/// <summary>Envelope parseado y normalizado del contrato de telemetría.</summary>
public sealed record ParsedReading(
    string ReadingId,
    string SensorRef,
    DateTime? ObservedAtUtc,
    decimal? Value,
    string? Unit,
    string? DeviceQuality);

public sealed record ParsedEnvelope(
    string SchemaVersion,
    string BatchId,
    string NodeId,
    long? Sequence,
    DateTime? SentAtUtc,
    string? FirmwareVersion,
    IReadOnlyList<ParsedReading> Readings);

/// <summary>
/// Parser del contrato v2 + adaptador del payload legado v1 (nodoId/timestamp/lecturas).
/// Normaliza timestamps a UTC: un DateTime sin zona se asume UTC (decisión de contrato).
/// El hash canónico es estable sin importar el orden de las propiedades del JSON.
/// </summary>
public static class TelemetryContractParser
{
    public sealed record ParseResult(ParsedEnvelope? Envelope, string? Error);

    public static ParseResult Parse(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return new(null, "El cuerpo debe ser un objeto JSON.");

        // Detecta el payload legado v1 por ausencia de batchId/schemaVersion.
        var hasV2Marker = body.TryGetProperty("batchId", out _);
        return hasV2Marker ? ParseV2(body) : ParseV1(body);
    }

    private static ParseResult ParseV2(JsonElement body)
    {
        if (!TryGetString(body, "batchId", out var batchId) || string.IsNullOrWhiteSpace(batchId))
            return new(null, $"Campo obligatorio faltante: 'batchId' ({TelemetryContract.ReasonBatchIdFaltante}).");
        if (!TryGetString(body, "nodeId", out var nodeId) || string.IsNullOrWhiteSpace(nodeId))
            return new(null, $"Campo obligatorio faltante: 'nodeId' ({TelemetryContract.ReasonNodeIdFaltante}).");

        var schemaVersion = TryGetString(body, "schemaVersion", out var sv) && !string.IsNullOrWhiteSpace(sv)
            ? sv.Trim()
            : TelemetryContract.SchemaVersionV2;

        long? sequence = null;
        if (body.TryGetProperty("sequence", out var seqEl) && seqEl.ValueKind == JsonValueKind.Number && seqEl.TryGetInt64(out var seq))
            sequence = seq;

        DateTime? sentAtUtc = null;
        if (TryGetString(body, "sentAt", out var sentAt) && DateTime.TryParse(sentAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var sent))
            sentAtUtc = NormalizeToUtc(sent);

        var firmware = TryGetString(body, "firmwareVersion", out var fw) ? fw : null;

        if (!body.TryGetProperty("readings", out var readingsEl) || readingsEl.ValueKind != JsonValueKind.Array || readingsEl.GetArrayLength() == 0)
            return new(null, $"El array 'readings' es requerido y no puede estar vacío.");

        var readings = new List<ParsedReading>(readingsEl.GetArrayLength());
        foreach (var item in readingsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                return new(null, "Cada elemento de 'readings' debe ser un objeto.");

            if (!TryGetString(item, "readingId", out var readingId) || string.IsNullOrWhiteSpace(readingId))
                return new(null, "Cada lectura requiere 'readingId' no vacío.");

            if (!TryGetString(item, "sensorRef", out var sensorRef) || string.IsNullOrWhiteSpace(sensorRef))
                return new(null, $"La lectura '{readingId}' requiere 'sensorRef' no vacío.");

            decimal? value = null;
            if (item.TryGetProperty("value", out var valueEl) && valueEl.ValueKind == JsonValueKind.Number && valueEl.TryGetDecimal(out var dec))
                value = dec;
            // "value": null se interpreta como faltante (nunca como cero).

            DateTime? observedAtUtc = null;
            if (TryGetString(item, "observedAt", out var observedAt) && DateTime.TryParse(observedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var obs))
                observedAtUtc = NormalizeToUtc(obs);

            var unit = TryGetString(item, "unit", out var u) ? u : null;
            var quality = TryGetString(item, "quality", out var q) ? q : null;

            readings.Add(new ParsedReading(readingId, sensorRef, observedAtUtc, value, unit, quality));
        }

        return new(new ParsedEnvelope(schemaVersion, batchId.Trim(), nodeId.Trim(), sequence, sentAtUtc, firmware, readings), null);
    }

    /// <summary>Adaptador del payload legado (nodoId/timestamp/lecturas con sensorRef/valor).</summary>
    private static ParseResult ParseV1(JsonElement body)
    {
        if (!TryGetString(body, "nodoId", out var nodoId) || string.IsNullOrWhiteSpace(nodoId))
            return new(null, "Payload no reconocido: faltan 'batchId' (v2) y 'nodoId' (v1).");

        DateTime? timestamp = null;
        if (TryGetString(body, "timestamp", out var ts) && DateTime.TryParse(ts, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedTs))
            timestamp = NormalizeToUtc(parsedTs);

        if (!body.TryGetProperty("lecturas", out var lecturasEl) || lecturasEl.ValueKind != JsonValueKind.Array || lecturasEl.GetArrayLength() == 0)
            return new(null, "El array 'lecturas' es requerido y no puede estar vacío.");

        var readings = new List<ParsedReading>(lecturasEl.GetArrayLength());
        foreach (var item in lecturasEl.EnumerateArray())
        {
            if (!TryGetString(item, "sensorRef", out var sensorRef) || string.IsNullOrWhiteSpace(sensorRef))
                return new(null, "Cada lectura v1 requiere 'sensorRef' no vacío.");

            decimal? value = null;
            if (item.TryGetProperty("valor", out var valueEl) && valueEl.ValueKind == JsonValueKind.Number && valueEl.TryGetDecimal(out var dec))
                value = dec;

            // Ids sintetizados estables: el mismo sensor+valor+fecha mapea al mismo readingId,
            // de modo que los reintentos idénticos se detecten como duplicados.
            readings.Add(new ParsedReading(
                SynthesizeId($"v1|{nodoId}|{sensorRef}|{ts ?? ""}|{value}"),
                sensorRef,
                timestamp,
                value,
                null,
                null));
        }

        return new(new ParsedEnvelope(
            TelemetryContract.SchemaVersionV1,
            SynthesizeId($"v1-batch|{nodoId}|{ts ?? ""}"),
            nodoId.Trim(),
            null,
            timestamp,
            null,
            readings), null);
    }

    /// <summary>
    /// Hash canónico del payload: estable ante cambios de orden de propiedades y de
    /// formato numérico. Se usa para idempotencia batch (mismo batchId + mismo hash).
    /// </summary>
    public static string ComputeCanonicalHash(ParsedEnvelope envelope)
    {
        var sb = new StringBuilder();
        sb.Append("{\"batchId\":\"").Append(JsonEncode(envelope.BatchId)).Append('"');
        sb.Append(",\"firmwareVersion\":").Append(envelope.FirmwareVersion is null ? "null" : $"\"{JsonEncode(envelope.FirmwareVersion)}\"");
        sb.Append(",\"nodeId\":\"").Append(JsonEncode(envelope.NodeId)).Append('"');
        sb.Append(",\"readings\":[");
        foreach (var (r, i) in envelope.Readings.OrderBy(r => r.ReadingId, StringComparer.Ordinal).Select((r, i) => (r, i)))
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"observedAt\":").Append(r.ObservedAtUtc is null ? "null" : $"\"{r.ObservedAtUtc.Value:O}\"");
            sb.Append(",\"quality\":").Append(r.DeviceQuality is null ? "null" : $"\"{JsonEncode(r.DeviceQuality)}\"");
            sb.Append(",\"readingId\":\"").Append(JsonEncode(r.ReadingId)).Append('"');
            sb.Append(",\"sensorRef\":\"").Append(JsonEncode(r.SensorRef)).Append('"');
            sb.Append(",\"unit\":").Append(r.Unit is null ? "null" : $"\"{JsonEncode(r.Unit)}\"");
            sb.Append(",\"value\":").Append(r.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null").Append('}');
        }
        sb.Append("],\"schemaVersion\":\"").Append(JsonEncode(envelope.SchemaVersion)).Append('"');
        sb.Append(",\"sentAt\":").Append(envelope.SentAtUtc is null ? "null" : $"\"{envelope.SentAtUtc.Value:O}\"");
        sb.Append(",\"sequence\":").Append(envelope.Sequence.HasValue ? envelope.Sequence.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null");
        sb.Append('}');

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Timestamp sin zona se asume UTC (decisión de contrato, documentada).</summary>
    private static DateTime NormalizeToUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

    private static string SynthesizeId(string seed)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return "legacy-" + Convert.ToHexString(bytes).ToLowerInvariant()[..40];
    }

    private static bool TryGetString(JsonElement element, string property, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(property, out var prop) || prop.ValueKind != JsonValueKind.String)
            return false;
        value = prop.GetString();
        return true;
    }

    private static string JsonEncode(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}