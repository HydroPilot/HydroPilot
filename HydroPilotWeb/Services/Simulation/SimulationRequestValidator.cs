using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Services.Simulation;

/// <summary>
/// Validación de servidor del escenario (SIM-05): negativos, incoherencias de
/// clima (Tmin &gt; Tmax), rangos de porcentajes y contexto incompleto se rechazan
/// con mensajes explícitos. La UI y la API usan la MISMA validación.
/// </summary>
public static class SimulationRequestValidator
{
    public const int MaxPlants = 100_000;

    /// <summary>Siembra del escenario libre: pasada, hoy o futura (planificar), hasta un año para cada lado.</summary>
    public const int MaxSowingOffsetDays = 365;

    public static IReadOnlyList<string> Validate(SimulationRequest request, DateOnly today)
    {
        var errors = new List<string>();

        // --- Contexto (SIM-01): lote o cultivo + parámetros libres (sin m²: cuenta la cantidad de plantas) ---
        if (request.LotId is null)
        {
            if (request.CropTypeId is null)
                errors.Add("Seleccioná un lote o un cultivo como contexto del escenario.");

            if (request.SowingDate is not { } sowing)
                errors.Add("Sin contexto de lote, la fecha de siembra es obligatoria.");
            else if (Math.Abs(sowing.DayNumber - today.DayNumber) > MaxSowingOffsetDays)
                errors.Add("La fecha de siembra tiene que estar dentro de un año (para atrás o para adelante) desde hoy.");

            if (request.PlantCount is not { } plants)
                errors.Add("Sin contexto de lote, la cantidad de plantas es obligatoria.");
            else if (plants is <= 0 or > MaxPlants)
                errors.Add($"La cantidad de plantas tiene que ser mayor a 0 y hasta {MaxPlants:N0}.");
        }

        // --- Clima manual (SIM-03) ---
        if (request.Climate.Mode == SimulationClimateMode.Manual)
        {
            if (request.Climate.ManualTempMinC is null)
                errors.Add("Modo manual: temperatura mínima (Tmin) obligatoria.");
            if (request.Climate.ManualTempMaxC is null)
                errors.Add("Modo manual: temperatura máxima (Tmax) obligatoria.");
            if (request.Climate.ManualTempMinC is { } min && request.Climate.ManualTempMaxC is { } max && min > max)
                errors.Add("Modo manual: Tmin no puede ser mayor que Tmax.");
        }

        if (request.Climate.ForecastHorizonDays is { } horizon && horizon is <= 0 or > 30)
            errors.Add("El horizonte de pronóstico debe estar entre 1 y 30 días.");

        // --- Costos (SIM-05): negativos rechazados en servidor ---
        ValidateNonNegative(errors, "precio de semilla", request.Costs.PricePerSeed);
        ValidateNonNegative(errors, "precio de nutrientes por litro", request.Costs.NutrientPricePerLiter);

        // --- Estrategia: la meta Baby Leaf máxima es obligatoria si se elige esa estrategia ---
        if (request.Harvest.BabyLeafGoalPercent is { } goal && goal is < 0 or > 100)
            errors.Add("La meta Baby Leaf máxima debe estar entre 0 y 100 %.");
        else if (request.Harvest.Strategy == SimulationStrategyKind.BabyLeafGoal && request.Harvest.BabyLeafGoalPercent is null)
            errors.Add("Indicá la meta Baby Leaf máxima (%).");

        // --- Pesos y precios del escenario: mismas reglas que Ajustes > Parámetros ---
        if (request.Yield is { } yield)
        {
            errors.AddRange(YieldReferenceRules.Validate(new YieldReferenceInput(
                yield.BabyLeafGramsPerPlant, yield.ConventionalGramsPerPlant,
                yield.BabyLeafPricePerKg, yield.ConventionalPricePerKg)));
        }

        return errors;
    }

    private static void ValidateNonNegative(List<string> errors, string field, decimal? value)
    {
        if (value is { } v && v < 0)
            errors.Add($"El campo '{field}' no puede ser negativo.");
    }
}
