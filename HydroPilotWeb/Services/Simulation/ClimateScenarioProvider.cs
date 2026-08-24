using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Forecasting;

namespace HydroPilotWeb.Services.Simulation;

/// <summary>
/// Plan climático del escenario (SIM-03): resuelve la proyección diaria de GDD
/// según el modo (manual / replay histórico / pronóstico) y reporta proveedor,
/// fecha de fetch, fallback y cobertura. La simulación NO llama a la API por cada
/// movimiento de slider: se invoca una vez por corrida y, en modo pronóstico,
/// reutiliza el fetch perezoso serializado con guard diario del forecasting
/// (WeatherService/GddService), idéntico al de la pantalla de forecasting.
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
    IReadOnlyList<string> Warnings);

/// <summary>
/// Proveedor de escenario climático. Los valores numéricos de GDD salen SIEMPRE
/// de GddService.DailyGdd (núcleo de forecasting); aquí solo se decide el origen
/// de las temperaturas del escenario y se etiquetan las fuentes.
/// </summary>
public class ClimateScenarioProvider
{
    /// <summary>Días observados usados como promedio para el replay histórico.</summary>
    private const int HistoricReplayObservedDays = 14;

    private readonly GddService _gddService;
    private readonly WeatherService _weatherService;

    public ClimateScenarioProvider(GddService gddService, WeatherService weatherService)
    {
        _gddService = gddService;
        _weatherService = weatherService;
    }

    public async Task<ClimatePlan> BuildAsync(
        SimulationClimateInput climate,
        Lot? lot,
        DateOnly referenceDate,
        decimal baseTemperature,
        CancellationToken ct = default)
    {
        var horizon = Math.Clamp(climate.ForecastHorizonDays ?? 7, 1, 30);
        var from = referenceDate.AddDays(1);
        var to = referenceDate.AddDays(horizon);
        var warnings = new List<string>();

        return climate.Mode switch
        {
            SimulationClimateMode.Manual => BuildManual(climate, baseTemperature, from, to, horizon, warnings),
            SimulationClimateMode.Historic => await BuildHistoricAsync(lot, referenceDate, from, to, horizon, warnings, ct),
            SimulationClimateMode.Forecast => await BuildForecastAsync(lot, referenceDate, baseTemperature, from, to, horizon, warnings, ct),
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

    private async Task<ClimatePlan> BuildHistoricAsync(
        Lot? lot,
        DateOnly referenceDate,
        DateOnly from,
        DateOnly to,
        int horizon,
        List<string> warnings,
        CancellationToken ct)
    {
        if (lot is null)
        {
            warnings.Add("Modo histórico sin lote: las lecturas de temperatura pertenecen a los sensores del invernadero; el escenario libre no tiene historial propio (no se inventa un promedio).");
            return new ClimatePlan([], "historico", null, false, horizon, 0, horizon, 0m, warnings);
        }

        // Replay histórico: promedio del GDD observado reciente (respeta AsOfDate).
        var average = await _gddService.GetRecentAverageDailyGddOrNullAsync(lot, referenceDate, HistoricReplayObservedDays, ct);
        if (average is null)
        {
            warnings.Add("Sin promedio de días observados para el replay histórico: la proyección del escenario queda sin datos (no se asume 0).");
            return new ClimatePlan([], "historico", null, false, horizon, 0, horizon, 0m, warnings);
        }

        warnings.Add($"Modo histórico: la proyección replica el promedio observado de los últimos {HistoricReplayObservedDays} días (fallback de replay); no es un pronóstico climático.");

        var points = new List<SimulationGddPoint>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            points.Add(new SimulationGddPoint(date, Math.Round(average.Value, 2), SimulationGddSource.HistoricReplay));
        }

        return new ClimatePlan(points, "historico", null, true, horizon, points.Count, 0, 100m, warnings);
    }

    private async Task<ClimatePlan> BuildForecastAsync(
        Lot? lot,
        DateOnly referenceDate,
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

        if (lot is not null)
        {
            // Reutiliza el núcleo de forecasting (DB primero + fetch perezoso
            // serializado con guard diario): la simulación NO duplica el fetch.
            var projection = await _gddService.GetFutureGddProjectionAsync(lot, referenceDate, horizon, ct);
            warnings.AddRange(projection.Warnings);

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
            // Escenario libre sin lote: mismo proveedor (DB del pronóstico), con el
            // fetch perezoso guardado del forecasting; sin lecturas propias no hay
            // fallback de sensor (se informa la falta, no se asume 0).
            await _weatherService.FetchForecastForDatesAsync(from, to, ct);
            var rows = await _weatherService.GetForecastForDatesAsync(from, to, ct);

            if (rows.Count == 0)
            {
                warnings.Add($"Sin pronóstico climático para {from:yyyy-MM-dd}..{to:yyyy-MM-dd}: la proyección del escenario queda sin datos (no se asume 0).");
            }
            else
            {
                fetchedAt = rows.Max(r => r.FetchedAt);
                if (DateOnly.FromDateTime(fetchedAt.Value) < referenceDate.AddDays(-3))
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
                        Math.Round(GddService.DailyGdd(tmax, tmin, baseTemperature), 2),
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
            warnings);
    }

    /// <summary>Fecha del último fetch del pronóstico persistido en el rango (solo lectura).</summary>
    private async Task<DateTime?> LastForecastFetchedAtAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await _weatherService.GetForecastForDatesAsync(from, to, ct);
        return rows.Count > 0 ? rows.Max(r => r.FetchedAt) : null;
    }

    private static SimulationGddSource MapSource(GddPointSource source) => source switch
    {
        GddPointSource.Forecast => SimulationGddSource.Forecast,
        GddPointSource.Fallback => SimulationGddSource.Fallback,
        _ => SimulationGddSource.HistoricReplay // Observed no ocurre en el rango futuro
    };
}