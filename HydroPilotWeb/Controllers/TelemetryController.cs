using System.Text.Json;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Controllers;

/// <summary>
/// Contrato confiable de ingesta de telemetría.
/// - Envelope versionado v2 (schemaVersion, batchId, nodeId, sentAt, lecturas con
///   readingId, sensorRef, observedAt UTC, valor, unidad, calidad reportada).
/// - Resultado por lectura: aceptada, duplicada, desconocida, inválida, fuera de
///   rango, sin lote, error de unidad, conflicto.
/// - Idempotencia batch+hash y por lectura+nodo (reintentos concurrentes seguros).
/// - Asignación temporal nodo→lote por fecha de observación (UNASSIGNED explícito).
/// El payload legado v1 (nodoId/timestamp/lecturas) se acepta por adaptador.
/// </summary>
[ApiController]
[Route("api/telemetria")]
public class TelemetryController : ControllerBase
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly TelemetryIngestionService _ingestion;
    private readonly NodeLotAssignmentService _assignments;
    private readonly TelemetryOptions _options;
    private readonly ILogger<TelemetryController> _logger;

    public TelemetryController(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        TelemetryIngestionService ingestion,
        NodeLotAssignmentService assignments,
        IOptions<TelemetryOptions> options,
        ILogger<TelemetryController> logger)
    {
        _dbFactory = dbFactory;
        _ingestion = ingestion;
        _assignments = assignments;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Recibe un batch de lecturas (v2) o el payload legado (v1).</summary>
    [HttpPost("lecturas")]
    [TypeFilter(typeof(ApiKeyAuthFilter))]
    public async Task<ActionResult<TelemetryIngestResponse>> PostReadings(
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var parse = TelemetryContractParser.Parse(body);
        if (parse.Envelope is null)
            return BadRequest(new TelemetryErrorDto(parse.Error ?? "Payload inválido."));

        var envelope = parse.Envelope;

        if (envelope.Readings.Count > _options.MaxReadingsPerBatch)
            return BadRequest(new TelemetryErrorDto(
                $"El batch supera el límite de {_options.MaxReadingsPerBatch} lecturas. ({TelemetryContract.ReasonLimiteBatch})"));

        IngestOutcome outcome;
        try
        {
            outcome = await _ingestion.IngestAsync(envelope, ct);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new TelemetryErrorDto(ex.Message));
        }

        return outcome.Status switch
        {
            IngestBatchStatus.NodeNotFound =>
                NotFound(new TelemetryErrorDto($"Nodo '{envelope.NodeId}' no registrado.")),

            IngestBatchStatus.Conflict =>
                Conflict(new TelemetryErrorDto(
                    $"El batch '{envelope.BatchId}' ya fue recibido con contenido distinto.")),

            IngestBatchStatus.TooManyRetries =>
                StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new TelemetryErrorDto("El batch no pudo procesarse por concurrencia; reintente.")),

            _ => Ok(BuildResponse(outcome)),
        };
    }

    private static TelemetryIngestResponse BuildResponse(IngestOutcome outcome)
    {
        var readings = outcome.Readings ?? [];
        return new TelemetryIngestResponse(
            outcome.BatchId,
            outcome.Status == IngestBatchStatus.Duplicate
                ? TelemetryContract.BatchResultDuplicado
                : TelemetryContract.BatchResultProcesado,
            Inserted: readings.Count(r => r.Result is TelemetryContract.ResultAceptada or TelemetryContract.ResultSinLote),
            Duplicated: readings.Count(r => r.Result is TelemetryContract.ResultDuplicada or TelemetryContract.ResultConflicto),
            Rejected: readings.Count(r => r.Result is TelemetryContract.ResultDesconocida
                or TelemetryContract.ResultInvalida
                or TelemetryContract.ResultFueraDeRango
                or TelemetryContract.ResultErrorUnidad),
            outcome.ReceivedAtUtc,
            readings);
    }

    /// <summary>
    /// Consulta lecturas persistidas con filtros opcionales (calidad, nodo, lote).
    /// Las lecturas inválidas persisten con Quality=INVALID (cuarentena consultable) y
    /// quedan fuera de GDD/anomalías mediante TelemetryQualityPolicy.IsOperationallyUsable.
    /// </summary>
    [HttpGet("lecturas")]
    [Authorize]
    public async Task<ActionResult<List<ReadingQueryResponse>>> GetReadings(
        [FromQuery] int? sensorId,
        [FromQuery] int? nodeId,
        [FromQuery] int? loteId,
        [FromQuery] string? calidad,
        [FromQuery] DateTime? desde,
        [FromQuery] DateTime? hasta,
        [FromQuery] int limite = 100,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var query = context.SensorReadings.AsQueryable();

        if (sensorId.HasValue)
            query = query.Where(r => r.SensorId == sensorId.Value);
        if (nodeId.HasValue)
            query = query.Where(r => r.NodeId == nodeId.Value);
        if (loteId.HasValue)
            query = query.Where(r => r.LotId == loteId.Value);
        if (!string.IsNullOrWhiteSpace(calidad))
            query = query.Where(r => r.Quality == calidad.Trim().ToUpperInvariant());
        if (desde.HasValue)
            query = query.Where(r => r.ObservedAtUtc >= desde.Value);
        if (hasta.HasValue)
            query = query.Where(r => r.ObservedAtUtc <= hasta.Value);

        var capped = Math.Clamp(limite, 1, 1000);

        var results = await query
            .OrderByDescending(r => r.ObservedAtUtc)
            .Take(capped)
            .Select(r => new ReadingQueryResponse(
                r.Id,
                r.SensorId,
                r.Sensor!.Name,
                r.Sensor!.SensorType!.Name,
                r.Value,
                r.Sensor!.MeasurementUnit != null ? r.Sensor.MeasurementUnit.Symbol : null,
                r.Timestamp,
                r.CreatedAt,
                r.Quality,
                r.QualityReason,
                r.IngestionResult,
                r.ObservedAtUtc,
                r.ReceivedAtUtc,
                r.NodeId,
                r.Node!.Identifier,
                r.LotId,
                r.Lot != null ? r.Lot.CropType!.Name : null,
                r.LotId.HasValue ? TelemetryContract.AssignmentStateAsignado : TelemetryContract.AssignmentStateSinAsignacion,
                r.NodeLotAssignmentId
            ))
            .ToListAsync(ct);

        return Ok(results);
    }

    /// <summary>Cuarentena de diagnóstico: lecturas rechazadas (no persistidas como lectura).</summary>
    [HttpGet("rechazos")]
    [Authorize]
    public async Task<ActionResult<List<RejectionQueryResponse>>> GetRejections(
        [FromQuery] int? nodeId,
        [FromQuery] string? batchId,
        [FromQuery] int limite = 100,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var query = context.TelemetryRejections.Include(r => r.Node).AsQueryable();

        if (nodeId.HasValue)
            query = query.Where(r => r.NodeId == nodeId.Value);
        if (!string.IsNullOrWhiteSpace(batchId))
            query = query.Where(r => r.BatchId == batchId);

        var results = await query
            .OrderByDescending(r => r.ReceivedAtUtc)
            .Take(Math.Clamp(limite, 1, 1000))
            .Select(r => new RejectionQueryResponse(
                r.Id,
                r.NodeId,
                r.Node!.Identifier,
                r.BatchId,
                r.ReadingId,
                r.SensorRef,
                r.ObservedAtUtc,
                r.ReceivedAtUtc,
                r.Value,
                r.Unit,
                r.Reason,
                r.Quality,
                r.Result))
            .ToListAsync(ct);

        return Ok(results);
    }

    /// <summary>Estado y frescura de los nodos (dashboard: NEVER_CONNECTED/ONLINE/DEGRADED/OFFLINE).</summary>
    [HttpGet("nodos")]
    [TypeFilter(typeof(ApiKeyOrSessionFilter))]
    public async Task<ActionResult<List<NodeStatusResponse>>> GetNodeStatuses(CancellationToken ct)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var since = DateTime.UtcNow.AddDays(-1);

        var projected = await context.IotNodes
            .Select(n => new
            {
                n.Id,
                n.Identifier,
                n.GreenhouseId,
                n.Status,
                n.ConnectionState,
                n.LastConnection,
                n.LastAcceptedAt,
                n.LastRejectedAt,
                n.ExpectedIntervalSeconds,
                SensorCount = n.Sensors.Count,
                RejectionsLast24h = n.Rejections.Count(r => r.ReceivedAtUtc >= since),
            })
            .OrderBy(x => x.Identifier)
            .ToListAsync(ct);

        var results = projected
            .Select(x => new NodeStatusResponse(
                x.Id, x.Identifier, x.GreenhouseId, x.Status, x.ConnectionState,
                x.LastConnection, x.LastAcceptedAt, x.LastRejectedAt,
                x.ExpectedIntervalSeconds, x.SensorCount, x.RejectionsLast24h))
            .ToList();

        return Ok(results);
    }

    /// <summary>Lista las asignaciones temporales nodo→lote.</summary>
    [HttpGet("asignaciones")]
    [TypeFilter(typeof(ApiKeyOrSessionFilter))]
    public async Task<ActionResult<IReadOnlyList<NodeLotAssignmentDto>>> GetAssignments(
        [FromQuery] int? nodeId,
        CancellationToken ct)
    {
        var list = await _assignments.ListAsync(nodeId, ct);
        return Ok(list);
    }

    /// <summary>Crea una asignación temporal nodo→lote (sin solapamientos).</summary>
    [HttpPost("asignaciones")]
    [TypeFilter(typeof(ApiKeyOrSessionFilter))]
    public async Task<ActionResult<NodeLotAssignmentDto>> CreateAssignment(
        [FromBody] NodeLotAssignmentCreateRequest request,
        CancellationToken ct)
    {
        var result = await _assignments.CreateAsync(
            request.NodeId,
            request.LotId,
            request.ValidFromUtc,
            request.ValidUntilUtc,
            request.Source ?? TelemetryContract.AssignmentSourceManual,
            ct);

        if (result.Assignment is null)
            return BadRequest(new TelemetryErrorDto(result.Error ?? "No se pudo crear la asignación."));

        return CreatedAtAction(nameof(GetAssignments), new { nodeId = result.Assignment.NodeId }, result.Assignment);
    }
}