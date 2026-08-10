using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Services;

namespace HydroPilotWeb.Controllers;

[ApiController]
[Route("api/forecasting")]
public class ForecastingController : ControllerBase
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly GddService _gddService;
    private readonly YieldService _yieldService;
    private readonly ILogger<ForecastingController> _logger;

    public ForecastingController(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        GddService gddService,
        YieldService yieldService,
        ILogger<ForecastingController> logger)
    {
        _dbFactory = dbFactory;
        _gddService = gddService;
        _yieldService = yieldService;
        _logger = logger;
    }

    /// <summary>
    /// Devuelve el forecast de un lote: GDD acumulado, fecha de cosecha estimada y rendimiento.
    /// </summary>
    [HttpGet]
    [Authorize]
    [TypeFilter(typeof(ApiKeyOrAuthorizeFilter))]
    public async Task<ActionResult<ForecastingResponse>> GetForecast(
        [FromQuery] int lotId,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var lot = await context.Lots
            .Include(l => l.CropType)
            .Include(l => l.Status)
            .FirstOrDefaultAsync(l => l.Id == lotId, ct);

        if (lot is null)
            return NotFound($"Lote '{lotId}' no encontrado.");

        var dailyGdd = await _gddService.GetDailyGddByDateAsync(lot, ct);
        var accumulated = dailyGdd.Values.Sum();
        var futureProjection = await _gddService.GetFutureGddProjectionAsync(lot, ct: ct);
        var harvestDate = _gddService.EstimateHarvestDateAsync(lot, accumulated, futureProjection);
        var daysRemaining = harvestDate is not null
            ? (harvestDate.Value.ToDateTime(TimeOnly.MinValue) - DateTime.UtcNow.Date).Days
            : 0;

        var yield = await _yieldService.EstimateAsync(lot, accumulated, ct);

        // Persistir la predicción generada para historial
        context.Predictions.Add(new Models.Prediction
        {
            LotId = lot.Id,
            GeneratedAt = DateTime.UtcNow,
            EstimatedHarvestDate = harvestDate,
            AccumulatedGdd = accumulated,
            EstimatedYield = yield.Base,
            ModelVersion = "gdd-v1"
        });
        await context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Forecast lote {LotId}: GDD {Gdd}/{Target}, cosecha {Harvest}, rendimiento {Yield} kg",
            lot.Id, accumulated, lot.CropType?.GddTarget, harvestDate, yield.Base);

        var history = dailyGdd
            .OrderBy(kv => kv.Key)
            .Select(kv => new DailyGddPoint(kv.Key, kv.Value))
            .ToList();

        var avgDaily = futureProjection.Count > 0
            ? futureProjection.Average(p => p.Gdd)
            : 0m;

        // Precisión del modelo: comparar predicciones vs cosechas reales de ciclos cerrados
        var accuracy = await GetModelAccuracyAsync(ct);

        return Ok(new ForecastingResponse(
            LotId: lot.Id,
            CropType: lot.CropType?.Name ?? "Desconocido",
            Status: lot.Status?.Name,
            SowingDate: lot.SowingDate,
            AreaM2: lot.PlantedAreaM2,
            GddAccumulated: Math.Round(accumulated, 2),
            GddTarget: lot.CropType?.GddTarget ?? 0m,
            GddDailyAverage: Math.Round(avgDaily, 2),
            EstimatedHarvestDate: harvestDate,
            DaysRemaining: daysRemaining,
            YieldConservative: yield.Conservative,
            YieldBase: yield.Base,
            YieldOptimistic: yield.Optimistic,
            ConfidencePercent: yield.ConfidencePercent,
            AccuracyMape: accuracy.Mape,
            AccuracyDaysError: accuracy.DaysError,
            AccuracyCycles: accuracy.Cycles,
            GddHistory: history
        ));
    }

    /// <summary>
    /// Compara la última predicción de cada lote COSECHADO contra su resultado real.
    /// Retorna MAPE del rendimiento y error promedio en días de la fecha de cosecha.
    /// </summary>
    private async Task<(decimal? Mape, decimal? DaysError, int Cycles)> GetModelAccuracyAsync(
        CancellationToken ct)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var closedLots = await context.Lots
            .Where(l => l.ActualYieldKg.HasValue)
            .Select(l => new
            {
                l.Id,
                l.ActualYieldKg,
                l.ActualHarvestDate,
                LatestPrediction = l.Predictions
                    .OrderByDescending(p => p.GeneratedAt)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        var withPrediction = closedLots
            .Where(l => l.LatestPrediction is not null)
            .ToList();

        if (withPrediction.Count == 0)
            return (null, null, 0);

        var mapeValues = new List<decimal>();
        var dayErrors = new List<decimal>();

        foreach (var lot in withPrediction)
        {
            var pred = lot.LatestPrediction!;

            if (pred.EstimatedYield.HasValue && pred.EstimatedYield > 0)
            {
                mapeValues.Add(Math.Abs(pred.EstimatedYield.Value - lot.ActualYieldKg!.Value)
                               / lot.ActualYieldKg.Value * 100m);
            }

            if (pred.EstimatedHarvestDate.HasValue && lot.ActualHarvestDate.HasValue)
            {
                dayErrors.Add(Math.Abs(
                    (pred.EstimatedHarvestDate.Value.ToDateTime(TimeOnly.MinValue)
                     - lot.ActualHarvestDate.Value.ToDateTime(TimeOnly.MinValue)).Days));
            }
        }

        var mape = mapeValues.Count > 0 ? Math.Round(mapeValues.Average(), 1) : (decimal?)null;
        var days = dayErrors.Count > 0 ? Math.Round(dayErrors.Average(), 1) : (decimal?)null;

        return (mape, days, withPrediction.Count);
    }

    /// <summary>
    /// Lista los lotes disponibles para el dropdown de forecasting.
    /// </summary>
    [HttpGet("lots")]
    [Authorize]
    public async Task<ActionResult<List<LotSummary>>> GetLots(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var lots = await context.Lots
            .Include(l => l.CropType)
            .Include(l => l.Status)
            .OrderByDescending(l => l.SowingDate)
            .Select(l => new LotSummary(
                l.Id,
                l.CropType!.Name,
                l.Status!.Name,
                l.SowingDate,
                l.PlantedAreaM2
            ))
            .ToListAsync(ct);

        return Ok(lots);
    }

    /// <summary>
    /// Crea un lote (usado por el script de datos históricos mock).
    /// </summary>
    [HttpPost("lots")]
    [TypeFilter(typeof(ApiKeyAuthFilter))]
    public async Task<ActionResult<LotSummary>> CreateLot(
        [FromBody] CreateLotRequest request,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var cropType = await context.CropTypes
            .FirstOrDefaultAsync(c => c.Name == request.CropTypeName, ct);
        if (cropType is null)
            return BadRequest($"Tipo de cultivo '{request.CropTypeName}' no encontrado.");

        var status = await context.LotStatuses
            .FirstOrDefaultAsync(s => s.Name == request.Status, ct);
        if (status is null)
            return BadRequest($"Estado '{request.Status}' no encontrado.");

        var greenhouse = await context.Greenhouses.FirstOrDefaultAsync(ct);
        if (greenhouse is null)
            return BadRequest("No hay invernaderos registrados.");

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

        return CreatedAtAction(nameof(GetLots), new LotSummary(
            lot.Id,
            cropType.Name,
            status.Name,
            lot.SowingDate,
            lot.PlantedAreaM2
        ));
    }

    /// <summary>
    /// Registra la cosecha real de un lote (rendimiento kg y fecha). Usado por el script mock
    /// y por el productor al cerrar un ciclo. Marca el lote como COSECHADO.
    /// </summary>
    [HttpPost("lots/{id:int}/harvest")]
    [TypeFilter(typeof(ApiKeyAuthFilter))]
    public async Task<IActionResult> RecordHarvest(
        int id,
        [FromBody] RecordHarvestRequest request,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var lot = await context.Lots
            .Include(l => l.CropType)
            .FirstOrDefaultAsync(l => l.Id == id, ct);

        if (lot is null)
            return NotFound($"Lote '{id}' no encontrado.");

        lot.ActualYieldKg = request.ActualYieldKg;
        lot.ActualHarvestDate = request.ActualHarvestDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

        var harvestedStatus = await context.LotStatuses
            .FirstOrDefaultAsync(s => s.Name == "COSECHADO", ct);
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
            status = "COSECHADO"
        });
    }
}

public record RecordHarvestRequest(
    decimal ActualYieldKg,
    DateOnly? ActualHarvestDate
);

public record CreateLotRequest(
    string CropTypeName,
    string Status,
    DateOnly SowingDate,
    decimal PlantedAreaM2
);

public record LotSummary(
    int Id,
    string CropType,
    string Status,
    DateOnly SowingDate,
    decimal AreaM2
);
