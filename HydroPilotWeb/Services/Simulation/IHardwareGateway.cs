namespace HydroPilotWeb.Services.Simulation;

/// <summary>
/// FRONTERA del módulo de simulación (SIM-07): la simulación NUNCA invoca
/// hardware (MQTT/GPIO/bombas/actuadores). Este contrato documenta esa frontera
/// y existe para que las pruebas de aislamiento puedan inyectar un ADAPTADOR FALSO
/// y verificar CERO invocaciones: ningún servicio de simulación recibe, depende ni
/// registra una implementación de este contrato. El módulo de control físico
/// (fase futura) podrá implementarlo sin tocar la simulación.
/// </summary>
public interface IHardwareGateway
{
    /// <summary>Envía un comando físico. La simulación nunca lo llama.</summary>
    Task SendCommandAsync(string target, string command, CancellationToken ct = default);
}