using HydroPilotWeb.Services.Forecasting;

namespace HydroPilotWeb.Services.Simulation;

/// <summary>
/// Cálculo puro de GDD del escenario (SIM-04). Reutiliza el NÚCLEO de forecasting:
/// <see cref="GddService.DailyGdd"/> para el GDD diario y
/// <see cref="GddDateLogic.EstimateCrossDate"/> para la fecha de cruce de umbrales.
/// NO copia fórmulas: delega en esos contratos compartidos.
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
    /// GDD acumulado del escenario LIBRE en modo manual: suma el GDD manual desde
    /// la siembra hasta la fecha de referencia (what-if: "si el clima hubiera sido
    /// este"). Con referencia anterior a la siembra devuelve 0 con advertencia
    /// (período vacío; nunca se inventa un valor). Con lote, la base observada la
    /// provee GddService respetando AsOfDate. Los modos histórico/pronóstico sin
    /// lote no tienen lecturas propias: base no disponible, informada como tal.
    /// </summary>
    public static (decimal Accumulated, IReadOnlyList<string> Warnings) AccumulatedManualScenario(
        SimulationClimateInput climate,
        decimal baseTemperature,
        DateOnly sowingDate,
        DateOnly referenceDate)
    {
        var warnings = new List<string>();
        if (referenceDate < sowingDate)
        {
            warnings.Add($"Fecha simulada ({referenceDate:yyyy-MM-dd}) anterior a la siembra ({sowingDate:yyyy-MM-dd}): el GDD del escenario queda en 0 (período sin calcular).");
            return (0m, warnings);
        }

        if (climate.Mode != SimulationClimateMode.Manual
            || climate.ManualTempMinC is not { } tmin
            || climate.ManualTempMaxC is not { } tmax)
        {
            warnings.Add("Escenario libre sin modo manual: el GDD base observado no está disponible (no se asume 0 como medición); la proyección parte de 0.");
            return (0m, warnings);
        }

        var accumulated = 0m;
        for (var date = sowingDate; date <= referenceDate; date = date.AddDays(1))
        {
            accumulated += GddService.DailyGdd(tmax, tmin, baseTemperature);
        }

        return (Math.Round(accumulated, 2), warnings);
    }

    /// <summary>
    /// Fecha estimada de cosecha del escenario por GDD puro (SIM-04): cruza el
    /// <paramref name="target"/> con la proyección mediante
    /// <see cref="GddDateLogic.EstimateCrossDate"/> (el mismo núcleo que usa
    /// GddService). Si la proyección no alcanza el umbral y el cultivo declara
    /// días estimados, usa el respaldo por días transcurridos desde la siembra
    /// (misma lógica que GddService.EstimateHarvestDateAsync). La extrapolación
    /// fuera del horizonte se informa (IsExtrapolation), nunca se etiqueta como
    /// pronóstico climático.
    /// </summary>
    public static (DateOnly? Date, bool IsExtrapolation, IReadOnlyList<string> Warnings) EstimateHarvestDate(
        decimal accumulated,
        IReadOnlyList<SimulationGddPoint> projection,
        decimal target,
        DateOnly referenceDate,
        DateOnly? sowingDate,
        int? estimatedDaysToHarvest,
        int horizonDays)
    {
        var warnings = new List<string>();

        if (accumulated >= target)
        {
            return (referenceDate, false, warnings);
        }

        // Reutiliza GddDateLogic (núcleo de forecasting) con los valores del escenario.
        var crossing = GddDateLogic.EstimateCrossDate(
            accumulated,
            projection.Select(p => new DailyGddPoint(p.Date, p.Gdd, MapSource(p.Source))).ToList(),
            target);

        if (crossing is not null)
        {
            var extrapolation = crossing > referenceDate.AddDays(horizonDays);
            if (extrapolation)
            {
                warnings.Add($"La fecha por GDD ({crossing:yyyy-MM-dd}) queda más allá del horizonte simulado ({horizonDays} días): es extrapolación con el último GDD diario, no un pronóstico climático.");
            }

            return (crossing, extrapolation, warnings);
        }

        if (estimatedDaysToHarvest is int estDays && sowingDate is { } sowing)
        {
            var elapsed = (referenceDate.ToDateTime(TimeOnly.MinValue)
                           - sowing.ToDateTime(TimeOnly.MinValue)).Days;
            var remaining = Math.Max(0, estDays - elapsed);
            var fallbackDate = referenceDate.AddDays(remaining);
            warnings.Add($"Sin proyección GDD utilizable: fecha estimada por días de ciclo del cultivo (D+{remaining} desde la referencia), no por GDD proyectado.");
            return (fallbackDate, false, warnings);
        }

        return (null, false, warnings);
    }

    /// <summary>Días desde la fecha de referencia hasta la fecha destino.</summary>
    public static int? DaysRemaining(DateOnly referenceDate, DateOnly? target) =>
        target is { } t ? (t.ToDateTime(TimeOnly.MinValue) - referenceDate.ToDateTime(TimeOnly.MinValue)).Days : null;

    private static GddPointSource MapSource(SimulationGddSource source) => source switch
    {
        SimulationGddSource.Forecast => GddPointSource.Forecast,
        // Manual/HistoricReplay/Fallback: el cruce solo usa Date+Gdd, la fuente es etiqueta.
        _ => GddPointSource.Fallback
    };
}