using System.Security.Claims;
using HydroPilotWeb.Data;
using HydroPilotWeb.Services.Dashboard;
using HydroPilotWeb.Services.Lotes;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Services.Sensors;

/// <summary>
/// Un sensor como lo muestra Infraestructura &gt; Sensores: tipo, dónde está (el circuito, o el invernadero para
/// temperatura y humedad), si está activo y su chequeo. IntervalDays y DueDate null = su tipo no tiene chequeo programado
/// (Ajustes &gt; Parámetros) o el sensor está inactivo.
/// </summary>
public sealed record SensorCheckRow(
    int SensorId,
    string Name,
    string SensorType,
    string Place,
    bool IsActive,
    DateOnly? LastCalibration,
    int? IntervalDays,
    DateOnly? DueDate);

/// <summary>Todos los sensores con su chequeo (los que vencen antes, arriba), hoy en el invernadero y si hay intervalos.</summary>
public sealed record SensorCheckList(DateOnly Today, IReadOnlyList<SensorCheckRow> Sensors, bool HasIntervals);

/// <summary>Cómo está el chequeo de un sensor: vencido (o vence hoy), vence en 7 días o menos, al día o sin programar.</summary>
public enum SensorCheckState
{
    Overdue,
    DueSoon,
    UpToDate,
    NotScheduled,
}

public static class SensorCheckRules
{
    public static SensorCheckState State(DateOnly? dueDate, DateOnly today) => dueDate switch
    {
        null => SensorCheckState.NotScheduled,
        { } due when due <= today => SensorCheckState.Overdue,
        { } due when GreenhouseDashboardRules.DueSoon(due, today) => SensorCheckState.DueSoon,
        _ => SensorCheckState.UpToDate,
    };
}

/// <summary>
/// Sensores del invernadero y sus chequeos: próximo = última calibración + días del tipo (Ajustes &gt; Parámetros); sin
/// calibración registrada, vence hoy. "Calibrado hoy" anota hoy como fecha de calibración. Lo usan Infraestructura &gt;
/// Sensores (todos) y el dashboard (los vencidos y los que vencen en 7 días o menos).
/// </summary>
public sealed class SensorService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly SystemParametersService _parameters;
    private readonly ILogger<SensorService> _logger;

    public SensorService(
        IDbContextFactory<HydroPilotDbContext> dbFactory, SystemParametersService parameters, ILogger<SensorService> logger)
    {
        _dbFactory = dbFactory;
        _parameters = parameters;
        _logger = logger;
    }

    /// <summary>Registran un chequeo de sensor todos los roles asignados (el que calibra suele ser el operario).</summary>
    public static bool CanRegisterCalibration(ClaimsPrincipal user) => LotCommentRules.CommenterRoles.Any(user.IsInRole);

    public async Task<SensorCheckList> ListAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var timeZoneId = await context.Greenhouses.AsNoTracking().OrderBy(g => g.Id).Select(g => g.TimeZoneId).FirstOrDefaultAsync(ct);
        var zone = GddService.ResolveGreenhouseTimeZone(timeZoneId, []);
        DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        var today = DateOnly.FromDateTime(Local(DateTime.UtcNow));

        var intervals = (await _parameters.GetSensorCheckIntervalsAsync(ct))
            .Where(i => i.Days is not null)
            .ToDictionary(i => i.SensorType, i => i.Days!.Value);

        var sensors = await context.Sensors
            .AsNoTracking()
            .Select(s => new
            {
                s.Id,
                s.Name,
                // pH y CE: el circuito; temperatura y humedad: el invernadero.
                Place = s.Circuit != null ? s.Circuit.Name : s.Node!.Greenhouse!.Name,
                Type = s.SensorType!.Name,
                s.IsActive,
                s.LastCalibrationDate,
            })
            .ToListAsync(ct);

        var rows = sensors
            .Select(s =>
            {
                DateOnly? last = s.LastCalibrationDate is { } calibrated ? DateOnly.FromDateTime(Local(calibrated)) : null;
                int? days = s.IsActive && intervals.TryGetValue(s.Type, out var every) ? every : null;
                DateOnly? due = days is { } d ? GreenhouseDashboardRules.NextCheck(last, d, today) : null;
                return new SensorCheckRow(s.Id, s.Name, s.Type, s.Place, s.IsActive, last, days, due);
            })
            .OrderBy(r => r.DueDate is null)
            .ThenBy(r => r.DueDate)
            .ThenBy(r => r.Place, NaturalStringComparer.Instance)
            .ThenBy(r => r.SensorType)
            .ToList();
        return new SensorCheckList(today, rows, intervals.Count > 0);
    }

    /// <summary>Anota hoy como fecha de calibración del sensor (chequeo hecho).</summary>
    public async Task<bool> RegisterCalibrationAsync(int sensorId, ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (!CanRegisterCalibration(user))
            return false;

        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var sensor = await context.Sensors.FirstOrDefaultAsync(s => s.Id == sensorId, ct);
        if (sensor is null)
            return false;

        sensor.LastCalibrationDate = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);
        _logger.LogInformation("Chequeo del sensor {SensorId} ({Sensor}) registrado por {User}", sensorId, sensor.Name, user.Identity?.Name);
        return true;
    }
}
