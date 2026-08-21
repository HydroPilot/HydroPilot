using HydroPilotWeb.Models;

namespace HydroPilotWeb.Services.Lotes;

/// <summary>
/// Calculador de referencia del BabyLeafScore a partir de los criterios del
/// catálogo (LOT-02). Implementa solo la SEMÁNTICA ponderada documentada:
/// - el criterio GDD se evalúa contra la ventana de la configuración;
/// - el resto de los criterios reciben un score normalizado 0-100 del pipeline
///   de métricas (fase futura de visión computacional; hoy se entrega explícito);
/// - el score final es el promedio ponderado sobre los criterios evaluados;
/// - los criterios obligatorios deben estar evaluados Y aprobados.
///
/// Valores de arranque a calibrar con datos reales. La normalización de métricas
/// visuales (morfología, crecimiento, estado visual) NO se implementa acá: queda
/// pendiente de la fase de procesamiento de imágenes.
/// </summary>
public static class BabyLeafScoreCalculator
{
    public const string DataTypeGdd = "GDD";

    public sealed record CriterionInput(
        string Name,
        string DataType,
        decimal Weight,
        bool IsMandatory,
        decimal? Score,   // 0-100; null = sin dato
        bool Passed);

    public sealed record ScoreResult(
        decimal? WeightedScore,   // null si ningún criterio pudo evaluarse
        bool AllMandatoryMet,     // false si un obligatorio falta o falló
        IReadOnlyList<CriterionInput> Evaluated);

    public static ScoreResult Calculate(
        IReadOnlyList<CriterionInput> criteria,
        decimal lotGdd,
        decimal gddWindowMin,
        decimal gddWindowMax)
    {
        // 1:1 con los criterios de entrada (los sin dato quedan en la lista para
        // que el chequeo de obligatorios los vea como no aprobados).
        var normalized = new List<CriterionInput>(criteria.Count);
        foreach (var criterion in criteria)
        {
            if (string.Equals(criterion.DataType, DataTypeGdd, StringComparison.OrdinalIgnoreCase))
            {
                // Ventana GDD: la regla documentada es binaria (dentro/fuera de ventana).
                var inWindow = lotGdd >= gddWindowMin && lotGdd < gddWindowMax;
                normalized.Add(criterion with { Score = inWindow ? 100m : 0m, Passed = inWindow });
                continue;
            }

            if (criterion.Score is not null)
            {
                normalized.Add(criterion with { Passed = criterion.Passed && criterion.Score >= 0 && criterion.Score <= 100 });
            }
            else
            {
                normalized.Add(criterion); // sin dato: no suma peso y no aprueba
            }
        }

        var evaluated = normalized.Where(c => c.Score is not null).ToList();
        if (evaluated.Count == 0)
            return new ScoreResult(null, false, evaluated);

        decimal weightedSum = 0m, totalWeight = 0m;
        foreach (var item in evaluated)
        {
            weightedSum += (item.Score ?? 0m) * item.Weight;
            totalWeight += item.Weight;
        }

        var score = totalWeight > 0 ? (decimal?)Math.Round(weightedSum / totalWeight, 1) : null;
        var mandatoryOk = normalized.Where(c => c.IsMandatory).All(c => c.Passed);

        return new ScoreResult(score, mandatoryOk, evaluated);
    }

    /// <summary>Convierte criterios del catálogo en entradas del cálculo (sin métricas: Score null).</summary>
    public static IReadOnlyList<CriterionInput> FromCatalog(
        IEnumerable<BabyLeafCriterion> criteria,
        IReadOnlyDictionary<int, decimal> metricScoresById)
    {
        var result = new List<CriterionInput>();
        foreach (var c in criteria.Where(c => c.IsActive))
        {
            if (!metricScoresById.TryGetValue(c.Id, out var metric))
            {
                result.Add(new CriterionInput(c.Name, c.DataType, c.Weight, c.IsMandatory, null, false));
                continue;
            }

            // Pasa si está dentro de los umbrales del criterio (si los tiene).
            var passes = metric >= (c.ValueMin ?? decimal.MinValue)
                         && metric <= (c.ValueMax ?? decimal.MaxValue);
            result.Add(new CriterionInput(c.Name, c.DataType, c.Weight, c.IsMandatory, metric, passes));
        }
        return result;
    }
}