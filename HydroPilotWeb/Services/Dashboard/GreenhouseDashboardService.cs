using System.Security.Claims;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Notifications;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.SensorFaults;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Services.Dashboard;

/// <summary>
/// Dashboard del invernadero (vista Invernadero): resume todos los lotes activos con la misma lógica
/// de la vista Lotes (LotOverviewService: plantas, rendimiento actual y esperado, ventanas de cosecha)
/// y suma las alertas, los últimos accedidos del usuario y los próximos chequeos de sensores.
/// </summary>
public class GreenhouseDashboardService
{
    public const int ListSize = 30;

    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly LotOverviewService _lots;
    private readonly SystemParametersService _parameters;
    private readonly RecentVisitService _visits;
    private readonly ILogger<GreenhouseDashboardService> _logger;

    public GreenhouseDashboardService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        LotOverviewService lots,
        SystemParametersService parameters,
        RecentVisitService visits,
        ILogger<GreenhouseDashboardService> logger)
    {
        _dbFactory = dbFactory;
        _lots = lots;
        _parameters = parameters;
        _visits = visits;
        _logger = logger;
    }

    /// <summary>Registran un chequeo de sensor todos los roles asignados (el que calibra suele ser el operario).</summary>
    public static bool CanRegisterCalibration(ClaimsPrincipal user) => LotCommentRules.CommenterRoles.Any(user.IsInRole);

    public async Task<GreenhouseDashboard> GetAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var timeZoneId = await context.Greenhouses.AsNoTracking().OrderBy(g => g.Id).Select(g => g.TimeZoneId).FirstOrDefaultAsync(ct);
        var zone = GddService.ResolveGreenhouseTimeZone(timeZoneId, []);
        DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        var nowLocal = Local(DateTime.UtcNow);
        var today = DateOnly.FromDateTime(nowLocal);

        // Lotes activos = en curso (ni cosechados ni descartados), como los operativos del selector de Lotes.
        var activeLots = await context.Lots
            .AsNoTracking()
            .Where(l => l.Status!.Name != LotStatusNames.Cosechado && l.Status.Name != LotStatusNames.Descartado)
            .OrderBy(l => l.SowingDate)
            .Select(l => new { l.Id, l.Name })
            .ToListAsync(ct);

        var current = new List<decimal?>();
        var expected = new List<decimal?>();
        var currency = YieldPrices.None.Currency;
        int developing = 0, apta = 0, candidate = 0, conventional = 0, discarded = 0, risk = 0, total = 0, expectedBabyLeaf = 0;
        var harvests = new List<DashboardHarvestItem>();

        foreach (var lot in activeLots)
        {
            LotOverviewDto? overview;
            try
            {
                overview = await _lots.GetOverviewAsync(lot.Id, ct: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Un lote con problemas no tira abajo el dashboard: no suma, y su $ queda sin calcular.
                _logger.LogWarning(ex, "No se pudo calcular el lote {LotId} para el dashboard", lot.Id);
                current.Add(null);
                expected.Add(null);
                continue;
            }
            if (overview is null)
                continue;

            var plants = overview.Plants;
            developing += plants.Developing;
            apta += plants.Apta;
            candidate += plants.Candidate;
            conventional += plants.Conventional;
            discarded += plants.Discarded;
            risk += plants.Risk;
            total += plants.TotalPlants;
            expectedBabyLeaf += overview.Yield.Expected.BabyLeaf.Plants;

            current.Add(GreenhouseDashboardRules.Revenue(overview.Yield.Current));
            expected.Add(GreenhouseDashboardRules.Revenue(overview.Yield.Expected));
            currency = overview.Yield.Prices.Currency;

            if (overview.Timeline is { } timeline)
                harvests.AddRange(GreenhouseDashboardRules.Harvests(lot.Id, lot.Name ?? $"Lote {lot.Id}", timeline));
        }

        // Mismos estados que el mapa de Lotes (el color lo pone la vista con la paleta de Lotes).
        var stages = new List<DashboardStageCount>
        {
            new("En desarrollo", developing, CommercialStageNames.EnDesarrollo),
            new("Baby Leaf", apta, CommercialStageNames.BabyLeafApta),
            new("Candidata", candidate, CommercialStageNames.CandidataBabyLeaf),
            new("Adulta", conventional, CommercialStageNames.CosechaConvencional),
            new("Descartada", discarded, null),
            new("Riesgo", risk, CommercialStageNames.RiesgoFueraDeVentana),
        };

        var (sensorChecks, hasIntervals) = await SensorChecksAsync(context, Local, today, ct);

        return new GreenhouseDashboard(
            today,
            nowLocal,
            activeLots.Count,
            new DashboardMoney(GreenhouseDashboardRules.Total(current), currency),
            new DashboardMoney(GreenhouseDashboardRules.Total(expected), currency),
            stages,
            total,
            expectedBabyLeaf,
            await AlertsAsync(context, Local, ct),
            harvests.OrderBy(h => h.Date).ThenBy(h => h.Destination).ToList(),
            await VisitsAsync(user, Local, ct),
            sensorChecks,
            hasIntervals);
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

    private static async Task<IReadOnlyList<DashboardAlertItem>> AlertsAsync(
        HydroPilotDbContext context, Func<DateTime, DateTime> local, CancellationToken ct)
    {
        var rows = await context.NotificationAlerts
            .AsNoTracking()
            .Where(a => a.Type != NotificationTypes.PruebaSistema)
            .OrderByDescending(a => a.CreatedAtUtc)
            .ThenByDescending(a => a.Id)
            .Take(ListSize)
            .Select(a => new { a.Id, a.Type, a.Severity, a.Title, a.LotId, a.Fingerprint, a.CreatedAtUtc, a.IsResolved })
            .ToListAsync(ct);

        // Error de sensor de un circuito: el link va a Optimización con ese circuito (sale de la huella de la alerta).
        return rows
            .Select(a => new DashboardAlertItem(a.Id, a.Severity, a.Title,
                GreenhouseDashboardRules.AlertLink(a.Type, a.LotId, SensorFaultAlerts.CircuitId(a.Fingerprint)),
                local(a.CreatedAtUtc), a.IsResolved))
            .ToList();
    }

    private async Task<IReadOnlyList<DashboardVisitItem>> VisitsAsync(ClaimsPrincipal user, Func<DateTime, DateTime> local, CancellationToken ct)
    {
        var visits = await _visits.GetAsync(user, 10, ct);
        return visits
            .Select(v =>
            {
                var optimization = v.Kind == RecentVisitKinds.Optimization;
                var title = v.LotName ?? v.CircuitName ?? "Lote";
                var link = (optimization, v.LotId) switch
                {
                    (false, { } lotId) => $"lotes?lote={lotId}",
                    (true, { } lotId) => $"optimization?lote={lotId}",
                    _ => $"optimization?circuito={v.CircuitId}"
                };
                return new DashboardVisitItem(title, optimization ? "Optimización" : "Lotes", link, local(v.VisitedAtUtc));
            })
            .ToList();
    }

    private async Task<(IReadOnlyList<DashboardSensorCheck> Checks, bool HasIntervals)> SensorChecksAsync(
        HydroPilotDbContext context, Func<DateTime, DateTime> local, DateOnly today, CancellationToken ct)
    {
        var intervals = (await _parameters.GetSensorCheckIntervalsAsync(ct))
            .Where(i => i.Days is not null)
            .ToDictionary(i => i.SensorType, i => i.Days!.Value);

        var sensors = await context.Sensors
            .AsNoTracking()
            .Where(s => s.IsActive)
            .Select(s => new
            {
                s.Id,
                // pH y CE: el circuito; temperatura y humedad: el invernadero.
                Place = s.Circuit != null ? s.Circuit.Name : s.Node!.Greenhouse!.Name,
                Type = s.SensorType!.Name,
                s.LastCalibrationDate,
            })
            .ToListAsync(ct);

        var checks = sensors
            .Where(s => intervals.ContainsKey(s.Type))
            .Select(s =>
            {
                DateOnly? last = s.LastCalibrationDate is { } calibrated ? DateOnly.FromDateTime(local(calibrated)) : null;
                var days = intervals[s.Type];
                return new DashboardSensorCheck(s.Id, s.Place, s.Type, last, days, GreenhouseDashboardRules.NextCheck(last, days, today));
            })
            .OrderBy(c => c.DueDate)
            .ThenBy(c => c.Place)
            .ThenBy(c => c.SensorType)
            .ToList();
        return (checks, intervals.Count > 0);
    }

}
