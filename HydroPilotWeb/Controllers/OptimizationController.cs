using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Optimization;

namespace HydroPilotWeb.Controllers;

/// <summary>
/// API del módulo de optimización (plan 16). Expone el MISMO contrato que
/// consume la UI (OptimizationStateDto / RecommendationDto / DecisionResult):
/// recomendaciones INFORMATIVAS, aceptar/descartar idempotentes y snapshot de
/// entradas persistido. No expone ningún endpoint de control físico.
/// </summary>
[ApiController]
[Route("api/optimization")]
[Authorize]
public class OptimizationController : ControllerBase
{
    private readonly OptimizationService _optimization;
    private readonly ForecastService _forecastService;

    public OptimizationController(OptimizationService optimization, ForecastService forecastService)
    {
        _optimization = optimization;
        _forecastService = forecastService;
    }

    /// <summary>Lotes para el selector (operativos: ACTIVO / EN_PAUSA).</summary>
    [HttpGet("lots")]
    public async Task<ActionResult<List<LotSummary>>> GetLots(CancellationToken ct = default)
    {
        var lots = await _forecastService.GetLotsAsync(includeClosed: false, ct);
        return Ok(lots);
    }

    /// <summary>Estado completo del lote (sin persistir).</summary>
    [HttpGet("state")]
    public async Task<ActionResult<OptimizationStateDto>> GetState([FromQuery] int lotId, CancellationToken ct = default)
    {
        var state = await _optimization.GetStateAsync(lotId, ct);
        if (state is null)
            return NotFound($"Lote '{lotId}' no encontrado.");
        return Ok(state);
    }

    /// <summary>
    /// Genera (o reutiliza) las recomendaciones del lote: idempotente por
    /// snapshot (repetir el cálculo con el mismo snapshot no duplica).
    /// </summary>
    [HttpPost("generate")]
    public async Task<ActionResult<OptimizationGenerationResult>> Generate([FromBody] GenerateRequest request, CancellationToken ct = default)
    {
        var result = await _optimization.GenerateAsync(request.LotId, ct);
        if (result is null)
            return NotFound($"Lote '{request.LotId}' no encontrado.");
        return Ok(result);
    }

    /// <summary>Aceptar una recomendación: registra acción manual (idempotente).</summary>
    [HttpPost("{id:int}/accept")]
    public async Task<ActionResult<DecisionResult>> Accept(int id, [FromBody] AcceptRequest request, CancellationToken ct = default)
    {
        var result = await _optimization.AcceptAsync(id, request.Note, CurrentUserName(), ct);
        return result.Succeeded ? Ok(result) : Conflict(result);
    }

    /// <summary>Descartar una recomendación con motivo (idempotente).</summary>
    [HttpPost("{id:int}/discard")]
    public async Task<ActionResult<DecisionResult>> Discard(int id, [FromBody] DiscardRequest request, CancellationToken ct = default)
    {
        var result = await _optimization.DiscardAsync(id, request.Reason, CurrentUserName(), ct);
        return result.Succeeded ? Ok(result) : Conflict(result);
    }

    /// <summary>Historial de recomendaciones (opcional filtrar por lote).</summary>
    [HttpGet("history")]
    public async Task<ActionResult<List<RecommendationDto>>> GetHistory([FromQuery] int? lotId = null, CancellationToken ct = default)
    {
        var history = await _optimization.GetHistoryAsync(lotId, ct);
        return Ok(history);
    }

    /// <summary>Expira recomendaciones PENDING sin decisión (mantenimiento).</summary>
    [HttpPost("expire")]
    public async Task<ActionResult<int>> ExpirePending(CancellationToken ct = default)
    {
        var count = await _optimization.ExpirePendingAsync(ct);
        return Ok(count);
    }

    private string? CurrentUserName() =>
        User.Identity?.Name
        ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
}

public sealed record GenerateRequest(int LotId);

public sealed record AcceptRequest(string? Note);

public sealed record DiscardRequest(string Reason);