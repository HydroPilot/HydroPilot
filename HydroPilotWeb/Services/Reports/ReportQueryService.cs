using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Services.Reports;

/// <summary>
/// Consultas de reportes del módulo Reports (plan 13, REP-01..REP-08).
///
/// Reglas del módulo:
/// - DTOs independientes de la UI (HydroPilotWeb.Services.Reports).
/// - Consultas proyectadas y AsNoTracking, con límites de rango y filas
///   (ReportLimits); nunca se carga todo el histórico sin control.
/// - Las estadísticas de telemetría excluyen lecturas no utilizables de
///   promedios/min/max/mediana (política única: TelemetryQualityPolicy).
/// - La predominancia comercial con "Mixto" se consume del dominio
///   (LotAggregateService / Predominance); Reports NO la recalcula.
/// - GDD y rendimiento se toman de GddService / YieldService (propietario:
///   forecasting) o de las predicciones persistidas por forecasting; no hay
///   fórmulas paralelas en Reports.
/// </summary>
public class ReportQueryService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly GddService _gddService;
    private readonly YieldService _yieldService;
    private readonly LotAggregateService _lotAggregate;

    /// <summary>
    /// Traducción SQL de <see cref="TelemetryQualityPolicy.IsOperationallyUsable"/>.
    /// La política sigue siendo la única fuente de la regla; este predicado la
    /// refleja para que EF Core la traduzca a SQL (los métodos estáticos no se
    /// traducen de forma garantizada). Se usa también post-consulta con
    /// <see cref="TelemetryQualityPolicy.IsOperationallyUsable"/>.
    /// </summary>
    private readonly Expression<Func<SensorReading, bool>> _usableReading =
        r => r.Quality == TelemetryContract.QualityValid
          || r.Quality == TelemetryContract.QualitySuspect
          || r.Quality == TelemetryContract.QualityStale;

    public ReportQueryService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        GddService gddService,
        YieldService yieldService,
        LotAggregateService lotAggregate)
    {
        _dbFactory = dbFactory;
        _gddService = gddService;
        _yieldService = yieldService;
        _lotAggregate = lotAggregate;
    }

    // ------------------------------------------------------------------
    // Opciones de filtros (catálogos para la UI)
    // ------------------------------------------------------------------

    public async Task<ReportFilterOptions> GetFilterOptionsAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var lots = await context.Lots
            .Include(l => l.CropType)
            .Include(l => l.Status)
            .OrderByDescending(l => l.SowingDate)
            .Select(l => new LotOptionDto(
                l.Id,
                $"#{l.Id} — {l.CropType!.Name} ({l.Status!.Name}, siembra {l.SowingDate.ToString("dd/MM/yyyy")})"))
            .ToListAsync(ct);

        var nodes = await context.IotNodes
            .OrderBy(n => n.Identifier)
            .Select(n => new NodeOptionDto(n.Id, n.Identifier))
            .ToListAsync(ct);

        var sensors = await context.Sensors
            .Include(s => s.SensorType)
            .OrderBy(s => s.NodeId).ThenBy(s => s.Name)
            .Select(s => new SensorOptionDto(s.Id, s.Name, s.NodeId, s.SensorType!.Name))
            .ToListAsync(ct);

        var sensorTypes = await context.SensorTypes
            .OrderBy(t => t.Name)
            .Select(t => new SensorTypeOptionDto(t.Id, t.Name))
            .ToListAsync(ct);

        var stages = await context.CommercialStages
            .OrderBy(s => s.Name)
            .Select(s => new CommercialStageOptionDto(s.Id, s.Name))
            .ToListAsync(ct);

        return new ReportFilterOptions(lots, nodes, sensors, sensorTypes, stages);
    }

    // ------------------------------------------------------------------
    // REP-02/03: Reporte de telemetría
    // ------------------------------------------------------------------

    public async Task<TelemetryReportResult> GetTelemetryReportAsync(
        TelemetryReportRequest request,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var warnings = new List<string>();

        // --- Rango obligatorio y truncado con advertencia (REP-03) ---
        var (from, to, rangeDays, rangeWarning) = ReportRangePolicy.Normalize(request.FromUtc, request.ToUtc);
        if (rangeWarning is not null)
            warnings.Add(rangeWarning);

        // --- Filtros base (rango + lote/nodo/sensor/tipo) ---
        IQueryable<SensorReading> baseQuery = context.SensorReadings
            .AsNoTracking()
            .Where(r => r.ObservedAtUtc >= from && r.ObservedAtUtc <= to);

        if (request.LotId is int lotId)
            baseQuery = baseQuery.Where(r => r.LotId == lotId);
        if (request.NodeId is int nodeId)
            baseQuery = baseQuery.Where(r => r.NodeId == nodeId);
        if (request.SensorId is int sensorId)
            baseQuery = baseQuery.Where(r => r.SensorId == sensorId);
        if (request.SensorTypeId is int sensorTypeId)
            baseQuery = baseQuery.Where(r => r.Sensor!.SensorTypeId == sensorTypeId);

        // Promedios/min/max/mediana solo sobre lecturas operacionalmente utilizables.
        var usableQuery = baseQuery.Where(_usableReading);

        // --- Estadísticas por sensor (agregación en servidor) ---
        var statsRows = await usableQuery
            .GroupBy(r => r.SensorId)
            .Select(g => new
            {
                SensorId = g.Key,
                Min = g.Min(r => r.Value),
                Max = g.Max(r => r.Value),
                Avg = g.Average(r => r.Value),
                UsableCount = g.Count(),
                First = g.Min(r => r.ObservedAtUtc),
                Last = g.Max(r => r.ObservedAtUtc),
            })
            .ToListAsync(ct);

        // Días con datos por sensor (agrupación por (sensor, día) en servidor).
        var dayRows = await usableQuery
            .GroupBy(r => new { r.SensorId, Day = r.ObservedAtUtc.Date })
            .Select(g => new { g.Key.SensorId, g.Key.Day })
            .ToListAsync(ct);
        var daysBySensor = dayRows
            .GroupBy(d => d.SensorId)
            .ToDictionary(g => g.Key, g => g.Select(d => d.Day).Distinct().Count());

        // Conteos por calidad (todas las lecturas del rango, no solo utilizables).
        var qualityRows = await baseQuery
            .GroupBy(r => new { r.SensorId, r.Quality })
            .Select(g => new { g.Key.SensorId, g.Key.Quality, Count = g.Count() })
            .ToListAsync(ct);

        // Última lectura por sensor (mayor ObservedAtUtc entre utilizables).
        var lastBySensor = await usableQuery
            .GroupBy(r => r.SensorId)
            .Select(g => new { SensorId = g.Key, LastAt = g.Max(r => r.ObservedAtUtc) })
            .ToListAsync(ct);
        var lastValueBySensor = new Dictionary<int, (decimal Value, string Quality)>();
        foreach (var lb in lastBySensor)
        {
            var row = await usableQuery
                .Where(r => r.SensorId == lb.SensorId && r.ObservedAtUtc == lb.LastAt)
                .Select(r => new { r.Value, r.Quality })
                .FirstOrDefaultAsync(ct);
            if (row is not null)
                lastValueBySensor[lb.SensorId] = (row.Value, row.Quality);
        }

        var sensorIds = statsRows.Select(s => s.SensorId).ToList();
        var sensorMeta = sensorIds.Count == 0
            ? []
            : await context.Sensors.AsNoTracking()
                .Where(s => sensorIds.Contains(s.Id))
                .Select(s => new
                {
                    s.Id,
                    s.Name,
                    s.NodeId,
                    NodeIdentifier = s.Node!.Identifier,
                    TypeName = s.SensorType!.Name,
                    Unit = s.MeasurementUnit != null ? s.MeasurementUnit.Symbol : null,
                })
                .ToListAsync(ct);
        var metaById = sensorMeta.ToDictionary(m => m.Id);

        // --- Mediana "si el volumen lo permite" (REP-02) ---
        var totalUsable = statsRows.Sum(s => s.UsableCount);
        Dictionary<int, decimal>? medians = null;
        if (totalUsable > ReportLimits.MedianSampleCap)
        {
            warnings.Add(
                $"Volumen alto ({totalUsable:N0} lecturas utilizables > {ReportLimits.MedianSampleCap:N0}): la mediana por sensor no se calcula.");
        }
        else if (totalUsable > 0)
        {
            var values = await usableQuery
                .Select(r => new { r.SensorId, r.Value })
                .ToListAsync(ct);
            medians = values
                .GroupBy(v => v.SensorId)
                .ToDictionary(g => g.Key, g => Median(g.Select(v => v.Value).OrderBy(x => x).ToList()));
        }

        var stats = statsRows
            .OrderBy(s => s.SensorId)
            .Select(s =>
            {
                var meta = metaById.GetValueOrDefault(s.SensorId);
                var qualityCounts = qualityRows
                    .Where(q => q.SensorId == s.SensorId)
                    .OrderBy(q => q.Quality)
                    .Select(q => new QualityCountDto(q.Quality, q.Count))
                    .ToList();
                var suspectCount = qualityCounts
                    .Where(q => q.Quality == TelemetryContract.QualitySuspect)
                    .Sum(q => q.Count);
                var invalidCount = qualityCounts
                    .Where(q => q.Quality is TelemetryContract.QualityInvalid
                        or TelemetryContract.QualityFuture
                        or TelemetryContract.QualityNoData
                        or TelemetryContract.QualitySensorError)
                    .Sum(q => q.Count);
                var daysWithData = daysBySensor.GetValueOrDefault(s.SensorId);
                var coverage = rangeDays > 0
                    ? Math.Round(daysWithData * 100m / rangeDays, 1)
                    : 0m;
                var last = lastValueBySensor.GetValueOrDefault(s.SensorId);

                return new TelemetrySensorStatsDto(
                    s.SensorId,
                    meta?.Name ?? $"Sensor #{s.SensorId}",
                    meta?.TypeName ?? "?",
                    meta?.Unit,
                    meta?.NodeId ?? 0,
                    meta?.NodeIdentifier ?? "?",
                    s.Min,
                    s.Max,
                    Math.Round(s.Avg, 4),
                    medians is not null && medians.TryGetValue(s.SensorId, out var m) ? Math.Round(m, 4) : null,
                    MedianComputed: medians is not null,
                    s.UsableCount,
                    suspectCount,
                    invalidCount,
                    daysWithData,
                    coverage,
                    s.First,
                    s.Last,
                    last != default ? last.Value : null,
                    last != default ? last.Quality : null,
                    qualityCounts);
            })
            .ToList();

        // --- Conteos globales / datos mock visibles ---
        var usableTotal = totalUsable;
        var allTotal = await baseQuery.CountAsync(ct);
        var mockCount = await baseQuery.CountAsync(
            r => r.ExternalReadingId.StartsWith("mock-")
              || r.ExternalReadingId.StartsWith("fixture-")
              || r.ExternalReadingId.StartsWith("seed-"), ct);
        if (mockCount > 0)
            warnings.Add(
                $"Datos de fixture/mock en el rango: {mockCount} lecturas con ExternalReadingId de siembra demo (mock-/fixture-/seed-). Se muestran tal cual persistieron.");

        // --- Detalle paginado (misma proyección que GET /api/telemetria/lecturas) ---
        var detailQuery = request.IncludeNonUsable ? baseQuery : usableQuery;
        var totalRows = await detailQuery.CountAsync(ct);
        var safePage = Math.Max(1, page);
        var safeSize = Math.Clamp(pageSize, 1, ReportLimits.MaxPageSize);

        var rows = await detailQuery
            .OrderByDescending(r => r.ObservedAtUtc)
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .Select(r => new TelemetryReadingRowDto(
                r.Id,
                r.ObservedAtUtc,
                r.Node!.Identifier,
                r.Sensor!.Name,
                r.Sensor!.SensorType!.Name,
                r.Value,
                r.Sensor!.MeasurementUnit != null ? r.Sensor.MeasurementUnit.Symbol : null,
                r.Quality,
                r.QualityReason,
                r.IngestionResult,
                r.LotId,
                r.Lot != null ? r.Lot.Name : null))
            .ToListAsync(ct);

        return new TelemetryReportResult(
            Parameters: BuildParameters([
                $"Rango: {from:yyyy-MM-dd HH:mm} → {to:yyyy-MM-dd HH:mm} UTC",
                request.LotId is int lid2 ? $"Lote: #{lid2}" : "Lote: todos",
                request.NodeId is int nid ? $"Nodo: #{nid}" : "Nodo: todos",
                request.SensorId is int sid ? $"Sensor: #{sid}" : "Sensor: todos",
                request.SensorTypeId is int tid ? $"Tipo de variable: #{tid}" : "Tipo de variable: todos",
                request.IncludeNonUsable ? "Calidad: incluye inválidas en el detalle" : "Calidad: solo operacionalmente utilizables",
            ]),
            RangeDays: rangeDays,
            TotalReadings: allTotal,
            UsableReadings: usableTotal,
            NonUsableReadings: allTotal - usableTotal,
            MockReadings: mockCount,
            Stats: stats,
            TotalRows: totalRows,
            Rows: rows,
            Page: safePage,
            PageSize: safeSize,
            IncludeNonUsable: request.IncludeNonUsable,
            Warnings: warnings);
    }

    // ------------------------------------------------------------------
    // REP-02: Reporte de ciclo de cultivo
    // ------------------------------------------------------------------

    public async Task<CycleReportResult> GetCycleReportAsync(
        CycleReportRequest request,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var warnings = new List<string>();
        var asOf = request.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

        IQueryable<Lot> lotsQuery = context.Lots
            .AsNoTracking()
            .Include(l => l.CropType)
            .Include(l => l.Status)
            .AsQueryable();
        if (request.LotId is int lotFilter)
            lotsQuery = lotsQuery.Where(l => l.Id == lotFilter);

        var lots = await lotsQuery.OrderBy(l => l.SowingDate).ToListAsync(ct);
        if (lots.Count == 0)
        {
            return new CycleReportResult(
                Parameters: BuildParameters(["Lote: todos", $"Fecha de corte: {asOf:yyyy-MM-dd}"]),
                Rows: [],
                Warnings: ["No hay lotes registrados para el reporte de ciclo."]);
        }

        var lotIds = lots.Select(l => l.Id).ToList();
        var predictions = await context.Predictions.AsNoTracking()
            .Where(p => lotIds.Contains(p.LotId))
            .OrderByDescending(p => p.GeneratedAt)
            .ToListAsync(ct);
        var latestByLot = predictions
            .GroupBy(p => p.LotId)
            .ToDictionary(g => g.Key, g => g.First());

        var rows = new List<CycleReportRowDto>(lots.Count);
        foreach (var lot in lots)
        {
            var rowWarnings = new List<string>();
            var prediction = latestByLot.GetValueOrDefault(lot.Id);

            // GDD acumulado: snapshot persistido o GddService (fuente canónica de forecasting).
            var gdd = lot.AccumulatedGdd
                      ?? await _gddService.GetAccumulatedGddAsync(lot, asOf, ct);

            // Fecha estimada de cosecha: última predicción persistida; si no existe
            // se calcula con la misma lógica que forecasting (GddService).
            DateOnly? estimatedHarvest;
            string harvestSource;
            if (prediction?.EstimatedHarvestDate is { } predDate)
            {
                estimatedHarvest = predDate;
                harvestSource = $"predicción {prediction.GeneratedAt:yyyy-MM-dd HH:mm} UTC (modelo {prediction.ModelVersion})";
            }
            else
            {
                var projection = await _gddService.GetFutureGddProjectionAsync(lot, asOf, ct: ct);
                estimatedHarvest = _gddService.EstimateHarvestDateAsync(lot, gdd, projection.Points, asOf);
                harvestSource = "GddService (proyección actual)";
            }

            // Rendimiento estimado: valor persistido por forecasting o YieldService.
            decimal? estimatedYield = prediction?.EstimatedYield;
            if (estimatedYield is null)
                estimatedYield = (await _yieldService.EstimateAsync(lot, gdd, ct)).Base;

            // Errores vs cosecha real (misma métrica que forecasting: MAPE kg/días).
            int? daysError = null;
            decimal? yieldErrorPercent = null;
            if (lot.ActualHarvestDate is { } actualHarvest && prediction is not null
                && prediction.EstimatedHarvestDate is { } predHarvest
                && DateOnly.FromDateTime(prediction.GeneratedAt) <= actualHarvest)
            {
                daysError = Math.Abs((predHarvest.ToDateTime(TimeOnly.MinValue)
                                      - actualHarvest.ToDateTime(TimeOnly.MinValue)).Days);
            }
            if (prediction?.EstimatedYield is { } estYield && estYield > 0
                && lot.ActualYieldKg is { } realYield && realYield > 0)
            {
                yieldErrorPercent = Math.Round(
                    Math.Abs(estYield - realYield) / realYield * 100m, 1);
            }

            // Cantidad y calidad de datos usados: lecturas de temperatura del
            // invernadero desde siembra hasta cosecha (o corte).
            var startUtc = lot.SowingDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var endDate = lot.ActualHarvestDate is { } hd && hd < asOf ? hd : asOf;
            var endUtc = endDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

            var tempQuery = context.SensorReadings.AsNoTracking()
                .Where(r => r.Sensor!.SensorType!.Name == "Temperatura")
                .Where(r => r.Sensor!.Node!.GreenhouseId == lot.GreenhouseId)
                .Where(r => r.ObservedAtUtc >= startUtc && r.ObservedAtUtc < endUtc);

            var tempTotal = await tempQuery.CountAsync(ct);
            var tempUsable = await tempQuery.Where(_usableReading).CountAsync(ct);

            if (gdd == 0 && !lot.AccumulatedGdd.HasValue)
                rowWarnings.Add("GDD 0: no hay lecturas de temperatura utilizables en el ciclo.");
            if (prediction is null)
                rowWarnings.Add("Sin predicción persistida (GET /api/forecasting no corrió para este lote): GDD/rendimiento calculados al corte con GddService/YieldService.");

            rows.Add(new CycleReportRowDto(
                lot.Id,
                lot.Name,
                lot.CropType?.Name ?? "Desconocido",
                lot.Status?.Name,
                lot.SowingDate,
                lot.PlantedAreaM2,
                Math.Round(gdd, 2),
                lot.CropType?.GddTarget ?? 0m,
                estimatedHarvest,
                harvestSource,
                lot.ActualHarvestDate,
                estimatedYield is null ? null : Math.Round(estimatedYield.Value, 2),
                lot.ActualYieldKg,
                daysError,
                yieldErrorPercent,
                prediction is not null,
                prediction?.GeneratedAt,
                prediction?.ModelVersion,
                tempTotal,
                tempUsable,
                rowWarnings));
        }

        if (rows.All(r => !r.HasPrediction))
            warnings.Add(
                "Ningún lote tiene predicción persistida: los errores forecast-vs-cosecha quedan sin calcular hasta que corra forecasting.");

        return new CycleReportResult(
            Parameters: BuildParameters([
                request.LotId is int lid ? $"Lote: #{lid}" : "Lote: todos",
                $"Fecha de corte: {asOf:yyyy-MM-dd}",
                "GDD/rendimiento: GddService/YieldService (fuente forecasting) o predicción persistida",
            ]),
            Rows: rows,
            Warnings: warnings);
    }

    // ------------------------------------------------------------------
    // REP-02: Forecast vs cosecha real (misma métrica que forecasting)
    // ------------------------------------------------------------------

    public async Task<ForecastVsHarvestResult> GetForecastVsHarvestAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var warnings = new List<string>();

        // Predicción más reciente por lote con cosecha real registrada. Misma
        // comparación que ForecastingController.GetModelAccuracyAsync: MAPE del
        // rendimiento sobre la predicción más reciente; error de días solo para
        // predicciones generadas antes de la cosecha real.
        var closed = await context.Lots.AsNoTracking()
            .Include(l => l.CropType)
            .Where(l => l.ActualYieldKg.HasValue || l.ActualHarvestDate.HasValue)
            .Select(l => new
            {
                l.Id,
                l.Name,
                CropTypeName = l.CropType!.Name,
                l.SowingDate,
                l.ActualHarvestDate,
                l.ActualYieldKg,
            })
            .ToListAsync(ct);

        var rows = new List<ForecastVsHarvestRowDto>();
        var mapeValues = new List<decimal>();
        var dayErrors = new List<decimal>();

        if (closed.Count > 0)
        {
            var closedIds = closed.Select(c => c.Id).ToList();
            var predictions = await context.Predictions.AsNoTracking()
                .Where(p => closedIds.Contains(p.LotId))
                .OrderByDescending(p => p.GeneratedAt)
                .ToListAsync(ct);
            var latestByLot = predictions
                .GroupBy(p => p.LotId)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var lot in closed)
            {
                if (!latestByLot.TryGetValue(lot.Id, out var pred))
                    continue;

                decimal? yieldError = null;
                if (pred.EstimatedYield is { } est && est > 0
                    && lot.ActualYieldKg is { } real && real > 0)
                {
                    yieldError = Math.Round(Math.Abs(est - real) / real * 100m, 1);
                    mapeValues.Add(yieldError.Value);
                }

                int? daysError = null;
                var daysComputed = false;
                if (pred.EstimatedHarvestDate is { } predDate && lot.ActualHarvestDate is { } realDate
                    && DateOnly.FromDateTime(pred.GeneratedAt) <= realDate)
                {
                    daysError = Math.Abs((predDate.ToDateTime(TimeOnly.MinValue)
                                          - realDate.ToDateTime(TimeOnly.MinValue)).Days);
                    dayErrors.Add(daysError.Value);
                    daysComputed = true;
                }

                rows.Add(new ForecastVsHarvestRowDto(
                    lot.Id,
                    lot.Name,
                    lot.CropTypeName,
                    lot.SowingDate,
                    lot.ActualHarvestDate,
                    lot.ActualYieldKg,
                    pred.EstimatedHarvestDate,
                    daysError,
                    pred.EstimatedYield,
                    yieldError,
                    pred.GeneratedAt,
                    pred.ModelVersion,
                    daysComputed));
            }
        }

        if (rows.Count == 0)
            warnings.Add(
                "Sin ciclos cerrados: no hay lotes con cosecha real y predicción persistida. El resumen de precisión queda vacío (no se muestran ceros engañosos).");

        var summary = new ForecastVsHarvestSummaryDto(
            Cycles: rows.Count,
            CyclesWithYield: mapeValues.Count,
            CyclesWithDays: dayErrors.Count,
            AverageMapePercent: mapeValues.Count > 0 ? Math.Round(mapeValues.Average(), 1) : null,
            AverageDaysError: dayErrors.Count > 0 ? Math.Round(dayErrors.Average(), 1) : null);

        warnings.Add(
            "Precisión calculada sobre las predicciones persistidas por forecasting (misma métrica MAPE/días del módulo). Cuando forecasting mergee un contrato público de precisión (ForecastResult), este reporte consumirá ese contrato.");

        return new ForecastVsHarvestResult(
            Parameters: BuildParameters([
                "Comparación: predicción más reciente vs cosecha real por lote",
                "Métrica: MAPE rendimiento (%) y error absoluto en días",
            ]),
            Rows: rows,
            Summary: summary,
            Warnings: warnings);
    }

    // ------------------------------------------------------------------
    // REP-06: Estado de lote (consume el dominio, no recalcula)
    // ------------------------------------------------------------------

    public async Task<LotStateReportResult> GetLotStateReportAsync(
        int lotId,
        DateOnly? asOfDate = null,
        CancellationToken ct = default)
    {
        var state = await _lotAggregate.GetLotStateAsync(lotId, asOfDate, ct);

        var warnings = new List<string>();
        if (state is null)
        {
            warnings.Add($"Lote #{lotId} no encontrado.");
        }
        else
        {
            // Datos demo etiquetados (plan 01: los datos mock se muestran como tales).
            if (state.Name == "Lote Demo 01")
                warnings.Add("Este lote proviene del seed demo de datos: los valores son de demostración, no productivos.");
            if (state.Warnings.Count > 0)
                warnings.AddRange(state.Warnings);
        }

        return new LotStateReportResult(
            Parameters: BuildParameters([
                $"Lote: #{lotId}",
                asOfDate is null ? "Fecha: hoy" : $"Fecha: {asOfDate.Value:yyyy-MM-dd}",
                "Predominancia: dominio (LotAggregateService/Predominance, Mixto ante empate)",
            ]),
            State: state,
            Warnings: warnings);
    }

    // ------------------------------------------------------------------
    // REP-07: Reporte de plantas
    // ------------------------------------------------------------------

    public async Task<PlantReportResult> GetPlantReportAsync(
        PlantReportRequest request,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var warnings = new List<string>();

        IQueryable<Plant> query = context.Plants.AsNoTracking();

        if (request.LotId is int lotId)
            query = query.Where(p => p.LotId == lotId);
        if (request.Row is int row)
            query = query.Where(p => p.Row == row);
        if (request.Column is int column)
            query = query.Where(p => p.Column == column);
        if (request.OperationalState is { } op)
            query = query.Where(p => p.OperationalState == op);
        if (request.CommercialStageId is int stageId)
            query = query.Where(p => p.CommercialStageId == stageId);

        var totalRows = await query.CountAsync(ct);
        var safePage = Math.Max(1, page);
        var safeSize = Math.Clamp(pageSize, 1, ReportLimits.MaxPageSize);

        var projected = await query
            .OrderBy(p => p.LotId).ThenBy(p => p.Row).ThenBy(p => p.Column)
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .Select(p => new PlantReportRowDto(
                p.Id,
                p.LotId,
                p.Lot != null ? p.Lot.Name : null,
                p.Row,
                p.Column,
                p.CommercialStage != null ? p.CommercialStage.Name : null,
                p.CommercialStageId,
                p.OperationalState,
                p.PhenologicalStage != null ? p.PhenologicalStage.Name : null,
                p.Evaluations.OrderByDescending(e => e.EvaluatedAtUtc).Select(e => (decimal?)e.BabyLeafScore).FirstOrDefault(),
                p.Evaluations.OrderByDescending(e => e.EvaluatedAtUtc).Select(e => (decimal?)e.GrowthRateUsed).FirstOrDefault(),
                p.Evaluations.OrderByDescending(e => e.EvaluatedAtUtc).Select(e => e.Result).FirstOrDefault(),
                p.Evaluations.OrderByDescending(e => e.EvaluatedAtUtc).Select(e => (DateTime?)e.EvaluatedAtUtc).FirstOrDefault(),
                p.Images.Count > 0,
                p.HarvestDate,
                p.DiscardDate,
                p.DiscardReason))
            .ToListAsync(ct);

        if (totalRows == 0)
            warnings.Add("No hay plantas que coincidan con los filtros.");

        var mockRows = projected.Count(p =>
            p.DiscardReason is not null && p.DiscardReason.Contains("Demo:", StringComparison.OrdinalIgnoreCase));
        if (mockRows > 0)
            warnings.Add($"{mockRows} planta(s) de este lote provienen del seed demo (motivos 'Demo:'): valores de demostración.");

        return new PlantReportResult(
            Parameters: BuildParameters([
                request.LotId is int lid ? $"Lote: #{lid}" : "Lote: todos",
                request.Row is int r ? $"Fila: {r}" : "Fila: todas",
                request.Column is int c ? $"Columna: {c}" : "Columna: todas",
                request.OperationalState is { } opState
                    ? $"Estado operativo: {opState}"
                    : "Estado operativo: todos",
                request.CommercialStageId is int sid
                    ? $"Etapa comercial: #{sid}"
                    : "Etapa comercial: todas",
            ]),
            TotalRows: totalRows,
            Rows: projected,
            Page: safePage,
            PageSize: safeSize,
            Warnings: warnings);
    }

    /// <summary>
    /// Detalle completo de una planta (REP-07): reutiliza el contrato de dominio
    /// PlantDetailDto (última evaluación, imagen, histórico de cosecha/descarte).
    /// </summary>
    public async Task<PlantDetailDto?> GetPlantDetailAsync(
        int plantId,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var plant = await context.Plants.AsNoTracking()
            .Where(p => p.Id == plantId)
            .Select(p => new
            {
                p.Id,
                p.LotId,
                LotGddSnapshot = p.Lot!.AccumulatedGdd,
                p.Lot!.CurrentPh,
                p.Lot!.CurrentEc,
            })
            .FirstOrDefaultAsync(ct);
        if (plant is null)
            return null;

        var lot = await context.Lots.AsNoTracking()
            .Include(l => l.CropType)
            .FirstOrDefaultAsync(l => l.Id == plant.LotId, ct);

        var lotGdd = lot is null
            ? plant.LotGddSnapshot ?? 0m
            : lot.AccumulatedGdd ?? await _gddService.GetAccumulatedGddAsync(lot, ct: ct);

        return await _lotAggregate.GetPlantDetailAsync(plantId, lotGdd, plant.CurrentPh, plant.CurrentEc, ct);
    }

    // ------------------------------------------------------------------
    // REP-08: Reporte de cambios de estado (PlantStageHistory)
    // ------------------------------------------------------------------

    public async Task<StageChangeReportResult> GetStageChangesAsync(
        int? lotId = null,
        string? source = null,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        IQueryable<PlantStageHistory> query = context.PlantStageHistories.AsNoTracking();
        if (lotId is int lid)
            query = query.Where(h => h.Plant!.LotId == lid);
        if (!string.IsNullOrWhiteSpace(source))
            query = query.Where(h => h.Source == source);

        var totalRows = await query.CountAsync(ct);
        if (totalRows == 0)
        {
            return new StageChangeReportResult(
                Parameters: BuildParameters([
                    lotId is int lidEmpty ? $"Lote: #{lidEmpty}" : "Lote: todos",
                    string.IsNullOrWhiteSpace(source) ? "Origen: todos" : $"Origen: {source}",
                ]),
                TotalRows: 0,
                Rows: [],
                IsTruncated: false,
                Warnings:
                [
                    "No hay historial de cambios de estado (PlantStageHistory vacío): el reporte queda PENDIENTE hasta que el flujo diario/cosecha/descarte registre transiciones.",
                ]);
        }

        var projected = await query
            .OrderByDescending(h => h.ChangedAtUtc)
            .Take(ReportLimits.MaxHistoryRows)
            .Select(h => new
            {
                h.Id,
                h.PlantId,
                h.ChangedAtUtc,
                h.Source,
                h.Reason,
                h.PreviousCommercialStageId,
                h.NewCommercialStageId,
                h.PreviousOperationalState,
                h.NewOperationalState,
                Row = h.Plant!.Row,
                Column = h.Plant!.Column,
                LotId = h.Plant!.LotId,
                LotName = h.Plant!.Lot != null ? h.Plant.Lot.Name : null,
            })
            .ToListAsync(ct);

        var isTruncated = totalRows > ReportLimits.MaxHistoryRows;
        var warnings = new List<string>();
        if (isTruncated)
            warnings.Add(
                $"El historial tiene {totalRows:N0} entradas; se muestran las últimas {ReportLimits.MaxHistoryRows:N0} (límite del reporte). Exportá para conservar el detalle.");

        var stageIds = projected
            .SelectMany(h => new[] { h.PreviousCommercialStageId, h.NewCommercialStageId })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var stageNames = stageIds.Count == 0
            ? new Dictionary<int, string>()
            : await context.CommercialStages.AsNoTracking()
                .Where(s => stageIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var rows = projected.Select(h => new StageChangeRowDto(
            h.Id,
            h.PlantId,
            h.LotId,
            h.LotName,
            h.Row,
            h.Column,
            h.PreviousCommercialStageId.HasValue
                ? stageNames.GetValueOrDefault(h.PreviousCommercialStageId.Value)
                : h.PreviousOperationalState,
            h.NewCommercialStageId.HasValue
                ? stageNames.GetValueOrDefault(h.NewCommercialStageId.Value)
                : h.NewOperationalState,
            h.PreviousOperationalState,
            h.NewOperationalState,
            h.Source,
            h.Reason,
            h.ChangedAtUtc)).ToList();

        var demoEntries = rows.Count(r => r.Reason is not null && r.Reason.Contains("Dato demo", StringComparison.OrdinalIgnoreCase));
        if (demoEntries > 0)
            warnings.Add($"El historial incluye {demoEntries} entradas del seed demo (Reason 'Dato demo'): se muestran tal cual, etiquetadas como demo.");

        return new StageChangeReportResult(
            Parameters: BuildParameters([
                lotId is int lid3 ? $"Lote: #{lid3}" : "Lote: todos",
                string.IsNullOrWhiteSpace(source) ? "Origen: todos" : $"Origen: {source}",
            ]),
            TotalRows: totalRows,
            Rows: rows,
            IsTruncated: isTruncated,
            Warnings: warnings);
    }

    // ------------------------------------------------------------------
    // REP-01: Anomalías (pendiente por diseño; no se inventan datos)
    // ------------------------------------------------------------------

    public Task<AnomalyReportResult> GetAnomalyReportAsync(CancellationToken ct = default)
    {
        var design = new AnomalyReportDto(
            Type: "tipo (regla que la produjo)",
            Severity: "severidad",
            EpisodeCount: 0,
            AverageDuration: null,
            Status: "estado del episodio",
            LotId: null,
            LotName: "lote",
            SensorId: null,
            SensorName: "sensor",
            AverageTimeToResolution: null);

        return Task.FromResult(new AnomalyReportResult(
            IsPending: true,
            Parameters: BuildParameters(["Estado: pendiente — el módulo de anomalías aún no existe en dev"]),
            Rows: [design],
            Warnings:
            [
                "El reporte de anomalías se declara PENDIENTE (REP-01): el diseño de datos está definido (tipo, severidad, cantidad de episodios, duración, estado, lote/sensor, tiempo a resolución) pero NO se inventan datos hasta que el módulo de anomalías provea su contrato.",
            ]));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static ReportAppliedParameters BuildParameters(IReadOnlyList<string> lines) =>
        new(GeneratedAtUtc: DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"), Lines: lines);

    private static decimal Median(IReadOnlyList<decimal> sorted)
    {
        var n = sorted.Count;
        if (n == 0) return 0m;
        if (n % 2 == 1) return sorted[n / 2];
        return (sorted[n / 2 - 1] + sorted[n / 2]) / 2m;
    }
}