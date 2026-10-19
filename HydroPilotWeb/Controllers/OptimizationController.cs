using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.Optimization;

namespace HydroPilotWeb.Controllers;

/// <summary>
/// API del módulo de optimización: el MISMO contrato que la vista (OptimizationViewDto /
/// RecommendationDto / DecisionResult). Recomendaciones informativas; no hay endpoints de control físico.
/// </summary>
[ApiController]
[Route("api/optimization")]
[Authorize]
public class OptimizationController : ControllerBase
{
    private readonly OptimizationService _optimization;

    public OptimizationController(OptimizationService optimization)
    {
        _optimization = optimization;
    }

    /// <summary>Lotes para el selector (en curso: Activo / En pausa).</summary>
    [HttpGet("lots")]
    public async Task<ActionResult<IReadOnlyList<LotOption>>> GetLots(CancellationToken ct = default) =>
        Ok(await _optimization.GetLotOptionsAsync(ct));

    /// <summary>Vista del lote: recalcula sus recomendaciones y devuelve las activas, el historial y los números.</summary>
    [HttpGet("state")]
    public async Task<ActionResult<OptimizationViewDto>> GetState([FromQuery] int lotId, CancellationToken ct = default)
    {
        var view = await _optimization.GetViewAsync(lotId, evaluate: true, ct);
        return view is null ? NotFound($"Lote '{lotId}' no encontrado.") : Ok(view);
    }

    /// <summary>Recalcula las recomendaciones del lote (una por problema; repetir no duplica).</summary>
    [HttpPost("generate")]
    public async Task<ActionResult<OptimizationViewDto>> Generate([FromBody] GenerateRequest request, CancellationToken ct = default)
    {
        var view = await _optimization.GetViewAsync(request.LotId, evaluate: true, ct);
        return view is null ? NotFound($"Lote '{request.LotId}' no encontrado.") : Ok(view);
    }

    /// <summary>Aceptar ("lo hago"): queda en curso y se verifica sola (idempotente).</summary>
    [HttpPost("{id:int}/accept")]
    public async Task<ActionResult<DecisionResult>> Accept(int id, CancellationToken ct = default)
    {
        var result = await _optimization.AcceptAsync(id, CurrentUserName(), ct);
        return result.Succeeded ? Ok(result) : Conflict(result);
    }

    /// <summary>Descartar con motivo (idempotente): el problema no vuelve mientras siga igual.</summary>
    [HttpPost("{id:int}/discard")]
    public async Task<ActionResult<DecisionResult>> Discard(int id, [FromBody] DiscardRequest request, CancellationToken ct = default)
    {
        var result = await _optimization.DiscardAsync(id, request.Reason, CurrentUserName(), ct);
        return result.Succeeded ? Ok(result) : Conflict(result);
    }

    private string? CurrentUserName() =>
        User.Identity?.Name
        ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
}

public sealed record GenerateRequest(int LotId);

public sealed record DiscardRequest(string Reason);
