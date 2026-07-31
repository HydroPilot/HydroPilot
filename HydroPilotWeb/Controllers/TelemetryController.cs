using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;

namespace HydroPilotWeb.Controllers;

[ApiController]
[Route("api/telemetria")]
public class TelemetryController : ControllerBase
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly ILogger<TelemetryController> _logger;

    public TelemetryController(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        ILogger<TelemetryController> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>
    /// Recibe un batch de lecturas de sensores desde un nodo IoT.
    /// </summary>
    [HttpPost("lecturas")]
    [TypeFilter(typeof(ApiKeyAuthFilter))]
    public async Task<ActionResult<TelemetryResponse>> PostReadings(
        [FromBody] TelemetryRequest request,
        CancellationToken ct)
    {
        if (request.Lecturas is null || request.Lecturas.Count == 0)
            return BadRequest("El array 'lecturas' es requerido y no puede estar vacío.");

        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        // Buscar el nodo por identificador
        var node = await context.IotNodes
            .FirstOrDefaultAsync(n => n.Identifier == request.NodoId, ct);

        if (node is null)
        {
            _logger.LogWarning("Nodo IoT no encontrado: {NodoId}", request.NodoId);
            return NotFound($"Nodo '{request.NodoId}' no registrado.");
        }

        // Actualizar última conexión del nodo
        node.LastConnection = DateTime.UtcNow;

        // Cargar todos los sensores del nodo con sus tipos
        var sensors = await context.Sensors
            .Include(s => s.SensorType)
            .Where(s => s.NodeId == node.Id && s.IsActive)
            .ToListAsync(ct);

        var sensorMap = sensors.ToDictionary(
            s => s.Name.ToLowerInvariant(),
            s => s);

        var inserted = 0;
        var alerts = 0;

        foreach (var reading in request.Lecturas)
        {
            var refKey = reading.SensorRef.ToLowerInvariant();

            if (!sensorMap.TryGetValue(refKey, out var sensor))
            {
                _logger.LogWarning("Sensor no encontrado en nodo {NodoId}: {SensorRef}",
                    request.NodoId, reading.SensorRef);
                continue;
            }

            // Validar rango físico según tipo de sensor
            var (valid, alert) = ValidateReading(sensor.SensorType?.Name, reading.Valor);
            if (!valid)
            {
                _logger.LogWarning("Valor fuera de rango para sensor {Sensor}: {Valor}",
                    sensor.Name, reading.Valor);
                alerts++;
            }

            context.SensorReadings.Add(new SensorReading
            {
                SensorId = sensor.Id,
                MeasurementUnitId = sensor.MeasurementUnitId,
                Value = reading.Valor,
                Timestamp = request.Timestamp,
                CreatedAt = DateTime.UtcNow
            });

            inserted++;
        }

        await context.SaveChangesAsync(ct);

        _logger.LogInformation("Nodo {NodoId}: {Inserted} lecturas insertadas, {Alerts} alertas",
            request.NodoId, inserted, alerts);

        return CreatedAtAction(nameof(PostReadings), new TelemetryResponse(
            Insertadas: inserted,
            Alertas: alerts,
            Timestamp: request.Timestamp
        ));
    }

    /// <summary>
    /// Consulta lecturas históricas con filtros opcionales.
    /// </summary>
    [HttpGet("lecturas")]
    [Authorize]
    public async Task<ActionResult<List<ReadingQueryResponse>>> GetReadings(
        [FromQuery] int? sensorId,
        [FromQuery] DateTime? desde,
        [FromQuery] DateTime? hasta,
        [FromQuery] int limite = 100,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var query = context.SensorReadings
            .Include(r => r.Sensor!).ThenInclude(s => s.SensorType)
            .Include(r => r.Sensor!).ThenInclude(s => s.MeasurementUnit)
            .AsQueryable();

        if (sensorId.HasValue)
            query = query.Where(r => r.SensorId == sensorId.Value);

        if (desde.HasValue)
            query = query.Where(r => r.Timestamp >= desde.Value);

        if (hasta.HasValue)
            query = query.Where(r => r.Timestamp <= hasta.Value);

        var results = await query
            .OrderByDescending(r => r.Timestamp)
            .Take(limite)
            .Select(r => new ReadingQueryResponse(
                r.Id,
                r.SensorId,
                r.Sensor!.Name,
                r.Sensor.SensorType!.Name,
                r.Value,
                r.Sensor.MeasurementUnit!.Symbol,
                r.Timestamp,
                r.CreatedAt
            ))
            .ToListAsync(ct);

        return Ok(results);
    }

    /// <summary>
    /// Valida que el valor esté dentro de rangos físicos plausibles según el tipo de sensor.
    /// Retorna (esValido, generaAlerta).
    /// </summary>
    private static (bool valid, bool alert) ValidateReading(string? sensorType, decimal value)
    {
        return sensorType switch
        {
            "pH" => (value >= 0m && value <= 14m,
                     value < 5.0m || value > 7.0m),
            "CE" => (value >= 0m && value <= 10m,
                     value < 0.5m || value > 3.5m),
            "Temperatura" => (value >= -20m && value <= 80m,
                              value < 10m || value > 40m),
            "Humedad" => (value >= 0m && value <= 100m,
                          value < 20m || value > 95m),
            _ => (true, false) // Tipo desconocido, se acepta
        };
    }
}
