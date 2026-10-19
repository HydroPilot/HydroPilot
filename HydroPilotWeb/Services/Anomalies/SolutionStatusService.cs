using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Dashboard;
using HydroPilotWeb.Services.Forecasting;

namespace HydroPilotWeb.Services.Anomalies;

/// <summary>Rango de una magnitud de la solución y su objetivo.</summary>
public sealed record SolutionBand(decimal? Min, decimal? Max, decimal? Target)
{
    public bool IsDefined => Min is not null && Max is not null;
}

/// <summary>
/// Estado de pH o CE para actuar: en rango, subir, bajar, lectura sin confirmar (una sola o
/// contradictorias), sin lectura fresca, valor manual del lote, sin datos o sin rango.
/// </summary>
public enum SolutionState { InRange, Raise, Lower, Unconfirmed, Stale, Manual, NoData, NoBand }

public sealed record SolutionParameterStatus(
    string SensorType,
    string Unit,
    decimal? Value,
    DateTime? ObservedAtUtc,
    SolutionBand Band,
    SolutionState State,
    int UsableReadings)
{
    /// <summary>Fuera de rango con lecturas firmes (frescas y consistentes): se puede actuar.</summary>
    public bool IsFirmDeviation => State is SolutionState.Raise or SolutionState.Lower;
}

/// <summary>Solución nutritiva de un lote en curso: pH (rango del cultivo) y CE (rango de la etapa aplicada).</summary>
public sealed record LotSolutionStatus(
    int LotId,
    string LotName,
    string? StageName,
    SolutionParameterStatus Ph,
    SolutionParameterStatus Ec,
    int OpenEpisodes,
    bool HasCriticalEpisode);

/// <summary>
/// Reglas de la solución nutritiva (lógica pura). Mismos rangos que los chips de Lotes: pH del
/// cultivo; CE de la etapa fenológica aplicada del lote o, sin etapa, la del cultivo. El objetivo es
/// el del cultivo / la etapa o, si no está cargado, el centro del rango.
/// </summary>
public static class SolutionRules
{
    /// <summary>Lecturas seguidas que tienen que coincidir para que la desviación sea firme.</summary>
    public const int ConsistentReadings = 2;

    /// <summary>Lecturas recientes que se miran por magnitud.</summary>
    public const int LookbackReadings = 5;

    public static SolutionBand PhBand(CropType? crop) =>
        Band(crop?.OptimalPhMin, crop?.OptimalPhMax, crop?.OptimalPhTarget);

    public static SolutionBand EcBand(PhenologicalStage? stage, CropType? crop) =>
        stage is not null
            ? Band(stage.EcMin, stage.EcMax, stage.EcObjective > 0 ? stage.EcObjective : null)
            : Band(crop?.OptimalEcMin, crop?.OptimalEcMax, null);

    private static SolutionBand Band(decimal? min, decimal? max, decimal? target) =>
        new(min, max, target ?? (min is { } lo && max is { } hi ? Math.Round((lo + hi) / 2m, 2) : null));

    public static string Direction(decimal value, SolutionBand band) =>
        band.Min is { } min && value < min ? Models.Optimization.OptimizationContractDirection.Raise
        : band.Max is { } max && value > max ? Models.Optimization.OptimizationContractDirection.Lower
        : Models.Optimization.OptimizationContractDirection.Maintain;

    /// <summary>
    /// Estado de una magnitud con sus lecturas usables: sin lecturas, el valor manual del lote (si hay);
    /// con lecturas viejas, "sin lectura fresca"; frescas, la dirección si las últimas coinciden.
    /// </summary>
    public static SolutionParameterStatus Evaluate(
        string sensorType,
        string unit,
        IReadOnlyList<SolutionReading> readings,
        decimal? manualValue,
        SolutionBand band,
        DateTime nowUtc,
        int freshMinutes)
    {
        var evidence = ReadingEvidence.Evaluate(
            readings, sensorType, v => Direction(v, band), nowUtc, freshMinutes, ConsistentReadings);

        if (evidence.UsableCount == 0)
        {
            var state = manualValue is null ? SolutionState.NoData : SolutionState.Manual;
            return new SolutionParameterStatus(sensorType, unit, manualValue, null, band, state, 0);
        }

        var value = evidence.LatestValue!.Value;
        SolutionState result;
        if (!band.IsDefined) result = SolutionState.NoBand;
        else if (!evidence.HasFreshReading) result = SolutionState.Stale;
        else if (!evidence.IsFirm) result = SolutionState.Unconfirmed;
        else
        {
            result = Direction(value, band) switch
            {
                Models.Optimization.OptimizationContractDirection.Raise => SolutionState.Raise,
                Models.Optimization.OptimizationContractDirection.Lower => SolutionState.Lower,
                _ => SolutionState.InRange,
            };
        }

        return new SolutionParameterStatus(sensorType, unit, value, evidence.LatestObservedAtUtc, band, result, evidence.UsableCount);
    }
}

