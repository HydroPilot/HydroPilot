using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Forecasting;

namespace HydroPilotWeb.Services;

/// <summary>
/// Calcula Grados Día de Desarrollo (GDD) para un lote (módulo forecasting).
///
/// Histórico (F-02): lecturas del sensor ambiental del invernadero, SOLO con
/// calidad operativamente usable (TelemetryQualityPolicy), agrupadas por la zona
/// horaria del invernadero; los días sin lectura se informan como faltantes y
/// NUNCA se convierten en cero silenciosamente.
///
/// Futuro (F-03): DailyWeatherForecast (DB primero, fetch perezoso serializado
/// contra el guard diario), respetando AsOfDate en toda consulta simulada.
///
/// Fórmula: GDD_diario = max(0, (min(Tmax,30) + Tmin)/2 - Tbase)
/// </summary>
public class GddService
{
    private const int ForecastHorizonDays = 7;

    /// <summary>Versión del cálculo (F-03: versionar el cálculo).</summary>
    public const string CalculationVersion = "gdd-v2";

    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly WeatherService _weatherService;
    private readonly SettingsService _settings;

    public GddService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        WeatherService weatherService,
        SettingsService settings)
    {
        _dbFactory = dbFactory;
        _weatherService = weatherService;
        _settings = settings;
    }

    public int HorizonDays => ForecastHorizonDays;

    public static decimal DailyGdd(decimal tmax, decimal tmin, decimal baseTemperature)
    {
        var cappedMax = Math.Min(tmax, 30m);
        return Math.Max(0m, (cappedMax + tmin) / 2m - baseTemperature);
    }

    /// <summary>
    /// Resuelve la zona horaria del invernadero (IANA). Null o inválida → UTC
    /// con advertencia (nunca se inventa un offset).
    /// </summary>
    public static TimeZoneInfo ResolveGreenhouseTimeZone(string? timeZoneId, IList<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        warnings.Add(
            timeZoneId is null
                ? "Zona horaria del invernadero no configurada: el GDD diario se agrupa en UTC."
                : $"Zona horaria '{timeZoneId}' del invernadero no válida: se agrupa en UTC.");
        return TimeZoneInfo.Utc;
    }

    /// <summary>
    /// Sensor(es) ambiental(es) de temperatura del invernadero del lote (F-02):
    /// se prefiere el sensor cuyo TechnicalKey/Name marca ambiente ("amb"),
    /// priorizando el asignado al lote por NodeLotAssignment; si no hay sensor
    /// ambiental explícito, se usan los sensores de temperatura activos con
    /// advertencia (nunca "cualquiera del primer invernadero").
    /// </summary>
    private static async Task<(List<int> SensorIds, List<int> NodeIds, string? Label, List<string> Warnings)>
        ResolveAmbientTemperatureSensorsAsync(HydroPilotDbContext context, Lot lot, CancellationToken ct)
    {
        var warnings = new List<string>();

        var sensors = await context.Sensors.AsNoTracking()
            .Include(s => s.SensorType)
            .Where(s => s.IsActive
                        && s.SensorType!.Name == "Temperatura"
                        && s.Node != null
                        && s.Node!.GreenhouseId == lot.GreenhouseId)
            .Select(s => new { s.Id, s.NodeId, s.Name, s.TechnicalKey })
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        if (sensors.Count == 0)
        {
            warnings.Add("Sin sensor de temperatura activo en el invernadero del lote: GDD observado no disponible (no se asume 0).");
            return ([], [], null, warnings);
        }

        var ambient = sensors
            .Where(s => s.Name.Contains("amb", StringComparison.OrdinalIgnoreCase)
                        || s.TechnicalKey.Contains("amb", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (ambient.Count == 0)
        {
            warnings.Add($"No se identificó un sensor ambiental explícito en el invernadero; se usan los {sensors.Count} sensores de temperatura activos.");
            return (sensors.Select(s => s.Id).ToList(), sensors.Select(s => s.NodeId).Distinct().ToList(), null, warnings);
        }

        // Varios ambientales: preferir el asignado al lote (asignación nodo→lote).
        if (ambient.Count > 1)
        {
            var nodeIds = ambient.Select(s => s.NodeId).Distinct().ToList();
            var assigned = await context.NodeLotAssignments.AsNoTracking()
                .Where(a => nodeIds.Contains(a.NodeId) && a.LotId == lot.Id)
                .Select(a => a.NodeId)
                .Distinct()
                .ToListAsync(ct);

            var preferred = ambient.Where(s => assigned.Contains(s.NodeId)).ToList();
            if (preferred.Count > 0)
                ambient = preferred;
        }

        var chosen = ambient[0];
        if (ambient.Count > 1)
        {
            warnings.Add($"Hay {ambient.Count} sensores ambientales posibles; se usa el primero ('{chosen.Name}').");
        }

        return ([chosen.Id], [chosen.NodeId], chosen.Name, warnings);
    }

    /// <summary>
    /// GDD diario por fecha con detalle de cobertura y advertencias (F-02).
    /// asOfDate simula "hoy": solo se consideran lecturas hasta el final de ese
    /// día (fecha simulada sin fuga de datos futuros).
    /// </summary>
    public async Task<GddHistoryResult> GetGddHistoryAsync(
        Lot lot,
        DateOnly? asOfDate = null,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var effectiveToday = asOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        if (effectiveToday < lot.SowingDate)
        {
            warnings.Add($"Fecha simulada ({effectiveToday}) anterior a la siembra ({lot.SowingDate}): sin período a calcular.");
            return new GddHistoryResult([], 0, 0, 0, null, warnings);
        }

        var periodDays = (effectiveToday.ToDateTime(TimeOnly.MinValue) - lot.SowingDate.ToDateTime(TimeOnly.MinValue)).Days + 1;
        var sowingStartUtc = lot.SowingDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var cutoffUtc = effectiveToday.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        if (asOfDate is { } simDate && simDate < DateOnly.FromDateTime(DateTime.UtcNow))
        {
            warnings.Add($"Consulta simulada al {simDate:yyyy-MM-dd}: no se consideran lecturas posteriores a esa fecha.");
        }

        var baseTemp = lot.CropType?.BaseTemperature ?? 4.5m;

        var (sensorIds, nodeIds, sensorLabel, sensorWarnings) =
            await ResolveAmbientTemperatureSensorsAsync(context, lot, ct);
        warnings.AddRange(sensorWarnings);
        if (sensorIds.Count == 0)
        {
            return new GddHistoryResult([], periodDays, 0, periodDays, 0m, warnings);
        }

        var readings = await context.SensorReadings.AsNoTracking()
            .Where(r => sensorIds.Contains(r.SensorId))
            .Where(r => r.ObservedAtUtc >= sowingStartUtc && r.ObservedAtUtc < cutoffUtc)
            .Where(r => r.Quality == TelemetryContract.QualityValid
                        || r.Quality == TelemetryContract.QualitySuspect
                        || r.Quality == TelemetryContract.QualityStale)
            .Select(r => new { r.ObservedAtUtc, r.Value, r.Quality, r.NodeId, r.ExternalReadingId })
            .ToListAsync(ct);

        var excludedByQuality = await context.SensorReadings.AsNoTracking()
            .Where(r => sensorIds.Contains(r.SensorId))
            .Where(r => r.ObservedAtUtc >= sowingStartUtc && r.ObservedAtUtc < cutoffUtc)
            .Where(r => r.Quality != TelemetryContract.QualityValid
                        && r.Quality != TelemetryContract.QualitySuspect
                        && r.Quality != TelemetryContract.QualityStale)
            .CountAsync(ct);

        if (excludedByQuality > 0)
        {
            warnings.Add($"Se descartaron {excludedByQuality} lecturas por calidad no usable (INVALID/FUTURE/NO_DATA/SENSOR_ERROR).");
        }

        // Respetar lote/sector cuando existe asociación nodo→lote: una lectura de un
        // nodo asignado a OTRO lote en ese momento no alimenta este lote.
        var assignments = await context.NodeLotAssignments.AsNoTracking()
            .Where(a => nodeIds.Contains(a.NodeId))
            .Where(a => a.ValidFromUtc < cutoffUtc
                        && (a.ValidUntilUtc == null || a.ValidUntilUtc > sowingStartUtc))
            .Select(a => new { a.NodeId, a.LotId, a.ValidFromUtc, a.ValidUntilUtc })
            .ToListAsync(ct);

        var excludedByAssignment = 0;
        var usable = new List<(DateTime ObservedAtUtc, decimal Value, string ReadingId)>();
        foreach (var r in readings)
        {
            var activeAssignments = assignments
                .Where(a => a.NodeId == r.NodeId
                            && a.ValidFromUtc <= r.ObservedAtUtc
                            && (a.ValidUntilUtc == null || a.ValidUntilUtc > r.ObservedAtUtc))
                .ToList();

            if (activeAssignments.Count > 0 && activeAssignments.All(a => a.LotId != lot.Id))
            {
                excludedByAssignment++;
                continue;
            }

            usable.Add((r.ObservedAtUtc, r.Value, r.ExternalReadingId));
        }

        if (excludedByAssignment > 0)
        {
            warnings.Add($"Se descartaron {excludedByAssignment} lecturas asociadas a otro lote por asignación nodo→lote.");
        }

        if (usable.Any(r => r.ReadingId.StartsWith("mock", StringComparison.OrdinalIgnoreCase)
                            || r.ReadingId.StartsWith("demo", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add("Se detectaron lecturas sintéticas (marker mock/demo) en el histórico: no representan precisión real.");
        }

        var timeZone = ResolveGreenhouseTimeZone(
            await context.Greenhouses.AsNoTracking()
                .Where(g => g.Id == lot.GreenhouseId)
                .Select(g => g.TimeZoneId)
                .FirstOrDefaultAsync(ct),
            warnings);

        var daily = usable
            .GroupBy(r => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(r.ObservedAtUtc, timeZone)))
            .Select(g => new DailyGddPoint(
                g.Key,
                DailyGdd(g.Max(r => r.Value), g.Min(r => r.Value), baseTemp),
                GddPointSource.Observed))
            .OrderBy(p => p.Date)
            .ToList();

        var coverageDays = daily.Count;
        var missingDays = Math.Max(0, periodDays - coverageDays);
        if (missingDays > 0)
        {
            warnings.Add($"Faltan {missingDays} día(s) del período sin lectura usable (cobertura {coverageDays}/{periodDays}).");
        }

        if (usable.Count == 0)
        {
            warnings.Add("Sin lecturas de temperatura operativamente usables en el período: el GDD observado queda sin dato (no se asume 0 como medición).");
        }

        var coveragePercent = periodDays > 0
            ? (decimal?)Math.Round(coverageDays * 100m / periodDays, 1)
            : null;

        return new GddHistoryResult(daily, periodDays, coverageDays, missingDays, coveragePercent, warnings);
    }

    /// <summary>
    /// GDD diario por fecha (compatibilidad): diccionario fecha→GDD observado.
    /// </summary>
    public async Task<Dictionary<DateOnly, decimal>> GetDailyGddByDateAsync(
        Lot lot,
        DateOnly? asOfDate = null,
        CancellationToken ct = default)
    {
        var history = await GetGddHistoryAsync(lot, asOfDate, ct);
        return history.Daily.ToDictionary(p => p.Date, p => p.Gdd);
    }

    /// <summary>GDD acumulado del lote desde la siembra (solo lecturas usables).</summary>
    public async Task<decimal> GetAccumulatedGddAsync(Lot lot, DateOnly? asOfDate = null, CancellationToken ct = default)
    {
        var history = await GetGddHistoryAsync(lot, asOfDate, ct);
        return history.Daily.Sum(p => p.Gdd);
    }

    /// <summary>
    /// GDD diario promedio de los últimos N días observados (fallback de proyección).
    /// null si no hay días observados (nunca se inventa un número).
    /// </summary>
    public async Task<decimal?> GetRecentAverageDailyGddOrNullAsync(
        Lot lot,
        DateOnly? asOfDate = null,
        int lastNDays = 7,
        CancellationToken ct = default)
    {
        var daily = await GetDailyGddByDateAsync(lot, asOfDate, ct);
        var effectiveToday = asOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var recent = daily
            .Where(kv => kv.Key >= effectiveToday.AddDays(-lastNDays))
            .Select(kv => kv.Value)
            .ToList();

        return recent.Count > 0 ? recent.Average() : (decimal?)null;
    }

    /// <summary>Compat: promedio de los últimos N días, 0 si no hay (uso legado).</summary>
    public async Task<decimal> GetRecentAverageDailyGddAsync(
        Lot lot,
        DateOnly? asOfDate = null,
        int lastNDays = 7,
        CancellationToken ct = default)
        => await GetRecentAverageDailyGddOrNullAsync(lot, asOfDate, lastNDays, ct) ?? 0m;

    /// <summary>
    /// Proyección diaria de GDD futuro (F-03): DB primero; si faltan fechas, fetch
    /// perezoso serializado contra el guard diario. Fechas sin dato → fallback con
    /// promedio del sensor, informado como tal (nunca cero silencioso).
    /// Respetar AsOfDate: no se usan lecturas posteriores a la fecha simulada.
    /// </summary>
    public async Task<FutureProjectionResult> GetFutureGddProjectionAsync(
        Lot lot,
        DateOnly? asOfDate = null,
        int? horizonDays = null,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var baseTemp = lot.CropType?.BaseTemperature ?? 4.5m;
        var today = asOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var horizon = horizonDays ?? ForecastHorizonDays;
        var from = today.AddDays(1);
        var to = today.AddDays(horizon);

        // 1. GDD real por fecha respetando AsOfDate (sin fuga de datos futuros).
        var realDaily = (await GetGddHistoryAsync(lot, asOfDate, ct)).Daily
            .ToDictionary(p => p.Date, p => p.Gdd);

        // 2. Fechas sin lectura real → pronóstico climático (DB primero, fetch serializado).
        var missingDates = new List<DateOnly>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (!realDaily.ContainsKey(date))
                missingDates.Add(date);
        }

        var forecastByDate = new Dictionary<DateOnly, DailyGddPoint>();
        var hasForecast = false;
        var stale = false;
        if (missingDates.Count > 0)
        {
            // Guard 1 vez/día (respetando el toggle administrativo) + fetch
            // serializado: la carrera fetch programado vs perezoso se resuelve en
            // WeatherService (semáforo + re-check de la DB dentro del lock).
            var fetchedToday = await _weatherService.WasFetchedTodayAsync(ct);
            var limitDisabled = await _settings.GetBoolAsync(
                SettingsService.WeatherDailyLimitDisabledKey, false, ct);
            if (limitDisabled || !fetchedToday)
            {
                await _weatherService.FetchForecastForDatesAsync(from, to, ct);
            }

            var rows = await _weatherService.GetForecastForDatesAsync(from, to, ct);
            foreach (var row in rows)
            {
                var tmin = row.TempMin;
                var tmax = row.TempMax;
                if (tmin > tmax)
                {
                    warnings.Add($"Pronóstico {row.Date:yyyy-MM-dd} con Tmin {tmin} > Tmax {tmax}: se intercambian y se continúa (validación F-03).");
                    (tmin, tmax) = (tmax, tmin);
                }

                forecastByDate[row.Date] = new DailyGddPoint(
                    row.Date, DailyGdd(tmax, tmin, baseTemp), GddPointSource.Forecast);
            }

            hasForecast = forecastByDate.Count > 0;
            if (rows.Count > 0)
            {
                var lastFetched = rows.Max(r => r.FetchedAt);
                if (DateOnly.FromDateTime(lastFetched) < today.AddDays(-3))
                {
                    stale = true;
                    warnings.Add($"Pronóstico vencido (último fetch {lastFetched:yyyy-MM-dd}): puede estar desactualizado.");
                }
            }
            else
            {
                warnings.Add($"Sin pronóstico climático para los próximos {horizon} días ({from:yyyy-MM-dd}..{to:yyyy-MM-dd}): la proyección usa fallback del sensor.");
            }
        }

        // 3. Fallback: promedio de los últimos días observados (nunca cero silencioso).
        var fallback = await GetRecentAverageDailyGddOrNullAsync(lot, asOfDate, ct: ct);
        if (fallback is null)
        {
            warnings.Add("Sin promedio de días observados para fallback: los días sin pronóstico quedan sin valor de GDD (no se asume 0).");
        }

        var projection = new List<DailyGddPoint>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (realDaily.TryGetValue(date, out var real))
            {
                projection.Add(new DailyGddPoint(date, Math.Round(real, 2), GddPointSource.Observed));
            }
            else if (forecastByDate.TryGetValue(date, out var fc))
            {
                projection.Add(new DailyGddPoint(date, Math.Round(fc.Gdd, 2), GddPointSource.Forecast));
            }
            else if (fallback is { } avg)
            {
                projection.Add(new DailyGddPoint(date, Math.Round(avg, 2), GddPointSource.Fallback));
            }
            else
            {
                projection.Add(new DailyGddPoint(date, 0m, GddPointSource.Fallback));
            }
        }

        return new FutureProjectionResult(projection, hasForecast, stale, warnings);
    }

    /// <summary>
    /// Fecha estimada de cosecha según GddTarget del cultivo (fallback de la regla
    /// híbrida, F-10): suma la proyección hasta alcanzar el target; extrapola fuera
    /// del horizonte con el último GDD diario (etiquetado como extrapolación por el
    /// llamador); sin proyección usa EstimatedDaysToHarvest del cultivo.
    /// </summary>
    public DateOnly? EstimateHarvestDateAsync(
        Lot lot,
        decimal accumulatedGdd,
        IReadOnlyList<DailyGddPoint> futureProjection,
        DateOnly? asOfDate = null)
    {
        var target = lot.CropType?.GddTarget ?? 300m;
        if (accumulatedGdd >= target)
            return asOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

        var cross = GddDateLogic.EstimateCrossDate(accumulatedGdd, futureProjection, target);
        if (cross is not null)
            return cross;

        if (lot.CropType?.EstimatedDaysToHarvest is int estDays)
        {
            var effectiveToday = asOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var elapsed = (effectiveToday.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
                           - lot.SowingDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).Days;
            var remainingDays = Math.Max(0, estDays - elapsed);
            return effectiveToday.AddDays(remainingDays);
        }

        return null;
    }
}