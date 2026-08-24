using HydroPilotWeb.Models.Optimization;

namespace HydroPilotWeb.Services.Optimization;

/// <summary>
/// Reglas comerciales agregadas del lote (OPT-08/OPT-09): porcentaje de plantas
/// aptas sobre activas, estados comerciales (incluido Mixto), ventana GDD y
/// plantas en riesgo. Una planta en Riesgo / fuera de ventana NO descarta el
/// lote: se muestra su cantidad y cómo afecta el resultado (el porcentaje de
/// aptas puede sobreestimar la cosecha). Lógica pura, testeable sin I/O.
/// </summary>
public static class CommercialRules
{
    public sealed record Input(
        int ActivePlants,
        int AptaCount,
        int RiskCount,
        decimal AptaPercent,
        bool IsCommercialMixed,
        decimal? AptaTargetPercent,
        decimal Gdd,
        decimal? WindowMin,
        decimal? WindowMax);

    public sealed record Result(
        bool HasFinding,
        string Direction,
        string Priority,
        string Explanation,
        IReadOnlyList<string> Details,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// Evalúa el agregado comercial. Tiene hallazgo cuando hay plantas en
    /// riesgo, porcentaje de aptas por debajo del objetivo, empate (Mixto) o
    /// ventana GDD sin configuración Baby Leaf (decisión pendiente visible).
    /// </summary>
    public static Result Evaluate(Input input)
    {
        var warnings = new List<string>();
        var details = new List<string>
        {
            $"{input.AptaCount}/{input.ActivePlants} plantas activas aptas ({input.AptaPercent:0.#}%)",
        };

        if (input.IsCommercialMixed)
            details.Add("Empate de estados comerciales: el lote se muestra como 'Mixto' (no se afirma un estado predominante).");

        // OPT-09: riesgo nunca descarta el lote; se cuantifica y se explica el efecto.
        if (input.RiskCount > 0)
        {
            details.Add($"{input.RiskCount} planta(s) en 'Riesgo / fuera de ventana'.");
            warnings.Add(
                $"Hay {input.RiskCount} planta(s) en riesgo: el porcentaje de aptas puede sobreestimar la cosecha " +
                "si esas plantas debían aportar al conteo. No se descarta el lote automáticamente.");
        }

        // Ventana GDD sin configuración Baby Leaf activa: decisión pendiente visible.
        if (input.WindowMin is null || input.WindowMax is null)
        {
            warnings.Add("Sin configuración Baby Leaf activa para el cultivo: la ventana GDD de cosecha no puede evaluarse.");
            details.Add("Ventana GDD: sin configuración (decisión pendiente).");
        }
        else if (input.Gdd < input.WindowMin)
        {
            details.Add($"GDD {input.Gdd:0.#} por debajo de la ventana Baby Leaf ({input.WindowMin:0.#}–{input.WindowMax:0.#}).");
        }
        else if (input.Gdd <= input.WindowMax)
        {
            details.Add($"GDD {input.Gdd:0.#} dentro de la ventana Baby Leaf ({input.WindowMin:0.#}–{input.WindowMax:0.#}).");
        }
        else
        {
            details.Add($"GDD {input.Gdd:0.#} más allá de la ventana Baby Leaf ({input.WindowMin:0.#}–{input.WindowMax:0.#}): hacia sobremadurez.");
        }

        // Objetivo de aptas configurable (BabyLeafHarvestTargetPercent nullable).
        if (input.AptaTargetPercent is { } target)
        {
            if (input.AptaPercent < target)
                details.Add($"Porcentaje de aptas ({input.AptaPercent:0.#}%) por debajo del objetivo configurado ({target:0.#}%).");
            else
                details.Add($"Porcentaje de aptas ({input.AptaPercent:0.#}%) alcanza el objetivo ({target:0.#}%).");
        }
        else
        {
            details.Add("Objetivo de aptas del lote sin configurar: decisión pendiente (no se reemplaza por una constante).");
        }

        var hasFinding = input.RiskCount > 0
            || (input.AptaTargetPercent is { } t && input.AptaPercent < t)
            || input.IsCommercialMixed
            || input.WindowMin is null
            || input.AptaTargetPercent is null;

        if (!hasFinding)
        {
            return new Result(false, OptimizationContractDirection.Maintain, OptimizationContractPriority.Low,
                "El lote está dentro de los parámetros comerciales esperados.", details, warnings);
        }

        // Prioridad: riesgo primero; objetivo de aptas segundo; lo demás informativo.
        var priority = input.RiskCount > 0
            ? OptimizationContractPriority.High
            : input.AptaPercent < (input.AptaTargetPercent ?? decimal.MaxValue)
                ? OptimizationContractPriority.Medium
                : OptimizationContractPriority.Low;

        var direction = input.RiskCount > 0
            ? OptimizationContractDirection.Verify
            : OptimizationContractDirection.Maintain;

        var explanation = input.RiskCount > 0
            ? $"{input.RiskCount} planta(s) en 'Riesgo / fuera de ventana' afectan el agregado del lote: revisar antes de decidir la cosecha. No se descarta el lote automáticamente."
            : input.AptaPercent < (input.AptaTargetPercent ?? decimal.MaxValue)
                ? $"El porcentaje de plantas aptas ({input.AptaPercent:0.#}%) no alcanza el objetivo ({input.AptaTargetPercent:0.#}%): el lote aún no está en condición de cosecha Baby Leaf."
                : "Estado comercial agregado con hallazgos informativos (revisar detalle).";

        return new Result(true, direction, priority, explanation, details, warnings);
    }
}