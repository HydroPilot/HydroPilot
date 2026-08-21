using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Forecasting;

namespace HydroPilotWeb.Controllers;

[ApiController]
[Route("api/forecasting")]
public class ForecastingController : ControllerBase
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly ForecastService _forecastService;
    private readonly ILogger<ForecastingController> _logger;

    public ForecastingController(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        ForecastService forecastService,
        ILogger<ForecastingController> logger)
    {
        _dbFactory = dbFactory;
        _forecastService = forecastService;
        _logger = logger;
    }

    /// <summary>
    /// Devuelve el forecast de un lote (F-01): el MISMO DTO que consume la UI.
    /// asOfDate simula la fecha de cálculo (sin datos futuros). Persiste un
    /// snapshot idempotente (una consulta repetida no duplica predicciones).
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [TypeFilter(typeof(ApiKeyOrSessionFilter))]
    public async Task<ActionResult<ForecastResult>> GetForecast(
        [FromQuery] int lotId,
        [FromQuery] DateOnly? asOfDate = null,
        CancellationToken ct = default)
    {
        var result = await _forecastService.GetForecastAsync(lotId, asOfDate, persistSnapshot: true, ct);

        if (result is null)
            return NotFound($"Lote '{lotId}' no encontrado.");

        return Ok(result);
    }

    /// <summary>
    /// Lista los lotes para el dropdown operativo (F-04): por defecto EXCLUYE
    /// cosechados y descartados de la proyección operativa. includeClosed=true
    /// devuelve todos (histórico).
    /// </summary>
    [HttpGet("lots")]
    [Authorize]
    public async Task<ActionResult<List<LotSummary>>> GetLots(
        [FromQuery] bool includeClosed = false,
        CancellationToken ct = default)
    {
        var lots = await _forecastService.GetLotsAsync(includeClosed, ct);
        return Ok(lots);
    }

    /// <summary>
    /// Crea un lote (F-04): no asume que el primer invernadero es el correcto.
    /// Si hay varios invernaderos, greenhouseId es obligatorio; con uno solo se
    /// usa el existente (compatibilidad con el script mock).
    /// </summary>
    [HttpPost("lots")]
    [TypeFilter(typeof(ApiKeyAuthFilter))]
    public async Task<ActionResult<LotSummary>> CreateLot(
        [FromBody] CreateLotRequest request,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        if (request.PlantedAreaM2 <= 0)
            return BadRequest("El área plantada debe ser positiva.");

        if (request.SowingDate > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1))
            return BadRequest("La fecha de siembra no puede estar en el futuro.");

        var cropType = await context.CropTypes
            .FirstOrDefaultAsync(c => c.Name == request.CropTypeName, ct);
        if (cropType is null)
            return BadRequest($"Tipo de cultivo '{request.CropTypeName}' no encontrado.");

        var status = await context.LotStatuses
            .FirstOrDefaultAsync(s => s.Name == request.Status, ct);
        if (status is null)
            return BadRequest($"Estado '{request.Status}' no encontrado.");

        Greenhouse? greenhouse;
        if (request.GreenhouseId is { } greenhouseId)
        {
            greenhouse = await context.Greenhouses.FindAsync([greenhouseId], ct);
            if (greenhouse is null)
                return BadRequest($"Invernadero '{greenhouseId}' no encontrado.");
        }
        else
        {
            var count = await context.Greenhouses.CountAsync(ct);
            if (count == 0)
                return BadRequest("No hay invernaderos registrados.");
            if (count > 1)
                return BadRequest("Hay varios invernaderos: especificá greenhouseId (no se asume el primero).");
            greenhouse = await context.Greenhouses.FirstAsync(ct);
        }

        var lot = new Models.Lot
        {
            GreenhouseId = greenhouse.Id,
            CropTypeId = cropType.Id,
            StatusId = status.Id,
            SowingDate = request.SowingDate,
            PlantedAreaM2 = request.PlantedAreaM2,
            CreatedAt = DateTime.UtcNow
        };

        context.Lots.Add(lot);
        await context.SaveChangesAsync(ct);

        _logger.LogInformation("Lote creado: id={LotId}, cultivo={Crop}, siembra={Sowing}",
            lot.Id, cropType.Name, request.SowingDate);

        return CreatedAtAction(nameof(GetLots), new { lotId = lot.Id }, new LotSummary(
            lot.Id,
            cropType.Name,
            status.Name,
            lot.SowingDate,
            lot.PlantedAreaM2
        ));
    }

    /// <summary>
    /// Registra la cosecha real de un lote (F-04): IDEMPOTENTE — un lote ya
    /// cerrado (COSECHADO con fecha) no se vuelve a cerrar ni duplica la fecha.
    /// </summary>
    [HttpPost("lots/{id:int}/harvest")]
    [TypeFilter(typeof(ApiKeyAuthFilter))]
    public async Task<IActionResult> RecordHarvest(
        int id,
        [FromBody] RecordHarvestRequest request,
        CancellationToken ct = default)
    {
        if (request.ActualYieldKg < 0)
            return BadRequest("El rendimiento real no puede ser negativo.");

        if (request.ActualHarvestDate is { } harvestDate
            && harvestDate > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1))
        {
            return BadRequest("La fecha de cosecha no puede estar en el futuro.");
        }

        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var lot = await context.Lots
            .Include(l => l.CropType)
            .Include(l => l.Status)
            .FirstOrDefaultAsync(l => l.Id == id, ct);

        if (lot is null)
            return NotFound($"Lote '{id}' no encontrado.");

        // Idempotencia: lote ya cerrado → devolver el estado existente sin cambios.
        if (lot.Status?.Name == LotStatusNames.Cosechado && lot.ActualHarvestDate.HasValue)
        {
            _logger.LogInformation("Cosecha repetida ignorada (idempotente) para lote {LotId}.", lot.Id);
            return Ok(new
            {
                lotId = lot.Id,
                lot.ActualYieldKg,
                lot.ActualHarvestDate,
                status = LotStatusNames.Cosechado,
                repeated = true
            });
        }

        lot.ActualYieldKg = request.ActualYieldKg;
        lot.ActualHarvestDate = request.ActualHarvestDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

        var harvestedStatus = await context.LotStatuses
            .FirstOrDefaultAsync(s => s.Name == LotStatusNames.Cosechado, ct);
        if (harvestedStatus is not null)
            lot.StatusId = harvestedStatus.Id;

        await context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Cosecha registrada lote {LotId}: {Yield} kg el {Date}",
            lot.Id, request.ActualYieldKg, lot.ActualHarvestDate);

        return Ok(new
        {
            lotId = lot.Id,
            lot.ActualYieldKg,
            lot.ActualHarvestDate,
            status = LotStatusNames.Cosechado,
            repeated = false
        });
    }
}