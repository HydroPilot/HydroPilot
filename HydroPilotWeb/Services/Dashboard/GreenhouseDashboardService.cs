using System.Security.Claims;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Notifications;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.SensorFaults;
using HydroPilotWeb.Services.Sensors;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Services.Dashboard;

/// <summary>
/// Dashboard del invernadero (vista Invernadero): resume todos los lotes activos con la misma lógica
/// de la vista Lotes (LotOverviewService: plantas, rendimiento actual y esperado, ventanas de cosecha)
/// y suma las alertas, los últimos accedidos del usuario y los chequeos de sensores vencidos o que vencen en 7 días o menos
/// (<see cref="SensorService"/>).
/// </summary>
public class GreenhouseDashboardService
{
    /// <summary>Alertas del dashboard: todas las abiertas y, si queda lugar, las últimas resueltas hasta este total.</summary>
    public const int AlertsShown = 5;

    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly LotOverviewService _lots;
    private readonly RecentVisitService _visits;
    private readonly SensorService _sensors;
    private readonly ILogger<GreenhouseDashboardService> _logger;

    public GreenhouseDashboardService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        LotOverviewService lots,
        RecentVisitService visits,
        SensorService sensors,
        ILogger<GreenhouseDashboardService> logger)
    {
        _dbFactory = dbFactory;
        _lots = lots;
        _visits = visits;
        _sensors = sensors;
        _logger = logger;
    }

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

        var (sensorChecks, hasIntervals) = await SensorChecksAsync(today, ct);

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

    /// <summary>
    /// Las abiertas siempre (aunque sean más de <see cref="AlertsShown"/>), más nuevas primero; si queda lugar, las últimas
    /// resueltas hasta completar <see cref="AlertsShown"/>. Todas están en la bandeja de notificaciones.
    /// </summary>
    private static async Task<IReadOnlyList<DashboardAlertItem>> AlertsAsync(
        HydroPilotDbContext context, Func<DateTime, DateTime> local, CancellationToken ct)
    {
        var alerts = context.NotificationAlerts
            .AsNoTracking()
            .Where(a => a.Type != NotificationTypes.PruebaSistema)
            .OrderByDescending(a => a.CreatedAtUtc)
            .ThenByDescending(a => a.Id)
            .Select(a => new { a.Id, a.Type, a.Severity, a.Title, a.LotId, a.Fingerprint, a.CreatedAtUtc, a.IsResolved });
        var rows = await alerts.Where(a => !a.IsResolved).ToListAsync(ct);
        if (rows.Count < AlertsShown)
            rows.AddRange(await alerts.Where(a => a.IsResolved).Take(AlertsShown - rows.Count).ToListAsync(ct));

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

    /// <summary>Los sensores activos con chequeo vencido o que vence en 7 días o menos (los más cercanos arriba).</summary>
    private async Task<(IReadOnlyList<DashboardSensorCheck> Checks, bool HasIntervals)> SensorChecksAsync(
        DateOnly today, CancellationToken ct)
    {
        var list = await _sensors.ListAsync(ct);
        var checks = list.Sensors
            .Where(s => s is { IntervalDays: not null, DueDate: { } due } && GreenhouseDashboardRules.DueSoon(due, today))
            .Select(s => new DashboardSensorCheck(s.SensorId, s.Place, s.SensorType, s.LastCalibration, s.IntervalDays!.Value, s.DueDate!.Value))
            .ToList();
        return (checks, list.HasIntervals);
    }

}
