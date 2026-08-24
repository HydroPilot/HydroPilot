using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Services.Simulation;

/// <summary>
/// Comparación de estrategias de cosecha (SIM-09): Baby Leaf vs convencional con
/// umbrales separados (ventana Baby Leaf configurada vs GddTarget del cultivo).
/// Lógica pura (testeable sin I/O): reutiliza HarvestReadinessRules (regla híbrida
/// de producción) y GddDateLogic (núcleo de forecasting). La salida indica qué
/// parte decidió el GDD y qué parte es hipótesis de score del usuario; sin
/// supuestos suficientes, la estrategia queda "pendiente" (nunca se infiere).
/// </summary>
public static class AlternativeComparisonService
{
    public sealed record Context(
        DateOnly ReferenceDate,
        decimal AccumulatedGdd,
        IReadOnlyList<SimulationGddPoint> Projection,
        int HorizonDays,
        string CropName,
        decimal CropTarget,
        (decimal Min, decimal Max)? BabyLeafWindow, // null = sin configuración activa
        int RealAptaCount,
        int TotalActivePlants,
        decimal? HypothesisAptaPercent,
        decimal? RequiredTargetPercent,
        bool ConventionalHasMaturityData,
        bool EnableBabyLeaf,
        bool EnableConvencional);

    public static SimulationAlternativeComparison Compare(Context ctx)
    {
        var alternatives = new List<SimulationAlternativeOutcome>();
        var hypotheses = new List<string>();

        // --- Estrategia Baby Leaf (ventana GDD + % aptas, regla híbrida de producción) ---
        if (ctx.EnableBabyLeaf)
        {
            if (ctx.BabyLeafWindow is not { } window)
            {
                var unavailable = new List<string>
                {
                    "Sin configuración Baby Leaf activa para el cultivo: la estrategia Baby Leaf no se puede simular."
                };
                alternatives.Add(new SimulationAlternativeOutcome(
                    "Baby Leaf", null, null, null, "no disponible", unavailable));
            }
            else
            {
                var entry = GddDateLogic.EstimateCrossDate(ctx.AccumulatedGdd, ToCorePoints(ctx.Projection), window.Min);

            // % de aptas efectivo: hipótesis del usuario (explícita) o real de evaluaciones.
            var hypothesis = ctx.HypothesisAptaPercent;
            var usesHypothesis = hypothesis.HasValue;
            var aptaCount = usesHypothesis
                ? Math.Max(0, (int)Math.Round(hypothesis!.Value * ctx.TotalActivePlants / 100m, 0, MidpointRounding.AwayFromZero))
                : ctx.RealAptaCount;
            var aptaPercent = usesHypothesis
                ? Math.Round(hypothesis!.Value, 1)
                : ctx.TotalActivePlants > 0
                    ? Math.Round(ctx.RealAptaCount * 100m / ctx.TotalActivePlants, 1)
                    : 0m;

            var rule = HarvestReadinessRules.Evaluate(new HarvestReadinessRules.Input(
                ctx.AccumulatedGdd, window.Min, window.Max, aptaCount, ctx.TotalActivePlants, ctx.RequiredTargetPercent));

            var babyLeafDate = rule.IsReady == true ? ctx.ReferenceDate : entry;
            var warnings = new List<string>();
            if (rule.PendingReason is not null)
                warnings.Add(rule.PendingReason);
            if (rule.IsReady == false && !rule.GddInWindow)
                warnings.Add($"GDD {ctx.AccumulatedGdd} fuera de la ventana Baby Leaf ({window.Min}–{window.Max}).");
            if (rule.IsReady == false && aptaPercent < ctx.RequiredTargetPercent)
                warnings.Add($"Porcentaje de aptas ({aptaPercent}%) por debajo del objetivo ({ctx.RequiredTargetPercent}%).");

            var basis = rule.IsReady switch
            {
                true when usesHypothesis => "gdd + hipótesis de score",
                true => "gdd + evaluaciones reales",
                null => "pendiente (sin decisión automática)",
                _ => "gdd + hipótesis de score (no listo)"
            };

            alternatives.Add(new SimulationAlternativeOutcome(
                "Baby Leaf",
                babyLeafDate,
                GddDateLogic.DaysRemaining(ctx.ReferenceDate, babyLeafDate),
                rule.IsReady,
                basis,
                warnings));

            if (usesHypothesis)
                hypotheses.Add($"Se supuso que el {ctx.HypothesisAptaPercent:0.#}% de las plantas activas alcanzará 'Baby Leaf apta' (hipótesis del usuario; no es una evaluación visual).");
            }
        }

        // --- Estrategia convencional (ciclo completo: GddTarget + madurez) ---
        if (ctx.EnableConvencional && ctx.CropTarget > 0)
        {
            var conventionalDate = GddDateLogic.EstimateCrossDate(ctx.AccumulatedGdd, ToCorePoints(ctx.Projection), ctx.CropTarget);
            if (ctx.AccumulatedGdd >= ctx.CropTarget)
                conventionalDate = ctx.ReferenceDate;

            var warnings = new List<string>();
            bool? ready = null;
            if (ctx.ConventionalHasMaturityData)
            {
                // Criterio de tamaño/madurez confirmado por análisis de imagen reales.
                ready = true;
            }
            else
            {
                warnings.Add("Cosecha convencional: sin análisis de imagen persistido (fase futura): la decisión de madurez queda pendiente, no se infiere tamaño.");
            }

            hypotheses.Add("Cosecha convencional por GddTarget del cultivo; la madurez requiere análisis de imagen (fase futura).");

            alternatives.Add(new SimulationAlternativeOutcome(
                "Convencional",
                conventionalDate,
                GddDateLogic.DaysRemaining(ctx.ReferenceDate, conventionalDate),
                ready,
                ctx.ConventionalHasMaturityData ? "gdd + madurez (análisis de imagen)" : "gdd (madurez pendiente)",
                warnings));
        }

        if (ctx.RequiredTargetPercent is { } target)
        {
            hypotheses.Add($"Umbral objetivo de plantas aptas: {target:0.#}% (configuración del lote o supuesto del escenario).");
        }
        else
        {
            hypotheses.Add("Sin umbral objetivo de aptas: la regla híbrida queda en 'decisión pendiente' (visible), no se reemplaza por una constante.");
        }

        return new SimulationAlternativeComparison(
            alternatives,
            hypotheses.Distinct().ToList(),
            hypotheses.Count == 0
                ? []
                : hypotheses.Select(h => $"Supuesto: {h}").ToList());
    }

    private static IReadOnlyList<DailyGddPoint> ToCorePoints(IReadOnlyList<SimulationGddPoint> points) =>
        points.Select(p => new DailyGddPoint(p.Date, p.Gdd, GddPointSource.Forecast)).ToList();
}