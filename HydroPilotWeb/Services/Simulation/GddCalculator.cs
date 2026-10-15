using HydroPilotWeb.Services.Forecasting;

namespace HydroPilotWeb.Services.Simulation;

/// <summary>
/// Cálculo puro de GDD del escenario (SIM-04). Reutiliza el NÚCLEO de forecasting:
/// <see cref="GddService.DailyGdd"/> para el GDD diario. Las fechas de las ventanas y de los
/// cortes las calcula CycleTimelineLogic (la misma lógica que la vista Lotes).
/// </summary>
public static class GddCalculator
{
    /// <summary>
    /// Puntos diarios de GDD desde la temperatura manual del escenario
    /// (SIM-03). Cada día usa <see cref="GddService.DailyGdd"/> con Tbase del cultivo.
    /// </summary>
    public static IReadOnlyList<SimulationGddPoint> ManualPoints(
        SimulationClimateInput climate,
        decimal baseTemperature,
        DateOnly from,
        DateOnly to)
    {
        if (climate.Mode != SimulationClimateMode.Manual
            || climate.ManualTempMinC is not { } tmin
            || climate.ManualTempMaxC is not { } tmax
            || to < from)
        {
            return [];
        }

        var points = new List<SimulationGddPoint>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            points.Add(new SimulationGddPoint(
                date,
                Math.Round(GddService.DailyGdd(tmax, tmin, baseTemperature), 2),
                SimulationGddSource.Manual));
        }

        return points;
    }

    /// <summary>
    /// GDD día a día del escenario LIBRE con clima manual, de la siembra a hoy: cada día suma el
    /// GDD de esas temperaturas. Siembra futura: todavía no hay días (vacío). Con lote, el historial
    /// real lo da GddService.
    /// </summary>
    public static IReadOnlyList<DailyGddPoint> ManualHistory(
        SimulationClimateInput climate,
        decimal baseTemperature,
        DateOnly sowingDate,
        DateOnly today) =>
        ManualPoints(climate, baseTemperature, sowingDate, today)
            .Select(p => new DailyGddPoint(p.Date, p.Gdd, GddPointSource.Observed))
            .ToList();

    /// <summary>
    /// Primer día que se proyecta con el clima del escenario: mañana, o el día de la siembra si
    /// todavía no se sembró (escenario libre para planificar).
    /// </summary>
    public static DateOnly ProjectionStart(DateOnly sowingDate, DateOnly today) =>
        sowingDate > today ? sowingDate : today.AddDays(1);

    /// <summary>Días desde hoy hasta la fecha destino.</summary>
    public static int? DaysRemaining(DateOnly today, DateOnly? target) =>
        target is { } t ? t.DayNumber - today.DayNumber : null;

    /// <summary>Proyección del escenario como puntos del núcleo de forecasting (para CycleTimelineLogic/GddCurveLogic).</summary>
    public static IReadOnlyList<DailyGddPoint> ToCorePoints(IReadOnlyList<SimulationGddPoint> points) =>
        points.Select(p => new DailyGddPoint(p.Date, p.Gdd, p.Source switch
        {
            SimulationGddSource.Forecast => GddPointSource.Forecast,
            // Manual / respaldo: para las fechas solo cuentan Date y Gdd.
            _ => GddPointSource.Fallback
        })).ToList();
}
