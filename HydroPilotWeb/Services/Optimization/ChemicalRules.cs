using HydroPilotWeb.Models.Optimization;

namespace HydroPilotWeb.Services.Optimization;

/// <summary>
/// Reglas químicas de optimización (OPT-02/OPT-07): evaluar pH contra los
/// límites absolutos del cultivo (CropType.OptimalPhMin/Target/Max — NUNCA una
/// regla porcentual) y CE contra la etapa fenológica del lote
/// (PhenologicalStage.EcObjective con EcMin/EcMax). Devuelve dirección
/// subir/bajar/mantener/verificar con explicación y confianza según la
/// evidencia de lecturas. Lógica pura, no calcula dosis exactas (falta volumen,
/// concentración, receta y curva de respuesta).
/// </summary>
public static class ChemicalRules
{
    /// <summary>Límites de pH del cultivo.</summary>
    public sealed record PhBounds(decimal? Min, decimal? Target, decimal? Max);

    /// <summary>Rango de EC de la etapa fenológica aplicada al lote.</summary>
    public sealed record EcBounds(decimal? Min, decimal? Objective, decimal? Max);

    public const string NoPhRangeWarning =
        "El cultivo no tiene rango de pH configurado (OptimalPhMin/OptimalPhMax): no se puede evaluar pH.";
    public const string NoEcStageWarning =
        "Sin etapa fenológica resuelta para el lote: el objetivo de EC (receta por etapa) no está disponible.";

    /// <summary>Dirección de un valor de pH contra los límites del cultivo.</summary>
    public static string DirectionForPh(decimal value, PhBounds bounds)
    {
        if (bounds.Min is { } min && value < min) return OptimizationContractDirection.Raise;
        if (bounds.Max is { } max && value > max) return OptimizationContractDirection.Lower;
        return OptimizationContractDirection.Maintain;
    }

    /// <summary>Dirección de un valor de CE contra el rango de la etapa.</summary>
    public static string DirectionForEc(decimal value, EcBounds bounds)
    {
        if (bounds.Min is { } min && value < min) return OptimizationContractDirection.Raise;
        if (bounds.Max is { } max && value > max) return OptimizationContractDirection.Lower;
        return OptimizationContractDirection.Maintain;
    }

    /// <summary>Objetivo de pH que se expone (target o borde más cercano).</summary>
    public static decimal? PhTargetForValue(decimal value, PhBounds bounds)
    {
        if (bounds.Target is { } target) return target;
        if (bounds.Min is { } min && value < min) return min;
        if (bounds.Max is { } max && value > max) return max;
        return null;
    }

    /// <summary>Objetivo de EC (objetivo de la etapa, o borde más cercano).</summary>
    public static decimal? EcTargetForValue(decimal value, EcBounds bounds)
    {
        if (bounds.Objective is { } obj) return obj;
        if (bounds.Min is { } min && value < min) return min;
        if (bounds.Max is { } max && value > max) return max;
        return null;
    }

    /// <summary>Etiqueta legible del rango objetivo de pH.</summary>
    public static string PhRangeLabel(PhBounds bounds)
    {
        var min = bounds.Min?.ToString("0.0#") ?? "—";
        var max = bounds.Max?.ToString("0.0#") ?? "—";
        return $"pH {min}–{max}";
    }

    /// <summary>Etiqueta legible del rango de EC de la etapa.</summary>
    public static string EcRangeLabel(EcBounds bounds, string? stageName)
    {
        var min = bounds.Min?.ToString("0.0#") ?? "—";
        var max = bounds.Max?.ToString("0.0#") ?? "—";
        return stageName is null ? $"EC {min}–{max} mS/cm" : $"EC {min}–{max} mS/cm (etapa {stageName})";
    }

    /// <summary>
    /// Evalúa pH con la evidencia de lecturas: bloquea datos inválidos/antiguos,
    /// calcula dirección, objetivo y confianza (desbalance de 1 lectura vs dos
    /// consistentes). Evaluable = false cuando falta el rango del cultivo.
    /// </summary>
    public static ChemicalAssessment EvaluatePh(
        IReadOnlyList<SnapshotReading> readings,
        PhBounds bounds,
        DateTime nowUtc,
        int maxAgeMinutes,
        int consistentCount)
    {
        var warnings = new List<string>();
        if (bounds.Min is null || bounds.Max is null)
        {
            warnings.Add(NoPhRangeWarning);
            return new ChemicalAssessment(false, OptimizationContractDirection.Verify, null, null, null,
                "pH no evaluable: " + NoPhRangeWarning, 0, warnings);
        }

        var evidence = ReadingEvidence.Evaluate(
            readings, "pH", v => DirectionForPh(v, bounds), nowUtc, maxAgeMinutes, consistentCount);
        return BuildAssessment("pH", "pH", evidence, bounds,
            (v, b) => PhTargetForValue(v, (PhBounds)b),
            (v, b) => DirectionForPh(v, (PhBounds)b),
            PhRangeLabel(bounds), warnings);
    }

