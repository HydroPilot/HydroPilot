using HydroPilotWeb.Models;

namespace HydroPilotWeb.Services.Anomalies;

/// <summary>
/// Registro canónico de reglas de anomalía (ANO-01): el catálogo cerrado y estable
/// de tipos. La política por cultivo (consecutivas, cooldown, activa) se persiste en
/// AnomalyRuleCatalog; cuando no hay fila, se usa la política por defecto de
/// AnomalyOptions. Temperatura/humedad nacen pendientes (sin umbral aprobado).
/// </summary>
public static class AnomalyRuleRegistry
{
    public sealed record RuleTemplate(string Code, string Name, string SensorTypeName);

    public static IReadOnlyList<RuleTemplate> Templates { get; } =
    [
        new(AnomalyContract.TypePhFueraDeBanda, "pH fuera de banda operativa", AnomalyContract.SensorTypePh),
        new(AnomalyContract.TypeCeFueraDeBanda, "CE fuera de banda operativa", AnomalyContract.SensorTypeCe),
        new(AnomalyContract.TypeTemperaturaFueraDeBanda, "Temperatura ambiente fuera de banda", AnomalyContract.SensorTypeTemperatura),
        new(AnomalyContract.TypeHumedadFueraDeBanda, "Humedad ambiente fuera de banda", AnomalyContract.SensorTypeHumedad),
    ];
}

/// <summary>Descripción explicable de la regla con los valores aplicados (qué y por qué se disparó).</summary>
public static class AnomalyRuleDescriptions
{
    private static string Fmt(decimal? v) => v?.ToString("0.##") ?? "-";

    public static string Describe(string ruleName, string sensorTypeName, AnomalyBand band) =>
        $"{ruleName} ({sensorTypeName}): banda operativa [{Fmt(band.OperationalMin)}–{Fmt(band.OperationalMax)}]"
        + (band.Target.HasValue ? $", objetivo {Fmt(band.Target)}" : string.Empty)
        + " — valor observado fuera de banda";
}