using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Services.Dashboard;

/// <summary>
/// Servicio de consulta del dashboard (plan 12, DASH-02). Única vía por la que la
/// vista accede a datos: consultas <c>AsNoTracking</c> con proyecciones acotadas;
/// los componentes NO tocan EF directamente.
///
/// - KPIs: última lectura operativamente usable por tipo de sensor (calidad
///   VALID/SUSPECT/STALE según TelemetryQualityPolicy), fresco/desactualizado
///   por antigüedad, delta real contra la lectura anterior del mismo tipo.
/// - Nodos: estado de conexión mantenido por NodeConnectionMonitorHostedService
///   (frescura por última aceptación; nunca se usa LastConnection como sinónimo).
/// - Serie: agregación por bucket en SQL (máx. <see cref="DashboardOptions.SeriesMaxPoints"/>
///   puntos), sin descargar el histórico completo.
/// - Anomalías/recomendaciones: sin fuente integrada → listas vacías honestas
///   (el dashboard no inventa datos).
///
/// Contexto: invernadero único (primer registro) — asunción de una instalación
/// por despliegue; el plan 12 no define selección multi-invernadero.
/// </summary>
public sealed class DashboardService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly IOptionsMonitor<DashboardOptions> _options;
    private readonly ILogger<DashboardService> _logger;

    private DashboardContextDto? _contextCache;

    /// <summary>Último snapshot válido generado en este circuito (para la barra superior y los reintentos).</summary>
    public DashboardSnapshot? LastSnapshot { get; private set; }

    /// <summary>Se dispara cuando un snapshot nuevo quedó disponible (la barra superior se actualiza sola).</summary>
    public event Action? DataChanged;

    public DashboardService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        IOptionsMonitor<DashboardOptions> options,
        ILogger<DashboardService> logger)
    {
        _dbFactory = dbFactory;
        _options = options;
        _logger = logger;
    }

    public DashboardOptions CurrentOptions => _options.CurrentValue;

    /// <summary>
    /// Snapshot completo: contexto, KPIs, nodos, anomalías/recomendaciones (vacías
    /// sin fuente) y la serie temporal de la variable y rango seleccionados.
    /// </summary>
    public async Task<DashboardSnapshot> GetSnapshotAsync(
        string variableKey,
        DashboardTimeRange range,
        CancellationToken ct = default)
    {
        var contextDto = await GetContextAsync(ct);
        var nowUtc = DateTime.UtcNow;

        var kpis = await GetKpisAsync(contextDto.GreenhouseId, nowUtc, ct);
        var nodes = await GetNodesAsync(contextDto.GreenhouseId, nowUtc, ct);
        var series = await GetSeriesAsync(contextDto.GreenhouseId, variableKey, range, ct);

        var snapshot = new DashboardSnapshot(
            contextDto,
            kpis,
            nodes,
            // Anomalías y recomendaciones: los módulos aún no están integrados.
            // El contrato queda listo; la lista vacía es el estado honesto (DASH-03).
            [],
            [],
            series,
            DateTime.UtcNow);

        LastSnapshot = snapshot;
        DataChanged?.Invoke();
        return snapshot;
    }

    /// <summary>Contexto del invernadero y lote activo (cacheado por circuito).</summary>
    public async Task<DashboardContextDto> GetContextAsync(CancellationToken ct = default)
    {
        if (_contextCache is not null)
            return _contextCache;

        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var greenhouse = await context.Greenhouses
            .AsNoTracking()
            .OrderBy(g => g.Id)
            .Select(g => new { g.Id, g.Name, g.Location })
            .FirstOrDefaultAsync(ct);

        DashboardContextDto dto;
        if (greenhouse is null)
        {
            dto = new DashboardContextDto(null, "Sin invernadero configurado", null, null, null, null);
        }
        else
        {
            var activeLot = await context.Lots
                .AsNoTracking()
                .Where(l => l.GreenhouseId == greenhouse.Id && l.Status!.Name == "ACTIVO")
                .OrderByDescending(l => l.SowingDate)
                .Select(l => new { l.Id, l.Name, Crop = l.CropType!.Name })
                .FirstOrDefaultAsync(ct);

            dto = new DashboardContextDto(
                greenhouse.Id,
                greenhouse.Name,
                greenhouse.Location,
                activeLot?.Id,
                activeLot?.Name,
                activeLot?.Crop);
        }

        _contextCache = dto;
        return dto;
    }

    /// <summary>
    /// Estado resumido para la barra superior: contexto real del invernadero y
    /// estado global DERIVADO de los nodos (último snapshot si existe; si el usuario
    /// todavía no cargó el dashboard, consulta los nodos una vez). Nunca texto fijo.
    /// </summary>
    public async Task<DashboardHeaderStatus> GetHeaderStatusAsync(CancellationToken ct = default)
    {
        var context = await GetContextAsync(ct);
        var nodes = LastSnapshot?.Nodes ?? await GetNodesAsync(context.GreenhouseId, DateTime.UtcNow, ct);
        return BuildHeaderStatus(context, nodes);
    }

    /// <summary>Versión síncrona para el evento DataChanged (usa el último snapshot).</summary>
    public DashboardHeaderStatus GetHeaderStatus() =>
        BuildHeaderStatus(
            _contextCache ?? new DashboardContextDto(null, "Sin invernadero configurado", null, null, null, null),
            LastSnapshot?.Nodes);

    private static DashboardHeaderStatus BuildHeaderStatus(DashboardContextDto context, IReadOnlyList<DashboardNodeDto>? nodes)
    {
        var subtitle = context.GreenhouseId is null
            ? "Sin invernadero configurado"
            : context.ActiveLotCrop is not null
                ? $"{context.GreenhouseName} · {context.ActiveLotCrop}"
                : $"{context.GreenhouseName} · sin lote activo";

        if (nodes is null || nodes.Count == 0)
            return new DashboardHeaderStatus(context.GreenhouseName, subtitle, "Sin nodos registrados", "status-pill--muted");

        var hasOffline = nodes.Any(n => n.ConnectionState is TelemetryContract.ConnectionOffline or TelemetryContract.ConnectionNeverConnected);
        if (hasOffline)
            return new DashboardHeaderStatus(context.GreenhouseName, subtitle, "Atención requerida", "status-pill--warn");

        if (nodes.Any(n => n.ConnectionState == TelemetryContract.ConnectionDegraded))
            return new DashboardHeaderStatus(context.GreenhouseName, subtitle, "Nodos degradados", "status-pill--warn");

        return new DashboardHeaderStatus(context.GreenhouseName, subtitle, "Sistemas operativos", "status-pill--ok");
    }

    // ---------------------------------------------------------------------
    // KPIs
    // ---------------------------------------------------------------------

    private sealed record ReadingProjection(
        decimal Value,
        DateTime ObservedAtUtc,
        string Quality,
        string SensorName,
        string Unit,
        string NodeIdentifier);

    private async Task<IReadOnlyList<DashboardKpiDto>> GetKpisAsync(
        int? greenhouseId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        if (greenhouseId is not int ghId)
            return [];

        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var types = await context.SensorTypes
            .AsNoTracking()
            .OrderBy(t => t.Id)
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(ct);

        var orderedTypes = types
            .OrderBy(t => DashboardVariableKeys.DisplayOrder(DashboardVariableKeys.Normalize(t.Name)))
            .ThenBy(t => t.Id)
            .ToList();

        if (orderedTypes.Count == 0)
            return [];

        var window = TimeSpan.FromMinutes(Math.Max(_options.CurrentValue.KpiFreshnessMinutes, 1));
        var kpis = new List<DashboardKpiDto>(orderedTypes.Count);

        foreach (var type in orderedTypes)
        {
            // Solo lecturas operativamente usables (compartido por todos los módulos),
            // acotado a las 2 más recientes del tipo para calcular el delta real.
            var latest2 = await context.SensorReadings
                .AsNoTracking()
                .Where(r => r.Sensor!.SensorTypeId == type.Id)
                .Where(r => r.Sensor!.Node!.GreenhouseId == ghId)
                .Where(TelemetryQualityPolicy.OperationallyUsableReading)
                .OrderByDescending(r => r.ObservedAtUtc)
                .ThenByDescending(r => r.Id)
                .Take(2)
                .Select(r => new ReadingProjection(
                    r.Value,
                    r.ObservedAtUtc,
                    r.Quality,
                    r.Sensor!.Name,
                    r.Sensor!.MeasurementUnit!.Symbol ?? string.Empty,
                    r.Node!.Identifier))
                .ToListAsync(ct);

            var key = DashboardVariableKeys.Normalize(type.Name);
            var latest = latest2.Count > 0 ? latest2[0] : null;
            var previous = latest2.Count > 1 ? latest2[1] : null;

            kpis.Add(new DashboardKpiDto(
                key,
                type.Name,
                DashboardVariableKeys.IconFor(key),
                latest?.Value,
                latest?.Unit ?? string.Empty,
                latest?.ObservedAtUtc,
                latest?.NodeIdentifier,
                latest?.SensorName,
                latest?.Quality,
                DashboardFreshness.ForValue(latest?.ObservedAtUtc, nowUtc, window),
                previous?.Value,
                previous?.ObservedAtUtc,
                DashboardFreshness.DeltaPercent(latest?.Value, previous?.Value)));
        }

        return kpis;
    }

    // ---------------------------------------------------------------------
    // Nodos
    // ---------------------------------------------------------------------

    private async Task<IReadOnlyList<DashboardNodeDto>> GetNodesAsync(
        int? greenhouseId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        if (greenhouseId is not int ghId)
            return [];

        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var since = nowUtc.AddDays(-1);

        return await context.IotNodes
            .AsNoTracking()
            .Where(n => n.GreenhouseId == ghId)
            .OrderBy(n => n.Identifier)
            .Select(n => new DashboardNodeDto(
                n.Id,
                n.Identifier,
                // Estado mantenido por NodeConnectionMonitorHostedService
                // (freshness por última telemetría ACEPTADA; LastConnection no equivale).
                n.ConnectionState,
                n.Status,
                n.LastAcceptedAt,
                n.LastRejectedAt,
                n.ExpectedIntervalSeconds,
                n.Sensors.Count,
                n.Rejections.Count(r => r.ReceivedAtUtc >= since)))
            .ToListAsync(ct);
    }

    // ---------------------------------------------------------------------
    // Serie temporal
    // ---------------------------------------------------------------------

    private async Task<DashboardSeriesDto> GetSeriesAsync(
        int? greenhouseId,
        string variableKey,
        DashboardTimeRange range,
        CancellationToken ct)
    {
        if (greenhouseId is not int ghId)
            return EmptySeries(variableKey, range);

        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        // El catálogo de tipos es acotado: se resuelve en memoria porque la
        // normalización de claves (DashboardVariableKeys.Normalize) no es traducible a SQL.
        var types = await context.SensorTypes
            .AsNoTracking()
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(ct);

        var type = types.FirstOrDefault(t => DashboardVariableKeys.Normalize(t.Name) == variableKey);

        if (type is null)
            return EmptySeries(variableKey, range);

        var label = type.Name;
        var unit = await context.Sensors
            .AsNoTracking()
            .Where(s => s.SensorTypeId == type.Id && s.Node!.GreenhouseId == ghId && s.MeasurementUnitId != null)
            .Select(s => s.MeasurementUnit!.Symbol ?? string.Empty)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        var duration = range.ToDuration();
        var maxPoints = Math.Max(_options.CurrentValue.SeriesMaxPoints, 5);
        // Duración de cada bucket: el rango partido en a lo sumo maxPoints buckets.
        var bucketSeconds = DashboardSeriesAggregation.BucketSeconds(range, maxPoints);
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var since = DateTime.UtcNow - duration;
        // Ancla el grid de buckets al inicio del rango: sin ancla, la fase del
        // reloj puede hacer que un rango de maxPoints*bucketSeconds caiga en
        // maxPoints+1 buckets (test flaky detectado en la integración).
        var sinceEpochSeconds = (long)(since - epoch).TotalSeconds;

        // Agrupación en SQL por bucket (segundos desde epoch / bucketSeconds):
        // evita bajar todo el histórico y devuelve a lo sumo maxPoints puntos.
        var grouped = await context.SensorReadings
            .AsNoTracking()
            .Where(r => r.Sensor!.SensorTypeId == type.Id)
            .Where(r => r.Sensor!.Node!.GreenhouseId == ghId)
            .Where(r => r.ObservedAtUtc >= since)
            .Where(TelemetryQualityPolicy.OperationallyUsableReading)
            .GroupBy(r => (EF.Functions.DateDiffSecond(epoch, r.ObservedAtUtc) - sinceEpochSeconds) / bucketSeconds)
            .Select(g => new { Bucket = g.Key, Value = g.Average(r => r.Value), Count = g.Count() })
            .OrderBy(x => x.Bucket)
            .ToListAsync(ct);

        // Clasificación en cliente: la lectura exactamente en el borde del rango
        // puede caer en el bucket maxPoints; se fusiona en el último para
        // garantizar a lo sumo maxPoints puntos.
        var points = grouped
            .GroupBy(x => Math.Min(Math.Max(x.Bucket, 0), maxPoints - 1))
            .Select(g => new DashboardSeriesPointDto(
                epoch.AddSeconds(((long)g.Max(x => x.Bucket) + 1) * bucketSeconds),
                Math.Round(g.Average(x => x.Value), 2),
                g.Sum(x => x.Count)))
            .ToList();

        return new DashboardSeriesDto(variableKey, label, unit, range, points);
    }

    private static DashboardSeriesDto EmptySeries(string variableKey, DashboardTimeRange range) =>
        new(variableKey, string.Empty, string.Empty, range, []);
}