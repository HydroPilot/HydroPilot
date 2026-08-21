namespace HydroPilotWeb.Services.Lotes;

/// <summary>
/// Regla híbrida de cosecha del lote (plan 09, decisión confirmada):
/// la cosecha combina la entrada en la ventana GDD Baby Leaf Y el porcentaje
/// configurable de plantas "Baby Leaf apta". Lógica pura, testeable.
/// Si el porcentaje objetivo no está definido, IsReady = null (decisión
/// pendiente); NUNCA se reemplaza por una constante oculta.
/// </summary>
public static class HarvestReadinessRules
{
    public sealed record Input(
        decimal LotGdd,
        decimal WindowMin,
        decimal WindowMax,
        int AptaCount,
        int TotalActive,
        decimal? TargetPercent);

    public sealed record Result(
        bool GddInWindow,
        decimal AptaPercent,
        bool? IsReady,
        string? PendingReason);

    public static Result Evaluate(Input input)
    {
        var gddInWindow = input.LotGdd >= input.WindowMin && input.LotGdd < input.WindowMax;
        var percent = input.TotalActive > 0
            ? Math.Round(input.AptaCount * 100m / input.TotalActive, 1)
            : 0m;

        if (!input.TargetPercent.HasValue)
        {
            return new Result(
                gddInWindow,
                percent,
                IsReady: null,
                PendingReason: "Porcentaje objetivo de plantas aptas no configurado: fecha de cosecha híbrida pendiente de decisión.");
        }

        var ready = gddInWindow && percent >= input.TargetPercent.Value;
        return new Result(gddInWindow, percent, ready, null);
    }
}