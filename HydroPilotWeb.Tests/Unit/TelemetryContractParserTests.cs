using System.Text.Json;
using HydroPilotWeb.Services;
using Xunit;

namespace HydroPilotWeb.Tests.Unit;

/// <summary>Parser del contrato v2 y adaptador legado v1.</summary>
public class TelemetryContractParserTests
{
    private static TelemetryContractParser.ParseResult Parse(string json) =>
        TelemetryContractParser.Parse(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Envelope_v2_completo_parsa_correctamente()
    {
        var json = """
        {
          "schemaVersion": 2,
          "batchId": "b-001",
          "nodeId": "rpi-inv-01",
          "sequence": 7,
          "sentAt": "2026-08-01T12:00:00Z",
          "firmwareVersion": "1.2.3",
          "readings": [
            { "readingId": "r-1", "sensorRef": "ph-solucion", "observedAt": "2026-08-01T12:00:00Z", "value": 5.9, "unit": "pH", "quality": "VALID" }
          ]
        }
        """;

        var result = Parse(json);
        Assert.Null(result.Error);
        var envelope = result.Envelope!;
        Assert.Equal("2", envelope.SchemaVersion);
        Assert.Equal("b-001", envelope.BatchId);
        Assert.Equal("rpi-inv-01", envelope.NodeId);
        Assert.Equal(7, envelope.Sequence);
        Assert.Equal("1.2.3", envelope.FirmwareVersion);
        var reading = Assert.Single(envelope.Readings);
        Assert.Equal("r-1", reading.ReadingId);
        Assert.Equal("ph-solucion", reading.SensorRef);
        Assert.Equal(5.9m, reading.Value);
        Assert.Equal("pH", reading.Unit);
        Assert.Equal(DateTimeKind.Utc, reading.ObservedAtUtc!.Value.Kind);
    }

    [Fact]
    public void JSON_incompleto_muestra_error_estructurado()
    {
        var result = Parse("""{ "batchId": "b-1" }""");
        Assert.Null(result.Envelope);
        Assert.Contains("nodeId", result.Error);
    }

    [Fact]
    public void Faltan_readings_da_error()
    {
        var result = Parse("""{ "batchId": "b-1", "nodeId": "n-1", "readings": [] }""");
        Assert.Null(result.Envelope);
        Assert.Contains("readings", result.Error);
    }

    [Fact]
    public void Lectura_sin_readingId_da_error()
    {
        var result = Parse("""
            { "batchId": "b-1", "nodeId": "n-1", "readings": [ { "sensorRef": "ph-solucion" } ] }
            """);
        Assert.Null(result.Envelope);
        Assert.Contains("readingId", result.Error);
    }

    [Fact]
    public void Payload_legado_v1_se_adapta_con_ids_sintetizados()
    {
        var result = Parse("""
            { "nodoId": "rpi-inv-01", "timestamp": "2026-08-01T12:00:00Z",
              "lecturas": [ { "sensorRef": "ph-solucion", "valor": 6.0 } ] }
            """);
        Assert.Null(result.Error);
        var envelope = result.Envelope!;
        Assert.Equal("1", envelope.SchemaVersion);
        Assert.Equal("rpi-inv-01", envelope.NodeId);
        Assert.StartsWith("legacy-", envelope.BatchId);
        var reading = Assert.Single(envelope.Readings);
        Assert.StartsWith("legacy-", reading.ReadingId);
        Assert.Equal(6.0m, reading.Value);
        Assert.NotNull(reading.ObservedAtUtc);
    }

    [Fact]
    public void Timestamp_sin_zona_se_asume_UTC()
    {
        var result = Parse("""
            { "batchId": "b-1", "nodeId": "n-1",
              "readings": [ { "readingId": "r-1", "sensorRef": "ph-solucion", "observedAt": "2026-08-01T12:00:00", "value": 6.0 } ] }
            """);
        var observed = result.Envelope!.Readings[0].ObservedAtUtc!.Value;
        Assert.Equal(DateTimeKind.Utc, observed.Kind);
        Assert.Equal(new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc), observed);
    }

    [Fact]
    public void Timestamp_con_offset_se_convierte_a_UTC()
    {
        var result = Parse("""
            { "batchId": "b-1", "nodeId": "n-1",
              "readings": [ { "readingId": "r-1", "sensorRef": "ph-solucion", "observedAt": "2026-08-01T15:00:00+03:00", "value": 6.0 } ] }
            """);
        var observed = result.Envelope!.Readings[0].ObservedAtUtc!.Value;
        Assert.Equal(new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc), observed);
    }

    [Fact]
    public void Hash_canonico_es_estable_con_propiedades_reordenadas()
    {
        var a = Parse("""
            { "batchId": "b-1", "nodeId": "n-1", "firmwareVersion": "1.0",
              "readings": [ { "readingId": "r-1", "sensorRef": "ph", "observedAt": "2026-08-01T12:00:00Z", "value": 6.0, "unit": "pH" } ] }
            """).Envelope!;
        var b = Parse("""
            { "nodeId": "n-1", "batchId": "b-1", "firmwareVersion": "1.0",
              "readings": [ { "unit": "pH", "value": 6.0, "sensorRef": "ph", "observedAt": "2026-08-01T12:00:00Z", "readingId": "r-1" } ] }
            """).Envelope!;

        Assert.Equal(TelemetryContractParser.ComputeCanonicalHash(a), TelemetryContractParser.ComputeCanonicalHash(b));
    }

    [Fact]
    public void Hash_diferia_si_el_valor_cambia()
    {
        var a = Parse("""{ "batchId": "b-1", "nodeId": "n-1", "readings": [ { "readingId": "r-1", "sensorRef": "ph", "value": 6.0 } ] }""").Envelope!;
        var b = Parse("""{ "batchId": "b-1", "nodeId": "n-1", "readings": [ { "readingId": "r-1", "sensorRef": "ph", "value": 6.1 } ] }""").Envelope!;
        Assert.NotEqual(TelemetryContractParser.ComputeCanonicalHash(a), TelemetryContractParser.ComputeCanonicalHash(b));
    }
}