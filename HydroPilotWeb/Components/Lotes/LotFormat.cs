using System.Globalization;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Components.Lotes;

/// <summary>Formato de números y fechas de la vista Lotes (es-AR, independiente de la cultura del servidor).</summary>
public static class LotFormat
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-AR");

    /// <summary>Porcentaje sin decimales para el primer nivel ("37%").</summary>
    public static string Pct(decimal value) => value.ToString("0", Culture) + "%";

    /// <summary>Porcentaje con hasta un decimal ("36,8%").</summary>
    public static string Pct1(decimal value) => value.ToString("0.#", Culture) + "%";

    public static string Num(decimal value) => value.ToString("0.#", Culture);

    /// <summary>Número con signo explícito ("+5,1", "-0,8").</summary>
    public static string SignedNum(decimal value) => (value > 0 ? "+" : string.Empty) + Num(value);

    public static string Kg(decimal value) => value.ToString("0.#", Culture) + " kg";

    /// <summary>Kg con 2 decimales si es poco (0,21 kg), con 1 si no (13,3 kg).</summary>
    public static string KgFine(decimal value) => value.ToString(value < 10m ? "0.##" : "0.#", Culture) + " kg";

    /// <summary>Importe sin decimales con separador de miles ("$1.014"); otra moneda va con su código.</summary>
    public static string Money(decimal value, string currency) =>
        (value < 0 ? "-" : string.Empty) + (currency == "ARS" ? "$" : currency + " ") + Math.Abs(value).ToString("N0", Culture);

    /// <summary>Fecha corta legible ("9 oct").</summary>
    public static string Day(DateOnly date) => date.ToString("d MMM", Culture).TrimEnd('.');

    /// <summary>Fecha numérica ("09/10").</summary>
    public static string Short(DateOnly date) => date.ToString("dd/MM", Culture);

    /// <summary>Hito con "≈" si es una estimación más allá del horizonte del pronóstico.</summary>
    public static string Milestone(CycleMilestone milestone) =>
        milestone.Date is { } date ? (milestone.IsExtrapolated ? "≈ " : string.Empty) + Day(date) : "sin fecha";

    /// <summary>Cantidad con singular/plural ("1 apta", "7 aptas").</summary>
    public static string Count(int count, string singular, string plural) => $"{count} {(count == 1 ? singular : plural)}";

    public static string Plants(int count) => Count(count, "planta", "plantas");

    public static string AptaPlants(int count) => count == 1 ? "1 planta apta" : $"{count} plantas aptas";

    public static string Days(int count) => count == 1 ? "1 día" : $"{count} días";
}

/// <summary>Clave de filtro de una celda del mapa (estado operativo o comercial).</summary>
public static class LotCellKeys
{
    public const string Apta = "apta";
    public const string Candidata = "candidata";
    public const string Desarrollo = "desarrollo";
    public const string Convencional = "convencional";
    public const string Riesgo = "riesgo";
    public const string Cosechada = "cosechada";
    public const string Descartada = "descartada";
    public const string Vacia = "vacia";

    public static string For(PlantCellDto cell)
    {
        if (cell.CellKind == "empty")
            return Vacia;
        if (cell.CellKind == "discarded" || cell.OperationalState == PlantOperationalState.Descartada)
            return Descartada;
        if (cell.OperationalState == PlantOperationalState.Cosechada)
            return Cosechada;

        return cell.CommercialStageName switch
        {
            CommercialStageNames.BabyLeafApta => Apta,
            CommercialStageNames.CandidataBabyLeaf => Candidata,
            CommercialStageNames.CosechaConvencional => Convencional,
            CommercialStageNames.RiesgoFueraDeVentana => Riesgo,
            _ => Desarrollo
        };
    }
}
