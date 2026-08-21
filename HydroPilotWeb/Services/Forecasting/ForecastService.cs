using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Controllers;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Services.Forecasting;

/// <summary>
/// Orquesta el resultado común de forecast a nivel lote (F-01): GDD histórico y
/// proyectado, fecha de cosecha (híbrida Baby Leaf F-10 o fallback por GDD),
/// rendimiento, precisión, fenología (F-08), estados comerciales (F-09) y
/// persistencia idempotente de snapshots. Lo consumen el controller y la página
/// Razor con el MISMO DTO (API y UI no pueden divergir).
/// </summary>
public class ForecastService
{
    /// <summary>Versión del cálculo persistido (F-05: versionar y consultar por versión).</summary>
    public const string ModelVersion = "forecast-v2";

    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly GddService _gddService;
    private readonly YieldService _yieldService;
    private readonly WeatherService _weatherService;
    private readonly SettingsService _settings;
    private readonly ILogger<ForecastService> _logger;

    public ForecastService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        GddService gddService,
        YieldService yieldService,
        WeatherService weatherService,
        SettingsService settings,
        ILogger<ForecastService> logger)
    {
        _dbFactory = dbFactory;
        _gddService = gddService;
        _yieldService = yieldService;
        _weatherService = weatherService;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Lista de lotes para el dropdown (F-04): por defecto excluye cosechados y
    /// descartados de la proyección operativa. Mismo contrato para API y UI
    /// (F-01: la UI no consulta EF por un camino distinto al endpoint).
    /// </summary>
    public async Task<IReadOnlyList<LotSummary>> GetLotsAsync(bool includeClosed = false, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        IQueryable<Lot> query = context.Lots
            .Include(l => l.CropType)
            .Include(l => l.Status);

        if (!includeClosed)
        {
            query = query.Where(l => l.Status!.Name != LotStatusNames.Cosechado
                                     && l.Status!.Name != LotStatusNames.Descartado);
        }

        return await query
            .OrderByDescending(l => l.SowingDate)
            .Select(l => new LotSummary(
                l.Id,
                l.CropType!.Name,
                l.Status!.Name,
                l.SowingDate,
                l.PlantedAreaM2))
            .ToListAsync(ct);
    }

    public async Task<ForecastResult?> GetForecastAsync(
        int lotId,
        DateOnly? asOfDate = null,
        bool persistSnapshot = false,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();

        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var lot = await context.Lots
            .Include(l => l.CropType)
            .Include(l => l.Status)
            .FirstOrDefaultAsync(l => l.Id == lotId, ct);

        if (lot is null)
            return null;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var effectiveToday = asOfDate ?? today;
        var crop = lot.CropType;
        var baseTemp = crop?.BaseTemperature ?? 4.5m;
        var gddTarget = crop?.GddTarget ?? 300m;

        // --- GDD histórico (F-02) ---
        var history = await _gddService.GetGddHistoryAsync(lot, asOfDate, ct);
        var accumulated = history.Daily.Sum(p => p.Gdd);
        warnings.AddRange(history.Warnings);

        // --- Proyección (F-03) ---
        var projection = await _gddService.GetFutureGddProjectionAsync(lot, asOfDate, ct: ct);
        warnings.AddRange(projection.Warnings);

        var avgDaily = projection.Points.Count > 0 ? projection.Points.Average(p => p.Gdd) : 0m;

        // --- Fecha por GDD (fallback, F-03: extrapolación etiquetada) ---
        var gddHarvestDate = _gddService.EstimateHarvestDateAsync(lot, accumulated, projection.Points, effectiveToday);
        if (gddHarvestDate is { } gddDate
            && gddDate > effectiveToday.AddDays(_gddService.HorizonDays))
        {
            warnings.Add($"La fecha por GDD ({gddDate:yyyy-MM-dd}) queda más allá del horizonte de {_gddService.HorizonDays} días: es una extrapolación con el último GDD diario, no un pronóstico climático.");
        }

        // --- Fenología (F-08) ---
        var phenoStage = await PlantEvaluationService.ResolvePhenologicalStageAsync(context, lot.CropTypeId, accumulated, ct);

        // --- Estados comerciales y readiness híbrido (F-09/F-10) ---
        var plants = await context.Plants
            .Include(p => p.CommercialStage)
            .Where(p => p.LotId == lotId)
            .ToListAsync(ct);

        var activeCounts = plants
            .Where(p => p.OperationalState == PlantOperationalState.Activa)
            .GroupBy(p => p.CommercialStage?.Name ?? "(sin estado)")
            .ToDictionary(g => g.Key, g => g.Count());

        var (predominantName, isMixed) = Predominance.Resolve(activeCounts);
        var counts = activeCounts
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new StageCountDto(kv.Key, kv.Value))
            .ToList();

        // Regla híbrida de cosecha (F-10): evaluación propia dentro de la ventana,
        // sin consultar EF por un camino distinto al del resultado común.
        var harvestWindow = await BuildHarvestWindowAsync(
            context, lot, accumulated, activeCounts, plants, projections: projection.Points, ct);
        if (harvestWindow is not null)
            warnings.AddRange(harvestWindow.Warnings);

        // --- Fecha final estimada (F-10) ---
        DateOnly? finalHarvestDate;
        if (harvestWindow is { IsReady: true })
        {
            finalHarvestDate = effectiveToday; // listo para cosechar hoy
        }
        else if (harvestWindow is { GddInWindow: false, WindowEntryDate: not null } window)
        {
            finalHarvestDate = window.WindowEntryDate;
        }
        else
        {
            finalHarvestDate = null; // pendiente de decisión (razón visible en HarvestWindow)
        }

        finalHarvestDate ??= gddHarvestDate;

        // --- Rendimiento (F-05) ---
        var yieldEstimate = await _yieldService.EstimateAsync(lot, accumulated, ct);

        // --- Precisión (F-05, centralizada) ---
        var accuracy = await ComputeAccuracyAsync(ct);

        // --- Estado del lote cerrado: sin proyección operativa ---
        var statusName = lot.Status?.Name;
        if (statusName is LotStatusNames.Cosechado or LotStatusNames.Descartado)
        {
            warnings.Add($"Lote {statusName}: se muestra contexto histórico; sin proyección operativa ni fecha de cosecha recomendada.");
        }

        // --- Fuente del resultado (F-01) ---
        var dataSourceParts = new List<string>
        {
            $"sensor {history.CoverageDays}/{history.PeriodDays} días"
        };
        if (projection.Points.Any(p => p.Source == GddPointSource.Forecast))
            dataSourceParts.Add($"pronóstico {projection.Points.Count(p => p.Source == GddPointSource.Forecast)} días");
        if (projection.Points.Any(p => p.Source == GddPointSource.Fallback))
            dataSourceParts.Add($"fallback {projection.Points.Count(p => p.Source == GddPointSource.Fallback)} días");
        if (history.CoverageDays == 0 && history.PeriodDays > 0)
            dataSourceParts.Add("sin lecturas usables");

        var mockDetected = history.Warnings.Any(w => w.Contains("sintéticas", StringComparison.OrdinalIgnoreCase));
        var sourceKind = mockDetected
            ? ForecastSourceKind.Mock
            : projection.Points.Any(p => p.Source == GddPointSource.Fallback)
                ? ForecastSourceKind.Fallback
                : projection.Points.Any(p => p.Source == GddPointSource.Forecast)
                    ? ForecastSourceKind.Forecast
                    : ForecastSourceKind.Observed;

        // --- Persistencia idempotente (F-05) ---
        if (asOfDate is null)
        {
            // Snapshot de GDD del lote: propiedad de forecasting (plan 09, contrato).
            lot.AccumulatedGdd = Math.Round(accumulated, 2);
        }

        if (persistSnapshot)
        {
            await UpsertPredictionAsync(context, lot, effectiveToday, accumulated,
                finalHarvestDate, yieldEstimate.Base, string.Join(" + ", dataSourceParts),
                history.CoveragePercent, ct);
        }

        await context.SaveChangesAsync(ct);

        if (persistSnapshot)
        {
            _logger.LogInformation(
                "Forecast lote {LotId}: GDD {Gdd}/{Target}, cosecha {Harvest}, rendimiento {Yield} kg (asOf {AsOf})",
                lot.Id, Math.Round(accumulated, 2), gddTarget, finalHarvestDate, yieldEstimate.Base, effectiveToday);
        }

        return new ForecastResult(
            LotId: lot.Id,
            LotName: lot.Name,
            CropTypeName: crop?.Name ?? "Desconocido",
            StatusName: statusName,
            SowingDate: lot.SowingDate,
            AreaM2: lot.PlantedAreaM2,
            AsOfDate: effectiveToday,
            IsSimulation: asOfDate is not null && asOfDate != today,
            ModelVersion: ModelVersion,
            GddAccumulated: Math.Round(accumulated, 2),
            GddTarget: gddTarget,
            BaseTemperature: baseTemp,
            GddDailyAverage: Math.Round(avgDaily, 2),
            GddHistory: history.Daily,
            FutureProjection: projection.Points,
            ForecastHorizonDays: _gddService.HorizonDays,
            CoverageDays: history.CoverageDays,
            MissingDays: history.MissingDays,
            CoveragePercent: history.CoveragePercent,
            DataSourceSummary: string.Join(" + ", dataSourceParts),
            SourceKind: sourceKind,
            GddHarvestDate: gddHarvestDate,
            GddDaysRemaining: GddDateLogic.DaysRemaining(effectiveToday, gddHarvestDate),
            HarvestWindow: harvestWindow,
            EstimatedHarvestDate: finalHarvestDate,
            DaysRemaining: GddDateLogic.DaysRemaining(effectiveToday, finalHarvestDate),
            PhenologicalStageName: phenoStage?.Name,
            PhenologicalStageOrder: phenoStage?.Order,
            CommercialStageName: isMixed ? CommercialStageNames.Mixto : predominantName,
            IsCommercialStageMixed: isMixed,
            CommercialCounts: counts,
            Yield: yieldEstimate,
            AccuracyMape: accuracy.Mape,
            AccuracyDaysError: accuracy.DaysError,
            AccuracyCycles: accuracy.Cycles,
            ActualYieldKg: lot.ActualYieldKg,
            ActualHarvestDate: lot.ActualHarvestDate,
            Warnings: warnings.Distinct().ToList());
    }

    /// <summary>
    /// Ventana GDD + regla híbrida de cosecha (F-10). Baby Leaf: ventana
    /// configurada + % de aptas (umbral configurable; null = decisión pendiente
    /// visible). Convencional: ventana posterior con criterio de tamaño/madurez
    /// (análisis de imagen en fase futura → decisión pendiente, nunca inferida).
    /// </summary>
    private static async Task<HarvestWindowInfo?> BuildHarvestWindowAsync(
        HydroPilotDbContext context,
        Lot lot,
        decimal accumulated,
        IReadOnlyDictionary<string, int> activeCounts,
        IReadOnlyList<Plant> plants,
        IReadOnlyList<DailyGddPoint> projections,
        CancellationToken ct)
    {
        var warnings = new List<string>();

        var config = await context.BabyLeafConfigs
            .Where(c => c.CropTypeId == lot.CropTypeId && c.IsActive)
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync(ct);

        var lastStage = await context.PhenologicalStages
            .Where(s => s.CropTypeId == lot.CropTypeId && s.IsActive)
            .OrderByDescending(s => s.Order)
            .FirstOrDefaultAsync(ct);

        if (config is null)
        {
            warnings.Add("Sin configuración Baby Leaf activa para el cultivo: no se puede evaluar la ventana GDD ni la fecha híbrida.");
            return null;
        }

        var totalActive = activeCounts.Values.Sum();
        var aptaCount = activeCounts.TryGetValue(CommercialStageNames.BabyLeafApta, out var apta) ? apta : 0;
        var aptaPercent = totalActive > 0 ? Math.Round(aptaCount * 100m / totalActive, 1) : 0m;

        // ¿Dentro de la ventana convencional posterior (GddMax → sobremadurez)?
        var babyLeafWindowMax = config.GddMax;
        var overmaturity = lastStage?.GddMax ?? 750m;
        var inBabyLeafWindow = accumulated >= config.GddMin && accumulated < babyLeafWindowMax;
        var inConventionalWindow = !inBabyLeafWindow
                                   && accumulated >= babyLeafWindowMax
                                   && accumulated <= overmaturity;

        var babyLeafEntry = GddDateLogic.EstimateCrossDate(accumulated, projections, config.GddMin);
        var conventionalEntry = GddDateLogic.EstimateCrossDate(accumulated, projections, babyLeafWindowMax);

        if (inBabyLeafWindow)
        {
            var result = HarvestReadinessRules.Evaluate(new HarvestReadinessRules.Input(
                accumulated, config.GddMin, config.GddMax, aptaCount, totalActive, lot.BabyLeafHarvestTargetPercent));

            if (result.PendingReason is not null)
                warnings.Add(result.PendingReason);
            if (result.IsReady == false)
            {
                if (!result.GddInWindow)
                    warnings.Add($"GDD {accumulated} fuera de la ventana Baby Leaf ({config.GddMin}–{config.GddMax}).");
                if (lot.BabyLeafHarvestTargetPercent is { } target && result.AptaPercent < target)
                    warnings.Add($"Porcentaje de aptas ({result.AptaPercent}%) por debajo del objetivo ({target}%).");
            }

            return new HarvestWindowInfo(
                IsBabyLeaf: true,
                WindowMin: config.GddMin,
                WindowMax: config.GddMax,
                GddInWindow: result.GddInWindow,
                AptaCount: aptaCount,
                TotalActivePlants: totalActive,
                AptaPercent: result.AptaPercent,
                RequiredPercent: lot.BabyLeafHarvestTargetPercent,
                IsReady: result.IsReady,
                WindowEntryDate: babyLeafEntry,
                NotReadyReason: result.IsReady == false
                    ? string.Join("; ", warnings)
                    : result.PendingReason,
                Warnings: warnings);
        }

        var conventionalWarnings = new List<string>();
        if (inConventionalWindow)
        {
            // Criterio de tamaño/madurez: proviene del análisis de imagen (fase
            // futura). NO se infiere tamaño sin análisis persistido.
            var hasMaturityData = await context.PlantImageAnalyses
                .AnyAsync(a => a.PlantImage != null
                               && a.PlantImage.Plant != null
                               && a.PlantImage.Plant.LotId == lot.Id
                               && a.PlantImage.Plant.OperationalState == PlantOperationalState.Activa, ct);

            if (accumulated > overmaturity)
            {
                conventionalWarnings.Add($"GDD {accumulated} supera el umbral de sobremadurez ({overmaturity}): Riesgo / fuera de ventana.");
                return new HarvestWindowInfo(
                    IsBabyLeaf: false, babyLeafWindowMax, overmaturity, false,
                    0, totalActive, 0m, null, false, conventionalEntry,
                    "Fuera de ventana por sobremadurez.", conventionalWarnings);
            }

            if (!hasMaturityData)
            {
                conventionalWarnings.Add("Cosecha convencional: sin criterio de tamaño/madurez disponible (análisis de imagen pendiente): fecha híbrida sin decidir.");
            }

            return new HarvestWindowInfo(
                IsBabyLeaf: false, babyLeafWindowMax, overmaturity, true,
                0, totalActive, 0m, null,
                IsReady: null, // decisión pendiente: madurez no confirmada (análisis de imagen en fase futura)
                conventionalEntry,
                hasMaturityData
                    ? "Madurez no confirmada (sin decisión automática de tamaño): pendiente de revisión."
                    : "Sin criterio de tamaño/madurez disponible: decisión pendiente.",
                conventionalWarnings);
        }

        // GDD bajo la ventana Baby Leaf: aún en desarrollo.
        return new HarvestWindowInfo(
            IsBabyLeaf: true, config.GddMin, config.GddMax, false,
            aptaCount, totalActive, aptaPercent, lot.BabyLeafHarvestTargetPercent,
            null, babyLeafEntry,
            $"GDD {accumulated} por debajo de la ventana Baby Leaf ({config.GddMin}): fecha estimada de entrada {babyLeafEntry:yyyy-MM-dd}.",
            warnings);
    }

    /// <summary>
    /// Persistencia idempotente (F-05): una consulta repetida con la misma fecha
    /// de cálculo y versión ACTUALIZA la misma fila (índice único filtrado
    /// LotId+AsOfDate+ModelVersion); nunca duplica predicciones.
    /// </summary>
    private static async Task UpsertPredictionAsync(
        HydroPilotDbContext context,
        Lot lot,
        DateOnly asOfDate,
        decimal accumulated,
        DateOnly? harvestDate,
        decimal? estimatedYield,
        string dataSource,
        decimal? coveragePercent,
        CancellationToken ct)
    {
        var existing = await context.Predictions
            .FirstOrDefaultAsync(p => p.LotId == lot.Id
                                      && p.AsOfDate == asOfDate
                                      && p.ModelVersion == ModelVersion, ct);

        if (existing is null)
        {
            context.Predictions.Add(new Prediction
            {
                LotId = lot.Id,
                GeneratedAt = DateTime.UtcNow,
                AsOfDate = asOfDate,
                EstimatedHarvestDate = harvestDate,
                AccumulatedGdd = accumulated,
                EstimatedYield = estimatedYield,
                ModelVersion = ModelVersion,
                DataSource = dataSource,
                CoveragePercent = coveragePercent
            });
        }
        else
        {
            existing.GeneratedAt = DateTime.UtcNow;
            existing.EstimatedHarvestDate = harvestDate;
            existing.AccumulatedGdd = accumulated;
            existing.EstimatedYield = estimatedYield;
            existing.DataSource = dataSource;
            existing.CoveragePercent = coveragePercent;
        }
    }

    /// <summary>
    /// Precisión del modelo (F-05): última predicción válida anterior a la cosecha
    /// por lote; MAPE sin rendimiento real cero; error de días separado; ciclos
    /// elegibles informados. Centralizada (ForecastAccuracy) para API y UI.
    /// </summary>
    private async Task<AccuracyResult> ComputeAccuracyAsync(CancellationToken ct)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var closed = await context.Lots
            .Where(l => l.ActualYieldKg.HasValue || l.ActualHarvestDate.HasValue)
            .Select(l => new { l.Id, l.ActualYieldKg, l.ActualHarvestDate })
            .ToListAsync(ct);

        if (closed.Count == 0)
            return new AccuracyResult(null, null, 0);

        var closedIds = closed.Select(c => c.Id).ToList();
        var predictions = await context.Predictions
            .Where(p => closedIds.Contains(p.LotId))
            .Select(p => new { p.LotId, p.AsOfDate, p.GeneratedAt, p.EstimatedYield, p.EstimatedHarvestDate })
            .ToListAsync(ct);

        var cycles = closed
            .Select(c => new ClosedCycleInput(
                c.ActualYieldKg,
                c.ActualHarvestDate,
                predictions
                    .Where(p => p.LotId == c.Id)
                    .Select(p => new PredictionInput(p.AsOfDate, p.GeneratedAt, p.EstimatedYield, p.EstimatedHarvestDate))
                    .ToList()))
            .ToList();

        return ForecastAccuracy.Compute(cycles);
    }
}