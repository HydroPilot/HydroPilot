using HydroPilotWeb.Models;

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
