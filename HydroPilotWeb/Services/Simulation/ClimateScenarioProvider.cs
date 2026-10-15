using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Forecasting;

namespace HydroPilotWeb.Services.Simulation;

/// <summary>
/// Plan climático del escenario (SIM-03): resuelve la proyección diaria de GDD
/// según el modo (manual / pronóstico) y reporta proveedor, fecha de fetch,
/// fallback y cobertura. Se invoca una vez por corrida y, en modo pronóstico con
/// lote, usa la MISMA proyección que la vista Lotes (GddService: pronóstico y,
/// los días sin pronóstico, el promedio de los últimos 7 días medidos).
/// </summary>
public sealed record ClimatePlan(
    IReadOnlyList<SimulationGddPoint> Points,
    string ProviderName,
    DateTime? FetchedAtUtc,
    bool UsedFallback,
    int HorizonDays,
    int CoveredDays,
    int MissingDays,
    decimal? CoveragePercent,
    IReadOnlyList<string> Warnings,
    ForecastCorrection? Correction = null);

/// <summary>
/// Proveedor de escenario climático. Los valores numéricos de GDD salen SIEMPRE
/// de GddService.DailyGdd (núcleo de forecasting); aquí solo se decide el origen
/// de las temperaturas del escenario y se etiquetan las fuentes.
/// </summary>
public class ClimateScenarioProvider
{
    private readonly GddService _gddService;
    private readonly WeatherService _weatherService;

    public ClimateScenarioProvider(GddService gddService, WeatherService weatherService)
    {
        _gddService = gddService;
        _weatherService = weatherService;
    }

    /// <summary>
    /// GDD del escenario LIBRE de la siembra a hoy, con el clima elegido (con lote, el historial
    /// real lo da GddService). Manual: cada día suma el GDD de Tmin/Tmax. Pronóstico: el guardado
    /// para esos días (el pasado no se descarga), corregido a la temperatura de adentro; los días
    /// sin pronóstico no suman y se avisa. Siembra futura: todavía no hay días.
    /// </summary>
    public async Task<(IReadOnlyList<DailyGddPoint> Daily, IReadOnlyList<string> Warnings)> FreeHistoryAsync(
        SimulationClimateInput climate,
        decimal baseTemperature,
        DateOnly sowingDate,
        DateOnly today,
        ForecastCorrection? correction = null,
        CancellationToken ct = default)
    {
        if (sowingDate > today)
            return ([], []);

        if (climate.Mode == SimulationClimateMode.Manual)
            return (GddCalculator.ManualHistory(climate, baseTemperature, sowingDate, today), []);

        var rows = await _weatherService.GetForecastForDatesAsync(sowingDate, today, ct);
        var daily = rows
            .Select(row => new DailyGddPoint(
                row.Date,
                Math.Round(GddService.CorrectedForecastGdd(
                    GddService.DailyGdd(Math.Max(row.TempMin, row.TempMax), Math.Min(row.TempMin, row.TempMax), baseTemperature),
                    correction), 2),
                GddPointSource.Forecast))
            .ToList();

        var days = today.DayNumber - sowingDate.DayNumber + 1;
        IReadOnlyList<string> warnings = daily.Count < days
            ? [$"{days - daily.Count} de {days} días desde la siembra sin pronóstico guardado: no suman GDD (para contarlos, usá clima manual)."]
            : [];
        return (daily, warnings);
    }

    /// <summary>
    /// Proyección del clima elegido desde <paramref name="from"/> (mañana, o la siembra si es
    /// futura) durante el horizonte; después, CycleTimelineLogic extrapola con el último día.
    /// Con lote, el pronóstico ya viene corregido por GddService (como en Lotes); sin lote se aplica
    /// <paramref name="freeCorrection"/>.
    /// </summary>
    public async Task<ClimatePlan> BuildAsync(
        SimulationClimateInput climate,
        Lot? lot,
        DateOnly from,
        decimal baseTemperature,
        GddHistoryResult? lotHistory = null,
        ForecastCorrection? freeCorrection = null,
        CancellationToken ct = default)
    {
        var horizon = Math.Clamp(climate.ForecastHorizonDays ?? 7, 1, 30);
        var to = from.AddDays(horizon - 1);
        var warnings = new List<string>();

        return climate.Mode switch
        {
            SimulationClimateMode.Manual => BuildManual(climate, baseTemperature, from, to, horizon, warnings),
            SimulationClimateMode.Forecast => await BuildForecastAsync(lot, lotHistory, freeCorrection, baseTemperature, from, to, horizon, warnings, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(climate.Mode))
        };
    }