/// <summary>
/// pH y CE de cada lote en curso, para Anomalías (qué corregir) y Optimización (cuánto cuesta no
/// corregirlo). La lectura es fresca con la misma ventana que Lotes (DashboardOptions.KpiFreshnessMinutes).
/// </summary>
public class SolutionStatusService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly IOptions<DashboardOptions> _dashboardOptions;

    public SolutionStatusService(IDbContextFactory<HydroPilotDbContext> dbFactory, IOptions<DashboardOptions> dashboardOptions)
    {
        _dbFactory = dbFactory;
        _dashboardOptions = dashboardOptions;
    }

    /// <summary>Lotes en curso (Activo / En pausa), más recientes primero.</summary>
    public async Task<IReadOnlyList<LotSolutionStatus>> GetOpenLotsAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var ids = await context.Lots.AsNoTracking()
            .Where(l => l.Status != null && (l.Status.Name == LotStatusNames.Activo || l.Status.Name == LotStatusNames.EnPausa))
            .OrderByDescending(l => l.SowingDate)
            .Select(l => l.Id)
            .ToListAsync(ct);

        var result = new List<LotSolutionStatus>(ids.Count);
        foreach (var id in ids)
        {
            if (await BuildAsync(context, id, ct) is { } status)
                result.Add(status);
        }
        return result;
    }

    public async Task<LotSolutionStatus?> GetLotAsync(int lotId, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        return await BuildAsync(context, lotId, ct);
    }

    private async Task<LotSolutionStatus?> BuildAsync(HydroPilotDbContext context, int lotId, CancellationToken ct)
    {
        var lot = await context.Lots.AsNoTracking()
            .Include(l => l.CropType)
            .Include(l => l.AppliedPhenologicalStage)
            .FirstOrDefaultAsync(l => l.Id == lotId, ct);
        if (lot is null)
            return null;

        var nowUtc = DateTime.UtcNow;
        var freshMinutes = Math.Max(1, _dashboardOptions.Value.KpiFreshnessMinutes);

        var ph = await ReadingsAsync(context, lotId, AnomalyContract.SensorTypePh, ct);
        var ec = await ReadingsAsync(context, lotId, AnomalyContract.SensorTypeCe, ct);

        var episodes = await context.AnomalyEvents.AsNoTracking()
            .Where(e => e.LotId == lotId
                        && (e.Status == AnomalyContract.StatusAbierta || e.Status == AnomalyContract.StatusReconocida))
            .Select(e => e.Severity)
            .ToListAsync(ct);

        // Objetivos de la etapa corridos por la receta aprendida del lote (fase 5), los mismos que usa el control.
        var recipe = await Control.RecipeService.CurrentAsync(context, lotId, ct);

        return new LotSolutionStatus(
            lot.Id,
            string.IsNullOrWhiteSpace(lot.Name) ? $"Lote #{lot.Id}" : lot.Name!,
            lot.AppliedPhenologicalStage?.Name,
            SolutionRules.Evaluate(AnomalyContract.SensorTypePh, "pH", ph, lot.CurrentPh,
                Control.RecipeRules.Shift(SolutionRules.PhBand(lot.CropType), recipe?.PhOffset ?? 0m), nowUtc, freshMinutes),
            SolutionRules.Evaluate(AnomalyContract.SensorTypeCe, "mS/cm", ec, lot.CurrentEc,
                Control.RecipeRules.Shift(SolutionRules.EcBand(lot.AppliedPhenologicalStage, lot.CropType), recipe?.EcOffset ?? 0m),
                nowUtc, freshMinutes),
            episodes.Count,
            episodes.Any(s => s == AnomalyContract.SeverityCritica));
    }

    private static async Task<IReadOnlyList<SolutionReading>> ReadingsAsync(
        HydroPilotDbContext context, int lotId, string sensorType, CancellationToken ct) =>
        await context.SensorReadings.AsNoTracking()
            .Where(TelemetryQualityPolicy.OperationallyUsableReading)
            .Where(r => r.LotId == lotId
                        && r.Sensor != null && r.Sensor.SensorType != null
                        && r.Sensor.SensorType.Name == sensorType)
            .OrderByDescending(r => r.ObservedAtUtc)
            .Take(SolutionRules.LookbackReadings)
            .Select(r => new SolutionReading(sensorType, r.Quality, r.Value, r.ObservedAtUtc))
            .ToListAsync(ct);
}
