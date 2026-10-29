using HydroPilotWeb.Services.Circuits;
using HydroPilotWeb.Services.Control;
using Microsoft.AspNetCore.Mvc;

namespace HydroPilotWeb.Controllers;

/// <summary>
/// Comandos de dosis para la Raspberry Pi (mismo X-Api-Key que la telemetría). La Pi consulta sus pendientes
/// cada pocos segundos y responde el resultado de cada uno. Es el transporte HTTP de la fase 1; con Azure IoT Hub
/// lo mismo viaja como mensajes C2D/D2C (notas/control/plan-control-ph-ce.md).
/// </summary>
[ApiController]
[Route("api/nodos/{nodeId}")]
[TypeFilter(typeof(ApiKeyAuthFilter))]
public class DeviceCommandsController : ControllerBase
{
    private readonly ControlService _control;
    private readonly CircuitService _circuits;

    public DeviceCommandsController(ControlService control, CircuitService circuits)
    {
        _control = control;
        _circuits = circuits;
    }

    /// <summary>
    /// Los circuitos de la Pi: claves de sondas y bombas, litros, plantas activas y días del lote. La Pi virtual arma un
    /// tanque por circuito y lo vuelve a pedir cada minuto (trasplantes, cosechas, lotes nuevos).
    /// </summary>
    [HttpGet("circuitos")]
    public async Task<ActionResult<DeviceConfigResponse>> GetCircuits(string nodeId, CancellationToken ct)
    {
        var config = await _circuits.DeviceConfigAsync(nodeId, DateTime.UtcNow, ct);
        return config is null ? NotFound(new TelemetryErrorDto($"Nodo '{nodeId}' no registrado.")) : Ok(config);
    }

    /// <summary>Comandos a ejecutar (pendientes y entregados sin resultado, no vencidos).</summary>
    [HttpGet("comandos")]
    public async Task<ActionResult<DeviceCommandsResponse>> GetCommands(string nodeId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var commands = await _control.PollAsync(nodeId, now, ct);
        if (commands is null)
            return NotFound(new TelemetryErrorDto($"Nodo '{nodeId}' no registrado."));

        return Ok(new DeviceCommandsResponse(nodeId, now, commands));
    }

    /// <summary>Resultado de un comando: EXECUTED, REJECTED o FAILED. Repetirlo igual devuelve 200.</summary>
    [HttpPost("comandos/{commandId:guid}/resultado")]
    public async Task<ActionResult<DeviceResultResponse>> PostResult(
        string nodeId,
        Guid commandId,
        [FromBody] DeviceResultRequest request,
        CancellationToken ct)
    {
        var report = await _control.ReportResultAsync(nodeId, commandId, request, DateTime.UtcNow, ct);
        return report.Outcome switch
        {
            DeviceResultOutcome.Applied => Ok(new DeviceResultResponse(commandId, report.Status!, Duplicate: false)),
            DeviceResultOutcome.Duplicate => Ok(new DeviceResultResponse(commandId, report.Status!, Duplicate: true)),
            DeviceResultOutcome.Invalid => BadRequest(new TelemetryErrorDto(report.Message ?? "Resultado inválido.")),
            DeviceResultOutcome.Conflict => Conflict(new TelemetryErrorDto(report.Message ?? "El comando ya tiene otro resultado.")),
            _ => NotFound(new TelemetryErrorDto(report.Message ?? "No encontrado.")),
        };
    }
}
