using System.Text.Json;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Services;

public enum IngestBatchStatus
{
    Processed,
    Duplicate,
    Conflict,
    NodeNotFound,
    TooManyRetries,

    /// <summary>Indicador interno: un reintento concurrente ganó la carrera de insert
    /// (violación de unicidad); el caller reintenta con un contexto nuevo.</summary>
    RaceDetected,
}

/// <summary>Resultado por lectura, tal como se devuelve en la respuesta de ingesta.</summary>
public sealed record ReadingIngestionRecord(
    string ReadingId,
    string SensorRef,
    string Result,
    string? Quality,
    string? Reason,
    int? LotId,
    string? LotName,
    bool Assigned,
    string AssignmentState);

public sealed record IngestOutcome(
    IngestBatchStatus Status,
    string BatchId,
    IReadOnlyList<ReadingIngestionRecord>? Readings,
    DateTime ReceivedAtUtc);

/// <summary>
/// Servicio reutilizable de ingesta del contrato de telemetría (REST hoy, MQTT mañana).
/// Orquesta: idempotencia batch+hash, resolución de sensor por nodo, calidad por lectura,
/// asignación temporal nodo→lote por fecha de observación y estado de conexión del nodo.
/// Los reintentos concurrentes se resuelven con índices únicos + reintento de transacción.
/// </summary>
public sealed class TelemetryIngestionService
{
    private const int MaxAttempts = 3;

    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly TelemetryValidationService _validation;
    private readonly TelemetryOptions _options;
    private readonly ILogger<TelemetryIngestionService> _logger;