    private static ClimatePlan BuildManual(
        SimulationClimateInput climate,
        decimal baseTemperature,
        DateOnly from,
        DateOnly to,
        int horizon,
        List<string> warnings)
    {
        var points = GddCalculator.ManualPoints(climate, baseTemperature, from, to);
        if (points.Count == 0)
        {
            warnings.Add("Modo manual sin temperaturas válidas: no hay proyección de GDD para el escenario.");
            return new ClimatePlan([], "manual", null, false, horizon, 0, horizon, 0m, warnings);
        }

        return new ClimatePlan(points, "manual", null, false, horizon, points.Count, 0, 100m, warnings);
    }

    private async Task<ClimatePlan> BuildForecastAsync(
        Lot? lot,
        GddHistoryResult? lotHistory,
        ForecastCorrection? freeCorrection,
        decimal baseTemperature,
        DateOnly from,
        DateOnly to,
        int horizon,
        List<string> warnings,
        CancellationToken ct)
    {
        var points = new List<SimulationGddPoint>();
        DateTime? fetchedAt = null;
        var usedFallback = false;
        var correction = lot is null ? freeCorrection : null;

        if (lot is not null)
        {
            // La misma proyección que la vista Lotes (DB primero + fetch perezoso serializado con
            // guard diario; días sin pronóstico: promedio de los últimos 7 días medidos).
            var projection = await _gddService.GetFutureGddProjectionAsync(lot, from.AddDays(-1), horizon, ct, lotHistory);
            warnings.AddRange(projection.Warnings);
            correction = projection.Correction;

            foreach (var point in projection.Points)
            {
                // El núcleo rellena con 0 los días sin ningún dato: no se convierten
                // en medición del escenario (se filtran y cuentan como faltantes).
                if (point.Source == GddPointSource.Fallback && point.Gdd == 0m)
                    continue;

                points.Add(new SimulationGddPoint(
                    point.Date, Math.Round(point.Gdd, 2), MapSource(point.Source)));
                usedFallback |= point.Source == GddPointSource.Fallback;
            }

            if (projection.HasForecastData)
            {
                fetchedAt = await LastForecastFetchedAtAsync(from, to, ct);
            }
        }
        else
        {
            // Escenario libre sin lote: mismo proveedor (DB del pronóstico, fetch como mucho una
            // vez por día); sin lecturas propias no hay promedio de respaldo (se informa la falta).
            if (!await _weatherService.WasFetchedTodayAsync(ct))
                await _weatherService.FetchForecastForDatesAsync(from, to, ct);
            var rows = await _weatherService.GetForecastForDatesAsync(from, to, ct);

            if (rows.Count == 0)
            {
                warnings.Add($"Sin pronóstico climático para {from:yyyy-MM-dd}..{to:yyyy-MM-dd}: la proyección del escenario queda sin datos (no se asume 0); usá clima manual.");
            }
            else
            {
                fetchedAt = rows.Max(r => r.FetchedAt);
                if (DateOnly.FromDateTime(fetchedAt.Value) < from.AddDays(-4))
                {
                    warnings.Add($"Pronóstico vencido (último fetch {fetchedAt:yyyy-MM-dd}): puede estar desactualizado.");
                }

                foreach (var row in rows)
                {
                    var (tmin, tmax) = (row.TempMin, row.TempMax);
                    if (tmin > tmax)
                    {
                        warnings.Add($"Pronóstico {row.Date:yyyy-MM-dd} con Tmin {tmin} > Tmax {tmax}: se intercambian y se continúa (validación del contrato forecasting).");
                        (tmin, tmax) = (tmax, tmin);
                    }

                    points.Add(new SimulationGddPoint(
                        row.Date,
                        Math.Round(GddService.CorrectedForecastGdd(GddService.DailyGdd(tmax, tmin, baseTemperature), correction), 2),
                        SimulationGddSource.Forecast));
                }
            }
        }

        var covered = points.Count;
        var missing = Math.Max(0, horizon - covered);
        if (missing > 0)
        {
            warnings.Add($"Cobertura del clima del escenario {covered}/{horizon} días ({from:yyyy-MM-dd}..{to:yyyy-MM-dd}).");
        }

        var coverage = horizon > 0 ? (decimal?)Math.Round(covered * 100m / horizon, 1) : null;

        return new ClimatePlan(
            points.OrderBy(p => p.Date).ToList(),
            "pronostico",
            fetchedAt,
            usedFallback,
            horizon,
            covered,
            missing,
            coverage,
            warnings,
            correction);
    }

    /// <summary>Fecha del último fetch del pronóstico persistido en el rango (solo lectura).</summary>
    private async Task<DateTime?> LastForecastFetchedAtAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await _weatherService.GetForecastForDatesAsync(from, to, ct);
        return rows.Count > 0 ? rows.Max(r => r.FetchedAt) : null;
    }

    private static SimulationGddSource MapSource(GddPointSource source) =>
        source == GddPointSource.Forecast ? SimulationGddSource.Forecast : SimulationGddSource.Fallback;
}