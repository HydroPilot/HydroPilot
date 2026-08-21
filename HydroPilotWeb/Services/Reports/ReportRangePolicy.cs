namespace HydroPilotWeb.Services.Reports;

/// <summary>
/// Política pura de rangos de fechas (REP-03): rangos demasiado grandes se
/// truncan a <see cref="ReportLimits.MaxRangeDays"/> y la omisión se devuelve
/// como advertencia explícita (nunca se descarta silenciosamente).
/// </summary>
public static class ReportRangePolicy
{
    public static (DateTime FromUtc, DateTime ToUtc, int RangeDays, string? Warning) Normalize(
        DateTime fromUtc,
        DateTime toUtc)
    {
        if (fromUtc > toUtc)
            throw new ArgumentOutOfRangeException(nameof(fromUtc), "El inicio del rango no puede ser posterior al fin.");

        var to = toUtc;
        string? warning = null;
        var maxSpan = TimeSpan.FromDays(ReportLimits.MaxRangeDays);
        if (to - fromUtc > maxSpan)
        {
            to = fromUtc.Add(maxSpan);
            warning =
                $"El rango excede el límite de {ReportLimits.MaxRangeDays} días: se truncó a {fromUtc:yyyy-MM-dd} → {to:yyyy-MM-dd}.";
        }

        var rangeDays = (int)Math.Ceiling((to - fromUtc).TotalDays) + 1;
        return (fromUtc, to, rangeDays, warning);
    }
}