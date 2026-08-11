using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;

namespace HydroPilotWeb.Services;

/// <summary>
/// Calcula Grados Día de Desarrollo (GDD) para un lote.
/// Histórico: lecturas de sensores del invernadero.
/// Futuro: DailyWeatherForecast (DB primero, fetch perezoso si faltan fechas).
/// Fórmula: GDD_diario = max(0, (min(Tmax,30) + Tmin)/2 - Tbase)
/// </summary>
public class GddService
{
    private const int ForecastHorizonDays = 7;

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

    public static decimal DailyGdd(decimal tmax, decimal tmin, decimal baseTemperature)
    {
        var cappedMax = Math.Min(tmax, 30m);
        return Math.Max(0m, (cappedMax + tmin) / 2m - baseTemperature);
    }

    /// <summary>
    /// GDD diario por fecha, calculado desde lecturas de sensores de temperatura del invernadero.
    /// asOfDate simula "hoy": solo se consideran lecturas hasta el final de ese día.
    /// </summary>
    public async Task<Dictionary<DateOnly, decimal>> GetDailyGddByDateAsync(
        Lot lot,
        DateOnly? asOfDate = null,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var effectiveToday = asOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var sowingDate = lot.SowingDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var cutoff = effectiveToday.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var baseTemp = lot.CropType?.BaseTemperature ?? 4.5m;

        var readings = await context.SensorReadings
            .Include(r => r.Sensor!).ThenInclude(s => s.SensorType)
            .Include(r => r.Sensor!).ThenInclude(s => s.Node)
            .Where(r => r.Sensor!.SensorType!.Name == "Temperatura")
            .Where(r => r.Sensor!.Node!.GreenhouseId == lot.GreenhouseId)
            .Where(r => r.Timestamp >= sowingDate)
            .Where(r => r.Timestamp < cutoff)
            .Select(r => new { r.Timestamp, r.Value })
            .ToListAsync(ct);

        var result = new Dictionary<DateOnly, decimal>();

        foreach (var group in readings.GroupBy(r => DateOnly.FromDateTime(r.Timestamp)))
        {
            var tmax = group.Max(r => r.Value);
            var tmin = group.Min(r => r.Value);
            result[group.Key] = DailyGdd(tmax, tmin, baseTemp);
        }

        return result;
    }

    /// <summary>
    /// GDD acumulado del lote desde la fecha de siembra.
    /// </summary>
    public async Task<decimal> GetAccumulatedGddAsync(Lot lot, DateOnly? asOfDate = null, CancellationToken ct = default)
    {
        var daily = await GetDailyGddByDateAsync(lot, asOfDate, ct);
        return daily.Values.Sum();
    }

    /// <summary>
    /// GDD diario promedio de los últimos N días (fallback si no hay pronóstico).
    /// </summary>
    public async Task<decimal> GetRecentAverageDailyGddAsync(
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

        if (recent.Count == 0) return 0m;
        return recent.Average();
    }

    /// <summary>
    /// Proyección diaria de GDD futuro: DB primero; si faltan fechas, fetch perezoso
    /// (no respeta el toggle). Fechas sin dato → fallback promedio del sensor.
    /// </summary>
    public async Task<List<DailyGddPoint>> GetFutureGddProjectionAsync(
        Lot lot,
        DateOnly? asOfDate = null,
        int horizonDays = ForecastHorizonDays,
        CancellationToken ct = default)
    {
        var baseTemp = lot.CropType?.BaseTemperature ?? 4.5m;
        var today = asOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today.AddDays(1);
        var to = today.AddDays(horizonDays);

        // 1. DB primero
        var rows = await _weatherService.GetForecastForDatesAsync(from, to, ct);

        // 2. Si faltan fechas, fetch perezoso.
        //    Por defecto hay un guard de 1 vez/día (evita llamar a OpenWeather en cada carga).
        //    Si WeatherDailyLimitDisabled está activo, se consulta siempre que falten fechas.
        var existingDates = rows.Select(r => r.Date).ToHashSet();
        var expected = (to.DayNumber - from.DayNumber) + 1;
        var limitDisabled = await _settings.GetBoolAsync(
            SettingsService.WeatherDailyLimitDisabledKey, false, ct);
        var fetchedToday = rows.Any(r => r.FetchedAt.Date == DateTime.UtcNow.Date);
        if (existingDates.Count < expected && (limitDisabled || !fetchedToday))
        {
            await _weatherService.FetchForecastForDatesAsync(from, to, ct);
            rows = await _weatherService.GetForecastForDatesAsync(from, to, ct);
        }

        var byDate = rows.ToDictionary(r => r.Date);

        // 3. Fallback para fechas que sigan faltando
        var fallback = await GetRecentAverageDailyGddAsync(lot, ct: ct);

        var projection = new List<DailyGddPoint>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var gdd = byDate.TryGetValue(date, out var row)
                ? DailyGdd(row.TempMax, row.TempMin, baseTemp)
                : fallback;

            projection.Add(new DailyGddPoint(date, Math.Round(gdd, 2)));
        }

        return projection;
    }

    /// <summary>
    /// Fecha estimada de cosecha: suma día a día la proyección futura hasta alcanzar el target.
    /// Si el horizonte no alcanza, extrapola con el último valor. Sin proyección → días estimados del cultivo.
    /// </summary>
    public DateOnly? EstimateHarvestDateAsync(
        Lot lot,
        decimal accumulatedGdd,
        List<DailyGddPoint> futureProjection,
        DateOnly? asOfDate = null)
    {
        var target = lot.CropType?.GddTarget ?? 300m;
        var effectiveToday = asOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

        if (accumulatedGdd >= target)
            return effectiveToday;

        var remaining = target - accumulatedGdd;

        foreach (var point in futureProjection)
        {
            remaining -= point.Gdd;
            if (remaining <= 0)
                return point.Date;
        }

        // No alcanzó en el horizonte: extrapolar con el último GDD diario
        if (futureProjection.Count > 0)
        {
            var last = futureProjection[^1].Gdd;
            if (last > 0)
            {
                var extraDays = (int)Math.Ceiling(remaining / last);
                return futureProjection[^1].Date.AddDays(extraDays);
            }
        }

        if (lot.CropType?.EstimatedDaysToHarvest is int estDays)
        {
            var elapsed = (effectiveToday.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
                           - lot.SowingDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).Days;
            var remainingDays = Math.Max(0, estDays - elapsed);
            return effectiveToday.AddDays(remainingDays);
        }

        return null;
    }
}

public record DailyGddPoint(DateOnly Date, decimal Gdd);
