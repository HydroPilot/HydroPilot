using Microsoft.AspNetCore.Mvc;
using HydroPilotWeb.Services.Simulation;

namespace HydroPilotWeb.Controllers;

/// <summary>
/// Endpoint de simulación (plan 14, SIM-06): POST preview SIN persistencia.
/// Recibe inputs del escenario y devuelve el resultado calculado; los valores
/// negativos o incoherentes se rechazan en servidor (400 con detalles).
/// La UI (Simulation.razor) consume el MISMO SimulationService, de modo que
/// API y pantalla no pueden divergir. No se persiste nada (SimulationRun queda
/// para una entrega posterior, decidida con el usuario).
/// </summary>
[ApiController]
[Route("api/simulaciones")]
public class SimulationController : ControllerBase
{
    private readonly SimulationService _simulationService;
    private readonly ILogger<SimulationController> _logger;

    public SimulationController(SimulationService simulationService, ILogger<SimulationController> logger)
    {
        _simulationService = simulationService;
        _logger = logger;
    }

    /// <summary>
    /// Simula un escenario y devuelve el resultado completo (GDD, fechas,
    /// rendimiento, costos, estrategias). Acepta sesión autenticada o X-Api-Key.
    /// La validación de negativos/moneda/texto ocurre en el servidor (SIM-05).
    /// </summary>
    [HttpPost("preview")]
    [TypeFilter(typeof(ApiKeyOrSessionFilter))]
    public async Task<ActionResult<SimulationResult>> Preview(
        [FromBody] SimulationRequest request,
        CancellationToken ct = default)
    {
        var preview = await _simulationService.PreviewAsync(request, ct);

        if (!preview.IsValid)
        {
            _logger.LogInformation("Simulación rechazada por validación: {Errors}",
                string.Join(" | ", preview.ValidationErrors));
            return BadRequest(new SimulationValidationProblem(preview.ValidationErrors));
        }

        return Ok(preview.Result);
    }

    /// <summary>Opciones de contexto (lotes y cultivos) para los selectores.</summary>
    [HttpGet("context-options")]
    [TypeFilter(typeof(ApiKeyOrSessionFilter))]
    public async Task<ActionResult<SimulationContextOptions>> ContextOptions(CancellationToken ct = default)
        => Ok(await _simulationService.GetContextOptionsAsync(ct));
}