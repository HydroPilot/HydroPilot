using HydroPilotWeb.Components.Lotes;

namespace HydroPilotWeb.Components.Dashboard;

/// <summary>Formato de los números del dashboard (es-AR).</summary>
public static class DashboardFormat
{
    /// <summary>
    /// Importe corto, siempre hacia abajo para no prometer de más: $123.569 → "$123k",
    /// $1.250.000 → "$1,2M", $950 → "$950". Otra moneda va con su código ("USD 12k").
    /// </summary>
    public static string CompactMoney(decimal value, string currency)
    {
        var sign = value < 0 ? "-" : string.Empty;
        var abs = Math.Abs(value);
        var prefix = currency == "ARS" ? "$" : currency + " ";
        var body = abs switch
        {
            >= 1_000_000m => (Math.Floor(abs / 100_000m) / 10m).ToString("0.#", LotFormat.Culture) + "M",
            >= 1_000m => Math.Floor(abs / 1_000m).ToString("0", LotFormat.Culture) + "k",
            _ => Math.Floor(abs).ToString("0", LotFormat.Culture)
        };
        return sign + prefix + body;
    }
}
