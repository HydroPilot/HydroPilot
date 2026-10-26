using System.Text;
using System.Text.Json;
using HydroPilotWeb.Services.Control;
using HydroPilotWeb.Services.Control.IotHub;

namespace HydroPilotWeb.Tests.Unit;

/// <summary>Formato de los mensajes por Azure IoT Hub (D2C y C2D). Lógica pura.</summary>
public class IotHubMessagesTests
{
    [Fact]
    public void Tipo_de_mensaje_por_propiedad_y_lecturas_por_defecto()
    {
        Assert.Equal(IotHubMessages.TypeTelemetry, IotHubMessages.TypeOf(new Dictionary<string, object>()));
        Assert.Equal(IotHubMessages.TypeCommandResult,
            IotHubMessages.TypeOf(new Dictionary<string, object> { ["type"] = " Command-Result " }));
    }

    [Fact]
    public void Dispositivo_sale_de_la_propiedad_del_sistema()
    {
        Assert.Equal("rpi-inv-01", IotHubMessages.DeviceIdOf(new Dictionary<string, object> { ["iothub-connection-device-id"] = "rpi-inv-01" }));
        Assert.Null(IotHubMessages.DeviceIdOf(new Dictionary<string, object>()));
    }

    [Fact]
    public void Resultado_valido_y_resultados_invalidos()
    {
        var id = Guid.NewGuid();
        var parsed = IotHubMessages.ParseResult(Encoding.UTF8.GetBytes(
            $$"""{ "commandId": "{{id}}", "status": "EXECUTED", "executedAmount": 20, "startedAt": "2026-10-16T13:00:21Z" }"""));

        Assert.NotNull(parsed);
        Assert.Equal(id, parsed.CommandId);
        Assert.Equal(new DeviceResultRequest("EXECUTED", 20m, DateTimeOffset.Parse("2026-10-16T13:00:21Z"), null, null), parsed.ToRequest());

        Assert.Null(IotHubMessages.ParseResult(Encoding.UTF8.GetBytes("""{ "status": "EXECUTED" }""")));
        Assert.Null(IotHubMessages.ParseResult(Encoding.UTF8.GetBytes("no es json")));
    }

    [Fact]
    public void Comando_C2D_es_el_mismo_JSON_que_por_HTTP()
    {
        var id = Guid.NewGuid();
        var command = new DeviceCommandDto(DeviceContract.TypeDoseCommand, id, "rpi-inv-01", "ph-down", 20m, "ml",
            new DateTime(2026, 10, 16, 13, 0, 5, DateTimeKind.Utc), new DateTime(2026, 10, 16, 13, 5, 5, DateTimeKind.Utc),
            "auto", new DeviceCommandReason("pH", 6.82m, 6.0m, "pv-ph-1"));

        using var json = JsonDocument.Parse(IotHubMessages.CommandBody(command));
        var root = json.RootElement;
        Assert.Equal("dose-command", root.GetProperty("type").GetString());
        Assert.Equal(id, root.GetProperty("commandId").GetGuid());
        Assert.Equal("ph-down", root.GetProperty("actuator").GetString());
        Assert.Equal("2026-10-16T13:05:05Z", root.GetProperty("expiresAt").GetString());
        Assert.Equal(6.0m, root.GetProperty("reason").GetProperty("target").GetDecimal());
    }
}