    /// <summary>
    /// Evalúa CE contra la etapa fenológica del lote (OPT-07): objetivo por
    /// etapa, dirección y confianza. Evaluable = false cuando no hay etapa.
    /// </summary>
    public static ChemicalAssessment EvaluateEc(
        IReadOnlyList<SnapshotReading> readings,
        EcBounds? bounds,
        string? stageName,
        DateTime nowUtc,
        int maxAgeMinutes,
        int consistentCount)
    {
        var warnings = new List<string>();
        if (bounds is null || bounds.Objective is null)
        {
            warnings.Add(NoEcStageWarning);
            return new ChemicalAssessment(false, OptimizationContractDirection.Verify, null, null, null,
                "CE no evaluable: " + NoEcStageWarning, 0, warnings);
        }

        var evidence = ReadingEvidence.Evaluate(
            readings, "CE", v => DirectionForEc(v, bounds), nowUtc, maxAgeMinutes, consistentCount);
        return BuildAssessment("CE", "CE", evidence, bounds,
            (v, b) => EcTargetForValue(v, (EcBounds)b),
            (v, b) => DirectionForEc(v, (EcBounds)b),
            EcRangeLabel(bounds, stageName), warnings);
    }

    private static ChemicalAssessment BuildAssessment(
        string magnitude,
        string displayName,
        ReadingEvidence.Result evidence,
        object bounds,
        Func<decimal, object, decimal?> targetFor,
        Func<decimal, object, string> directionFor,
        string rangeLabel,
        List<string> warnings)
    {
        if (!evidence.HasFreshReading)
        {
            var reason = evidence.BlockReason ?? "Sin lectura utilizable.";
            warnings.Add(reason);
            return new ChemicalAssessment(false, OptimizationContractDirection.Verify,
                evidence.LatestValue, null, rangeLabel,
                $"{displayName}: {reason}", 0, warnings);
        }

        var latest = evidence.LatestValue!.Value;
        var target = targetFor(latest, bounds);
        var direction = directionFor(latest, bounds);

        if (!evidence.IsSingle && !evidence.IsConsistent)
        {
            // Lecturas consecutivas contradictorias → verificar discrepancia.
            direction = OptimizationContractDirection.Verify;
            warnings.Add(
                $"Las últimas {evidence.UsableReadings.Count} lecturas de {displayName} no son consistentes " +
                $"({string.Join(", ", evidence.PerReadingDirections)}): se recomienda verificar el sensor.");
        }

        var explanation = direction switch
        {
            OptimizationContractDirection.Raise =>
                $"{displayName} actual {latest:0.0#} está por debajo del rango objetivo {rangeLabel}: subir hacia {target:0.0#}.",
            OptimizationContractDirection.Lower =>
                $"{displayName} actual {latest:0.0#} está por encima del rango objetivo {rangeLabel}: bajar hacia {target:0.0#}.",
            OptimizationContractDirection.Verify => $"{displayName} requiere verificación: {DiscrepancyNote(evidence, displayName)}.",
            _ => $"{displayName} actual {latest:0.0#} está dentro del rango objetivo {rangeLabel}.",
        };

        if (evidence.IsSingle)
            explanation += " Una sola lectura: confianza baja, verificar con una segunda lectura.";

        return new ChemicalAssessment(
            Evaluable: true,
            direction,
            latest,
            target,
            rangeLabel,
            explanation,
            evidence.ConfidencePercent,
            warnings);
    }

    private static string DiscrepancyNote(ReadingEvidence.Result evidence, string displayName) =>
        evidence.IsSingle
            ? $"una sola lectura de {displayName} disponible (confianza baja)."
            : $"lecturas consecutivas de {displayName} con direcciones opuestas ({string.Join(", ", evidence.PerReadingDirections)}).";
}