    public TelemetryIngestionService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        TelemetryValidationService validation,
        IOptions<TelemetryOptions> options,
        ILogger<TelemetryIngestionService> logger)
    {
        _dbFactory = dbFactory;
        _validation = validation;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IngestOutcome> IngestAsync(ParsedEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Readings.Count > _options.MaxReadingsPerBatch)
            throw new InvalidOperationException($"Batch supera el límite de {_options.MaxReadingsPerBatch} lecturas.");

        var receivedAtUtc = DateTime.UtcNow;
        var hash = TelemetryContractParser.ComputeCanonicalHash(envelope);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var outcome = await IngestAttemptAsync(envelope, hash, receivedAtUtc, attempt, ct);
            if (outcome.Status != IngestBatchStatus.RaceDetected)
                return outcome;

            _logger.LogWarning("Intento {Attempt}/{MaxAttempts} de {BatchId} abortado por carrera (violación de unicidad); reintentando.",
                attempt, MaxAttempts, envelope.BatchId);
        }

        _logger.LogError("Batch {BatchId} del nodo {NodeId} no pudo procesarse tras {MaxAttempts} intentos concurrentes.",
            envelope.BatchId, envelope.NodeId, MaxAttempts);
        return new IngestOutcome(IngestBatchStatus.TooManyRetries, envelope.BatchId, null, receivedAtUtc);
    }

    /// <summary>
    /// Un intento completo de ingesta. Utiliza el SaveChanges (transacción implícita)
    /// de EF Core en lugar de transacciones explícitas para ser compatible con la
    /// SqlServerRetryingExecutionStrategy configurada en Program.cs. Los índices únicos
    /// (batch y lectura+nodo) garantizan que un reintento concurrente no duplique datos.
    /// </summary>
    private async Task<IngestOutcome> IngestAttemptAsync(
        ParsedEnvelope envelope,
        string hash,
        DateTime receivedAtUtc,
        int attempt,
        CancellationToken ct)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        try
        {
            var node = await context.IotNodes
                .FirstOrDefaultAsync(n => n.Identifier == envelope.NodeId, ct);

            if (node is null)
                return new IngestOutcome(IngestBatchStatus.NodeNotFound, envelope.BatchId, null, receivedAtUtc);

            // ---- Idempotencia a nivel batch: mismo batchId + mismo hash ⇒ repetir resultado anterior ----
            var existingBatch = await context.TelemetryBatches
                .FirstOrDefaultAsync(b => b.NodeId == node.Id && b.BatchId == envelope.BatchId, ct);

            if (existingBatch is not null)
            {
                if (existingBatch.PayloadHash == hash)
                    return new IngestOutcome(IngestBatchStatus.Duplicate, envelope.BatchId,
                        DeserializeReadings(existingBatch.ResponseJson), receivedAtUtc);

                _logger.LogWarning("Batch {BatchId} del nodo {NodeId} reenviado con contenido distinto (conflicto).",
                    envelope.BatchId, envelope.NodeId);
                return new IngestOutcome(IngestBatchStatus.Conflict, envelope.BatchId, null, receivedAtUtc);
            }

            // ---- Resolución de sensores dentro del nodo (IOT-02) ----
            var sensors = await context.Sensors
                .Include(s => s.SensorType)
                .Include(s => s.MeasurementUnit)
                .Where(s => s.NodeId == node.Id && s.IsActive)
                .ToListAsync(ct);

                var sensorsByKey = new Dictionary<string, Sensor>(StringComparer.OrdinalIgnoreCase);
                foreach (var sensor in sensors)
                {
                    sensorsByKey[sensor.TechnicalKey] = sensor;
                    // Fallback: el nombre visible sigue resolviendo (compatibilidad con seed/legacy).
                    sensorsByKey[sensor.Name] = sensor;
                }

                // ---- Asignación temporal nodo→lote (IOT-05), resuelta por fecha de observación ----
                var assignments = await context.NodeLotAssignments
                    .Where(a => a.NodeId == node.Id)
                    .ToListAsync(ct);

                var lotIds = assignments.Select(a => a.LotId).Distinct().ToList();
                var lotNames = lotIds.Count == 0
                    ? new Dictionary<int, string>()
                    : await context.Lots
                        .Where(l => lotIds.Contains(l.Id))
                        .Select(l => new { l.Id, Label = l.CropType!.Name })
                        .ToDictionaryAsync(l => l.Id, l => l.Label, ct);

                // ---- Lecturas ya persistidas del nodo (idempotencia por lectura, IOT-04) ----
                var externalIds = envelope.Readings.Select(r => r.ReadingId).Distinct().ToList();
                var existingReadings = await context.SensorReadings
                    .Where(r => r.NodeId == node.Id && externalIds.Contains(r.ExternalReadingId))
                    .Select(r => new { r.ExternalReadingId, r.Value, r.ObservedAtUtc, r.Quality, r.IngestionResult, r.LotId })
                    .ToListAsync(ct);
                var existingById = existingReadings.ToDictionary(x => x.ExternalReadingId, StringComparer.Ordinal);

                var results = new List<ReadingIngestionRecord>(envelope.Readings.Count);
                var seenInBatch = new HashSet<string>(StringComparer.Ordinal);
                var nowUtc = DateTime.UtcNow;
                var anyAccepted = false;
                var anyRejected = false;

                foreach (var reading in envelope.Readings)
                {
                    // Mismo readingId dos veces dentro del mismo batch ⇒ segunda ocurrencia duplicada.
                    if (!seenInBatch.Add(reading.ReadingId))
                    {
                        results.Add(RejectedResult(reading, TelemetryContract.ResultDuplicada, null, null, "duplicada_en_batch"));
                        continue;
                    }

                    // Idempotencia por lectura: mismo id externo con distinto contenido ⇒ conflicto.
                    if (existingById.TryGetValue(reading.ReadingId, out var existingReading))
                    {
                        var identical = existingReading.Value == reading.Value &&
                                        (reading.ObservedAtUtc is null || existingReading.ObservedAtUtc == reading.ObservedAtUtc);
                        results.Add(identical
                            ? new ReadingIngestionRecord(reading.ReadingId, reading.SensorRef,
                                existingReading.IngestionResult, existingReading.Quality, null,
                                existingReading.LotId, existingReading.LotId.HasValue && lotNames.TryGetValue(existingReading.LotId.Value, out var ln) ? ln : null,
                                existingReading.LotId.HasValue,
                                existingReading.LotId.HasValue ? TelemetryContract.AssignmentStateAsignado : TelemetryContract.AssignmentStateSinAsignacion)
                            : RejectedResult(reading, TelemetryContract.ResultConflicto, null, null,
                                $"readingId '{reading.ReadingId}' ya existe con contenido distinto"));
                        continue;
                    }

                    if (!sensorsByKey.TryGetValue(reading.SensorRef, out var sensor))
                    {
                        anyRejected = true;
                        AddRejection(context, node.Id, envelope, reading, TelemetryContract.ReasonSensorDesconocido,
                            TelemetryContract.QualityInvalid, TelemetryContract.ResultDesconocida, receivedAtUtc);
                        results.Add(RejectedResult(reading, TelemetryContract.ResultDesconocida, TelemetryContract.QualityInvalid,
                            TelemetryContract.ReasonSensorDesconocido, "sensor_no_registrado_en_nodo"));
                        continue;
                    }

                    var classification = _validation.Classify(
                        sensor.SensorType?.Name,
                        reading.Value,
                        reading.ObservedAtUtc,
                        reading.Unit,
                        sensor.MeasurementUnit?.Symbol,
                        reading.DeviceQuality,
                        nowUtc,
                        node.ExpectedIntervalSeconds);

                    if (!classification.Persisted)
                    {
                        anyRejected = true;
                        AddRejection(context, node.Id, envelope, reading, classification.Reason!,
                            classification.Quality, classification.Result, receivedAtUtc);
                        results.Add(RejectedResult(reading, classification.Result, classification.Quality,
                            classification.Reason, classification.Reason));
                        continue;
                    }

                    // Lectura aceptada (o cuarentena INVALID): resolver lote por fecha de observación.
                    var assignment = ResolveAssignment(assignments, reading.ObservedAtUtc!.Value);

                    // "Aceptada" se matiza según la asignación (sin_lote si no hay);
                    // los demás resultados (fuera_de_rango) se conservan tal cual.
                    var readingResult = classification.Result == TelemetryContract.ResultAceptada
                        ? (assignment is null ? TelemetryContract.ResultSinLote : TelemetryContract.ResultAceptada)
                        : classification.Result;

                    var sensorReading = new SensorReading
                    {
                        SensorId = sensor.Id,
                        NodeId = node.Id,
                        LotId = assignment?.LotId,
                        NodeLotAssignmentId = assignment?.Id,
                        ExternalReadingId = reading.ReadingId,
                        Value = reading.Value!.Value,
                        MeasurementUnitId = sensor.MeasurementUnitId,
                        ObservedAtUtc = reading.ObservedAtUtc.Value,
                        ReceivedAtUtc = receivedAtUtc,
                        Timestamp = reading.ObservedAtUtc.Value,
                        Quality = classification.Quality,
                        QualityReason = classification.Reason,
                        IngestionResult = readingResult,
                    };
                    context.SensorReadings.Add(sensorReading);

                    anyAccepted = true;
                    results.Add(new ReadingIngestionRecord(
                        reading.ReadingId, reading.SensorRef,
                        readingResult, classification.Quality, classification.Reason,
                        assignment?.LotId,
                        assignment?.LotId is int lotId && lotNames.TryGetValue(lotId, out var lotName) ? lotName : null,
                        assignment is not null,
                        assignment is null ? TelemetryContract.AssignmentStateSinAsignacion : TelemetryContract.AssignmentStateAsignado));
                }

                node.LastConnection = receivedAtUtc;
                if (anyAccepted) node.LastAcceptedAt = receivedAtUtc;
                if (anyRejected) node.LastRejectedAt = receivedAtUtc;

                if (node.ConnectionState == TelemetryContract.ConnectionNeverConnected && anyAccepted)
                {
                    // El monitor lo recalcula con su propio timing; aquí solo se asegura
                    // que un nodo recién conectado deja de estar "nunca conectado".
                    node.ConnectionState = NodeConnectionEvaluator.Evaluate(
                        node.LastAcceptedAt, receivedAtUtc, node.ExpectedIntervalSeconds, _options.DegradedIntervalFactor);
                }

                var responseJson = JsonSerializer.Serialize(results);
                context.TelemetryBatches.Add(new TelemetryBatch
                {
                    NodeId = node.Id,
                    BatchId = envelope.BatchId,
                    SchemaVersion = envelope.SchemaVersion,
                    Sequence = envelope.Sequence,
                    SentAtUtc = envelope.SentAtUtc,
                    ReceivedAtUtc = receivedAtUtc,
                    FirmwareVersion = envelope.FirmwareVersion,
                    PayloadHash = hash,
                    Result = TelemetryContract.BatchResultProcesado,
                    ResponseJson = responseJson,
                });

                await context.SaveChangesAsync(ct);

                _logger.LogInformation("Nodo {NodeId} batch {BatchId}: {Accepted} aceptadas, {Rejected} rechazadas, {Duplicated} duplicadas.",
                    envelope.NodeId, envelope.BatchId,
                    results.Count(r => r.Result is TelemetryContract.ResultAceptada or TelemetryContract.ResultSinLote),
                    results.Count(r => r.Result is TelemetryContract.ResultDesconocida
                        or TelemetryContract.ResultInvalida or TelemetryContract.ResultErrorUnidad),
                    results.Count(r => r.Result is TelemetryContract.ResultDuplicada or TelemetryContract.ResultConflicto or TelemetryContract.ResultFueraDeRango));

                return new IngestOutcome(IngestBatchStatus.Processed, envelope.BatchId, results, receivedAtUtc);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Un reintento concurrente insertó el batch o una lectura primero:
                // se descarta este intento (SaveChanges ya hizo rollback de su transacción)
                // y el caller reintenta con un contexto nuevo.
                _logger.LogWarning(ex, "Intento {Attempt}/{MaxAttempts} de {BatchId} abortado por violación de unicidad (reintento concurrente).",
                    attempt, MaxAttempts, envelope.BatchId);
                return new IngestOutcome(IngestBatchStatus.RaceDetected, envelope.BatchId, null, receivedAtUtc);
            }
    }

    /// <summary>Asigna por fecha de observación: la asignación vigente más reciente.</summary>
    public static NodeLotAssignment? ResolveAssignment(IReadOnlyList<NodeLotAssignment> assignments, DateTime observedAtUtc) =>
        assignments
            .Where(a => a.ValidFromUtc <= observedAtUtc && (a.ValidUntilUtc is null || a.ValidUntilUtc > observedAtUtc))
            .OrderByDescending(a => a.ValidFromUtc)
            .FirstOrDefault();

    private static void AddRejection(
        HydroPilotDbContext context,
        int nodeId,
        ParsedEnvelope envelope,
        ParsedReading reading,
        string reason,
        string quality,
        string result,
        DateTime receivedAtUtc)
    {
        context.TelemetryRejections.Add(new TelemetryRejection
        {
            NodeId = nodeId,
            BatchId = envelope.BatchId,
            ReadingId = reading.ReadingId,
            SensorRef = reading.SensorRef,
            ObservedAtUtc = reading.ObservedAtUtc,
            ReceivedAtUtc = receivedAtUtc,
            Value = reading.Value,
            Unit = reading.Unit,
            Reason = reason,
            Quality = quality,
            Result = result,
        });
    }

    private static ReadingIngestionRecord RejectedResult(
        ParsedReading reading, string result, string? quality, string? reason, string? fallbackReason) =>
        new(reading.ReadingId, reading.SensorRef, result, quality, reason ?? fallbackReason, null, null, false,
            TelemetryContract.AssignmentStateSinAsignacion);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException sql && (sql.Number == 2601 || sql.Number == 2627);

    private static IReadOnlyList<ReadingIngestionRecord>? DeserializeReadings(string? responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ReadingIngestionRecord>>(responseJson);
        }
        catch (JsonException)
        {
            return [];
        }
    }
}