using HydroPilotWeb.Models;

namespace HydroPilotWeb.Services.Anomalies;

/// <summary>
/// Banda (física y operativa) usada para evaluar una lectura contra una regla.
/// La validez FÍSICA ya la decide IoT (calidad INVALID excluida por
/// TelemetryQualityPolicy): acá la banda física es solo referencia divulgativa.
/// </summary>
public sealed record AnomalyBand(
    decimal? PhysicalMin,
    decimal? PhysicalMax,
    decimal? OperationalMin,
    decimal? OperationalMax,
    decimal? Target);

/// <summary>Regla resuelta para evaluación: política + banda (null = pendiente, nunca dispara).</summary>
public sealed record ResolvedRule(
    string Code,
    string Type,
    string Name,
    string SensorTypeName,
    int ConsecutiveToOpen,
    int CooldownMinutes,
    bool IsActive,
    string Source,
    string? Notes,
    AnomalyBand? Band);

/// <summary>
/// Resolución de la banda operativa por cultivo/etapa (ANO-01/ANO-02), sin duplicar
/// los rangos del dueño: pH desde CropType.OptimalPh*, CE desde PhenologicalStage
/// (EcMin/EcMax/EcObjective) — los mismos valores que usa la receta de lotes.
/// Temperatura y humedad NO tienen umbral agronómico aprobado (plan 15): se evalúan
/// solo si el catálogo de reglas define un baseline explícito (hoy ninguna fila lo
/// tiene → pendiente visible, nunca se inventa un límite). La banda física se toma
/// del catálogo físico de IoT (TelemetryValidationService.Bands) para no repetir constantes.
/// </summary>
public static class AnomalyBandResolver
{
    public static AnomalyBand? ResolveOperative(
        CropType crop,
        PhenologicalStage? stage,
        string sensorTypeName,
        AnomalyRuleCatalog? rule)
    {
        switch (sensorTypeName)
        {
            case AnomalyContract.SensorTypePh:
                // Sin rango aprobado en el cultivo → la regla queda pendiente (no se evalúa).
                if (crop.OptimalPhMin is null || crop.OptimalPhMax is null)
                    return null;
                var phPhysical = TelemetryValidationService.FindBand(AnomalyContract.SensorTypePh);
                return new AnomalyBand(
                    phPhysical?.PhysicalMin, phPhysical?.PhysicalMax,
                    crop.OptimalPhMin, crop.OptimalPhMax, crop.OptimalPhTarget);

            case AnomalyContract.SensorTypeCe:
                // La receta de EC es por etapa fenológica aplicada del lote.
                if (stage is null)
                    return null;
                var ecPhysical = TelemetryValidationService.FindBand(AnomalyContract.SensorTypeCe);
                return new AnomalyBand(
                    ecPhysical?.PhysicalMin, ecPhysical?.PhysicalMax,
                    stage.EcMin, stage.EcMax, stage.EcObjective);

            // Temperatura / Humedad: solo con baseline explícito en el catálogo (futuro).
            default:
                if (rule is null || (rule.OperationalMin is null && rule.OperationalMax is null))
                    return null; // sin umbral aprobado → pendiente
                var physical = TelemetryValidationService.FindBand(sensorTypeName);
                return new AnomalyBand(
                    physical?.PhysicalMin, physical?.PhysicalMax,
                    rule.OperationalMin, rule.OperationalMax, rule.TargetValue);
        }
    }
}