namespace HydroPilotWeb.Services.Simulation;

/// <summary>
/// Validación de servidor del escenario (SIM-05): negativos, incoherencias de
/// clima (Tmin &gt; Tmax), rangos de porcentajes y contexto incompleto se rechazan
/// con mensajes explícitos. La UI y la API usan la MISMA validación.
/// </summary>
public static class SimulationRequestValidator
{
    public static IReadOnlyList<string> Validate(SimulationRequest request)
    {
        var errors = new List<string>();

        // --- Contexto (SIM-01): lote o cultivo + parámetros libres ---
        if (request.LotId is null)
        {
            if (request.CropTypeId is null)
                errors.Add("Seleccioná un lote o un cultivo como contexto del escenario.");

            if (request.SowingDate is not { } sowing)
                errors.Add("Sin contexto de lote, la fecha de siembra es obligatoria.");
            else if (sowing > request.ReferenceDate.AddDays(1))
                errors.Add("La fecha de siembra no puede ser posterior a la fecha de referencia.");

            if (request.AreaM2 is not { } area)
            {
                errors.Add("Sin contexto de lote, la superficie (m²) es obligatoria.");
            }
            else if (area <= 0)
            {
                errors.Add("La superficie debe ser un valor positivo (m²).");
            }
        }

        // --- Clima manual (SIM-03) ---
        if (request.Climate.Mode == SimulationClimateMode.Manual)
        {
            if (request.Climate.ManualTempMinC is not { } tmin)
                errors.Add("Modo manual: temperatura mínima (Tmin) obligatoria.");
            if (request.Climate.ManualTempMaxC is not { } tmax)
                errors.Add("Modo manual: temperatura máxima (Tmax) obligatoria.");
            if (request.Climate.ManualTempMinC is { } min && request.Climate.ManualTempMaxC is { } max && min > max)
                errors.Add("Modo manual: Tmin no puede ser mayor que Tmax.");
        }

        if (request.Climate.ManualHumidityPercent is { } humidity && humidity is < 0 or > 100)
            errors.Add("La humedad manual debe estar entre 0 y 100%.");

        if (request.Climate.ForecastHorizonDays is { } horizon && horizon is <= 0 or > 30)
            errors.Add("El horizonte de pronóstico debe estar entre 1 y 30 días.");

        // --- Costos (SIM-05): negativos rechazados en servidor ---
        ValidateNonNegative(errors, "precio de semilla", request.Costs.Seeds.PricePerSeed);
        ValidateNonNegative(errors, "cantidad de semillas", request.Costs.Seeds.SeedCount);
        ValidateNonNegative(errors, "precio de nutrientes por litro", request.Costs.Nutrients.PricePerLiter);
        ValidateNonNegative(errors, "litros por día", request.Costs.Nutrients.LitersPerDay);
        ValidateNonNegative(errors, "precio de energía por kWh", request.Costs.Energy.PricePerKwh);
        ValidateNonNegative(errors, "kWh por día", request.Costs.Energy.KwhPerDay);

        if (request.Costs.Seeds.SeedCount is { } seeds && seeds > 1_000_000)
            errors.Add("La cantidad de semillas supera un rango razonable (≤ 1.000.000).");

        if (string.IsNullOrWhiteSpace(request.Costs.CurrencyCode))
            errors.Add("Indicá una moneda (código ISO 4217, ej. ARS).");
        else if (request.Costs.CurrencyCode.Length is < 3 or > 3)
            errors.Add("La moneda debe ser un código ISO 4217 de 3 letras (ej. ARS, USD).");

        // --- Hipótesis de cosecha (SIM-08/09) ---
        if (request.Harvest.HypothesisAptaPercent is { } hypothesis && hypothesis is < 0 or > 100)
            errors.Add("El porcentaje hipotético de plantas aptas debe estar entre 0 y 100.");
        if (request.Harvest.RequiredTargetPercent is { } target && target is < 0 or > 100)
            errors.Add("El umbral objetivo de aptas debe estar entre 0 y 100.");

        if (!request.Harvest.EnableBabyLeaf && !request.Harvest.EnableConvencional)
            errors.Add("Habilitá al menos una estrategia de cosecha (Baby Leaf y/o convencional).");

        // --- Overrides agronómicos (informativos) ---
        if (request.Agronomic?.PhOverride is { } ph && ph is < 0 or > 14)
            errors.Add("El pH del escenario debe estar entre 0 y 14.");
        if (request.Agronomic?.EcOverride is { } ec && ec < 0)
            errors.Add("La EC del escenario no puede ser negativa.");

        return errors;
    }

    private static void ValidateNonNegative(List<string> errors, string field, decimal? value)
    {
        if (value is { } v && v < 0)
            errors.Add($"El campo '{field}' no puede ser negativo.");
    }
}