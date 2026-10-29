using HydroPilotWeb.Models.Notifications;
using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Services.Dashboard;

/// <summary>
/// Plantas de los lotes activos en un estado (barra del gráfico). Stage = etapa comercial (para el color
/// de la vista Lotes); null = descartadas.
/// </summary>
public sealed record DashboardStageCount(string Label, int Plants, string? Stage);

/// <summary>Total en $ de los lotes activos; Amount null si a algún lote le falta peso o precio para calcularlo.</summary>
public sealed record DashboardMoney(decimal? Amount, string Currency);

public sealed record DashboardAlertItem(int Id, string Severity, string Title, string Link, DateTime CreatedAtLocal, bool IsResolved);

/// <summary>Próximo óptimo de corte de un lote. Approximate = cae más allá del pronóstico (se muestra con ≈).</summary>
public sealed record DashboardHarvestItem(
    int LotId, string LotName, HarvestDestination Destination, DateOnly Date, bool Approximate, bool CutNow, string Link);

public sealed record DashboardVisitItem(string Title, string View, string Link, DateTime VisitedAtLocal);

/// <summary>
/// Chequeo de un sensor: próximo = última calibración + días del tipo (Ajustes &gt; Parámetros). Sin
/// calibración registrada, el chequeo vence hoy. El dashboard solo trae los vencidos y los que vencen en
/// <see cref="GreenhouseDashboardRules.CheckWindowDays"/> días o menos; todos están en Infraestructura &gt; Sensores.
/// </summary>
public sealed record DashboardSensorCheck(
    int SensorId, string Place, string SensorType, DateOnly? LastCalibration, int IntervalDays, DateOnly DueDate);

/// <summary>Todo lo que muestra el dashboard del invernadero (vista Invernadero).</summary>
public sealed record GreenhouseDashboard(
    DateOnly Today,
    DateTime NowLocal,
    int ActiveLots,
    DashboardMoney CurrentRevenue,
    DashboardMoney ExpectedRevenue,
    IReadOnlyList<DashboardStageCount> Stages,
    int TotalPlants,
    int ExpectedBabyLeafPlants,
    IReadOnlyList<DashboardAlertItem> Alerts, // las abiertas y las últimas resueltas (GreenhouseDashboardService.AlertsShown)
    IReadOnlyList<DashboardHarvestItem> Harvests,
    IReadOnlyList<DashboardVisitItem> RecentVisits,
    IReadOnlyList<DashboardSensorCheck> SensorChecks,
    bool HasSensorIntervals);

/// <summary>Reglas puras del dashboard: links de las alertas, cosechas, chequeos y totales.</summary>
public static class GreenhouseDashboardRules
{
    /// <summary>Días hacia adelante que mira el dashboard en los chequeos de sensores.</summary>
    public const int CheckWindowDays = 7;

    /// <summary>
    /// A dónde lleva una alerta: error de sensor de un circuito, a Optimización con ese circuito; nodo
    /// desconectado o error de un sensor del invernadero, a Optimización; las de la solución (anomalías
    /// químicas, fallas de dosificación) a Optimización con el lote; el resto a Lotes; sin lote, a la
    /// bandeja de notificaciones.
    /// </summary>
    public static string AlertLink(string type, int? lotId, int? circuitId = null) => (type, lotId) switch
    {
        (NotificationTypes.ErrorSensor, _) when circuitId is not null => $"optimization?circuito={circuitId}",
        (NotificationTypes.NodoDesconectado or NotificationTypes.ErrorSensor, null) => "optimization",
        (_, null) => "notifications",
        (NotificationTypes.DesbalanceQuimico or NotificationTypes.FallaDosificacion or NotificationTypes.NodoDesconectado
            or NotificationTypes.ErrorSensor, { } id) => $"optimization?lote={id}",
        (_, { } id) => $"lotes?lote={id}"
    };

    /// <summary>
    /// Próximos cortes de un lote según su línea de tiempo: el óptimo Baby Leaf mientras su ventana no
    /// cerró y el convencional mientras no pasó a sobremadurez. CutNow = ya está en el óptimo.
    /// </summary>
    public static IEnumerable<DashboardHarvestItem> Harvests(int lotId, string lotName, CycleTimeline timeline)
    {
        var cut = timeline.NextCut;
        var link = $"lotes?lote={lotId}";
        if (timeline.Phase <= CyclePhase.VentanaBabyLeaf && timeline.BabyLeafOptimum.Date is { } babyLeaf)
            yield return new DashboardHarvestItem(lotId, lotName, HarvestDestination.BabyLeaf, babyLeaf,
                timeline.BabyLeafOptimum.IsExtrapolated, cut is { Destination: HarvestDestination.BabyLeaf, CutNow: true }, link);
        if (timeline.Phase <= CyclePhase.VentanaConvencional && timeline.ConventionalOptimum.Date is { } conventional)
            yield return new DashboardHarvestItem(lotId, lotName, HarvestDestination.Convencional, conventional,
                timeline.ConventionalOptimum.IsExtrapolated, cut is { Destination: HarvestDestination.Convencional, CutNow: true }, link);
    }

    /// <summary>Próximo chequeo: última calibración + intervalo; sin calibración registrada, hoy.</summary>
    public static DateOnly NextCheck(DateOnly? lastCalibration, int intervalDays, DateOnly today) =>
        lastCalibration is { } last ? last.AddDays(intervalDays) : today;

    /// <summary>Vencido o vence en <see cref="CheckWindowDays"/> días o menos: va en el dashboard.</summary>
    public static bool DueSoon(DateOnly dueDate, DateOnly today) => dueDate.DayNumber - today.DayNumber <= CheckWindowDays;

    /// <summary>Suma de $ de los lotes; null si a alguno le falta (no se muestra un total incompleto).</summary>
    public static decimal? Total(IEnumerable<decimal?> amounts)
    {
        var total = 0m;
        foreach (var amount in amounts)
        {
            if (amount is not { } value)
                return null;
            total += value;
        }
        return total;
    }

    /// <summary>$ de un escenario de un lote: null si un destino con plantas no tiene importe.</summary>
    public static decimal? Revenue(YieldScenario scenario)
    {
        decimal? Part(DestinationYield d) => d.Plants == 0 ? 0m : d.Revenue;
        return Part(scenario.BabyLeaf) is { } babyLeaf && Part(scenario.Conventional) is { } conventional
            ? babyLeaf + conventional
            : null;
    }
}
