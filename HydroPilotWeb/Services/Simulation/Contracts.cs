using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Services.Simulation;

// ============================================================================
// Contratos del módulo de simulación (plan 14, SIM-01..SIM-10).
//
// La simulación es un motor WHAT-IF puro: recibe inputs del escenario y devuelve
// un resultado calculado. NUNCA escribe producción (lotes, plantas, lecturas,
// predicciones, nodos, configuración de cultivo) y NUNCA invoca hardware.
// Los overrides (clima, % apto, costos) se aplican al escenario, no al dominio.
// La persistencia (SimulationRun) queda para una entrega posterior; en esta
// entrega el endpoint es PREVIEW sin persistencia (SIM-06).
// ============================================================================

/// <summary>
/// Modo climático del escenario (SIM-03): manual (temperaturas del usuario),
/// histórico (replay del promedio observado) o pronóstico (persistido en DB por
/// WeatherService, con fetch perezoso serializado/guard diario del forecasting).
/// </summary>
public enum SimulationClimateMode
{
    Manual,
    Historic,
    Forecast
}

/// <summary>
/// Fuente de un punto diario de GDD DENTRO de la simulación. No reutiliza
/// <see cref="GddPointSource"/> (contrato de forecasting) porque las semánticas
/// difieren: "Manual" solo existe en el escenario y "HistoricReplay" es un
/// promedio observado proyectado, no un pronóstico. Los valores numéricos sí
/// se calculan SIEMPRE con <see cref="GddService.DailyGdd"/> (núcleo compartido).
/// </summary>
public enum SimulationGddSource
{
    /// <summary>Temperatura manual del usuario aplicada al escenario (SIM-03).</summary>
    Manual,

    /// <summary>Promedio del GDD observado reciente proyectado hacia adelante (replay, SIM-03).</summary>
    HistoricReplay,

    /// <summary>GDD derivado del pronóstico climático persistido (SIM-03).</summary>
    Forecast,

    /// <summary>GDD de respaldo cuando no hay dato directo para la fecha (informado, nunca silencioso).</summary>
    Fallback
}

/// <summary>Punto diario de GDD del escenario.</summary>
public sealed record SimulationGddPoint(DateOnly Date, decimal Gdd, SimulationGddSource Source);

/// <summary>Entrada climática del escenario (SIM-03).</summary>
public sealed record SimulationClimateInput(
    SimulationClimateMode Mode,
    decimal? ManualTempMinC,
    decimal? ManualTempMaxC,
    int? ManualHumidityPercent, // contexto informativo; no participa de las fórmulas vigentes (GDD usa Tmin/Tmax)
    int? ForecastHorizonDays);

/// <summary>Overrides de solución nutritiva del escenario (informativo, no modifica el lote).</summary>
public sealed record SimulationAgronomicInput(
    decimal? PhOverride,
    decimal? EcOverride);

/// <summary>Costo de semillas: precio por semilla × cantidad (fórmula SIM-05).</summary>
public sealed record SimulationSeedCostsInput(decimal? PricePerSeed, int? SeedCount);

/// <summary>Costo de nutrientes: precio por litro × litros/día × días proyectados (fórmula SIM-05).</summary>
public sealed record SimulationNutrientCostsInput(decimal? PricePerLiter, decimal LitersPerDay);

/// <summary>Costo de energía: precio por kWh × kWh/día × días proyectados (fórmula SIM-05).</summary>
public sealed record SimulationEnergyCostsInput(decimal? PricePerKwh, decimal KwhPerDay);

/// <summary>
/// Costos del escenario (SIM-05). CurrencyCode es un código ISO 4217 (ej. "ARS").
/// Si falta un precio, el componente muestra "No calculable" (nunca se inventa).
/// </summary>
public sealed record SimulationCostsInput(
    string CurrencyCode,
    SimulationSeedCostsInput Seeds,
    SimulationNutrientCostsInput Nutrients,
    SimulationEnergyCostsInput Energy);

/// <summary>
/// Hipótesis de cosecha del escenario (SIM-08/SIM-09). HypothesisAptaPercent es
/// la HIPÓTESIS EXPLÍCITA del usuario sobre el % de plantas activas que llegarán
/// a "Baby Leaf apta": la simulación no inventa evaluaciones visuales por planta.
/// RequiredTargetPercent es el umbral objetivo (prefijado con el del lote cuando
/// hay contexto de lote; editable solo para el escenario).
/// </summary>
public sealed record SimulationHarvestInput(
    bool EnableBabyLeaf,
    bool EnableConvencional,
    decimal? HypothesisAptaPercent,
    decimal? RequiredTargetPercent);

/// <summary>
/// Escenario de simulación (SIM-01). NO se aceptan desde el cliente resultado
/// calculado, usuario autenticado de destino ni comandos físicos.
/// SaveScenario es una marca explícita de guardar: en esta entrega la persistencia
/// no está disponible y la marca se informa como advertencia (SIM-06), nunca se
/// persiste silenciosamente.
/// </summary>
public sealed record SimulationRequest(
    int? LotId,
    int? CropTypeId,
    DateOnly? SowingDate,
    decimal? AreaM2,
    DateOnly ReferenceDate,
    SimulationClimateInput Climate,
    SimulationAgronomicInput? Agronomic,
    SimulationCostsInput Costs,
    SimulationHarvestInput Harvest,
    bool SaveScenario);

/// <summary>Resumen del contexto del escenario: lote real o cultivo + parámetros libres.</summary>
public sealed record SimulationContextInfo(
    string ContextKind, // "lote" | "cultivo"
    int? LotId,
    string? LotName,
    string CropTypeName,
    DateOnly? SowingDate,
    decimal? AreaM2,
    decimal BaseTemperature,
    decimal GddTarget);

/// <summary>
/// Resultado del cálculo de GDD del escenario (SIM-04). El GDD base es siempre
/// un valor RESPETANDO AsOfDate (ReferenceDate): lecturas posteriores a la fecha
/// simulada quedan excluidas. Los escenarios de rendimiento conservador/base/
/// optimista los provee el núcleo de forecasting (YieldEstimate).
/// </summary>
public sealed record SimulationGdd(
    decimal Accumulated,
    decimal GddTarget,
    decimal BaseTemperature,
    IReadOnlyList<SimulationGddPoint> Projection,
    int HorizonDays,
    int CoveredDays,
    int MissingDays,
    decimal? CoveragePercent,
    DateOnly? EstimatedHarvestDate,
    int? DaysRemaining,
    string ProviderName, // "manual" | "historico" | "pronostico"
    DateTime? ForecastFetchedAtUtc,
    bool UsedFallback,
    bool IsExtrapolation, // fecha más allá del horizonte: extrapolación con el último GDD diario
    IReadOnlyList<string> Warnings);

/// <summary>
/// Información del lote real usada como contexto (SIM-08): GDD base, etapa
/// fenológica, plantas activas/cosechadas/descartadas, grilla configurada y
/// celdas reales. Todo SOLO LECTURA: la simulación nunca escribe el lote.
/// </summary>
public sealed record SimulationLotInfo(
    int LotId,
    string? LotName,
    string CropTypeName,
    DateOnly SowingDate,
    decimal AreaM2,
    decimal GddAccumulated,
    string? PhenologicalStageName,
    int? PhenologicalStageOrder,
    int GridRows,
    int GridColumns,
    int TotalPlants,
    int ActivePlants,
    int HarvestedPlants,
    int DiscardedPlants,
    int EmptyPositions,
    IReadOnlyList<PlantCellDto> Grid);

/// <summary>
/// Resultado de la regla híbrida de cosecha simulada (SIM-09). La regla es la
/// misma de producción (HarvestReadinessRules, reutilizada): entrada en ventana
/// GDD Y porcentaje de aptas sobre el umbral. La diferencia: el % de aptas puede
/// ser una HIPÓTESIS del usuario (AptaPercentIsHypothesis=true), nunca una
/// evaluación visual inventada. La salida indica qué parte decidió el GDD y qué
/// parte es hipótesis de score.
/// </summary>
public sealed record SimulationHarvestOutcome(
    bool Enabled,
    bool HasBabyLeafConfig, // sin configuración activa no se puede evaluar ventana
    decimal? WindowMin,
    decimal? WindowMax,
    bool GddInWindow,
    decimal? RequiredPercent,
    int RealAptaCount,
    int TotalActivePlants,
    decimal? AptaPercentUsed,
    bool AptaPercentIsHypothesis,
    bool? IsReady, // null = decisión pendiente
    string? DecisionReason,
    DateOnly? BabyLeafEntryDate,
    DateOnly? ConventionalEntryDate,
    bool ConventionalHasMaturityData, // análisis de imagen (fase futura)
    IReadOnlyList<string> Warnings);

/// <summary>Componente de costo con fórmula transparente (SIM-05).</summary>
public sealed record SimulationCostComponent(
    string Name,
    string Formula,
    decimal? Amount,
    string? NotCalculableReason,
    string Unit);

/// <summary>
/// Desglose de costos (SIM-05). Total null = "No calculable" (faltan precios).
/// CostPerKg solo si total y rendimiento > 0; nunca se inventa rentabilidad.
/// </summary>
public sealed record SimulationCostBreakdown(
    string CurrencyCode,
    IReadOnlyList<SimulationCostComponent> Components,
    decimal? Total,
    string? TotalNotCalculableReason,
    decimal? CostPerKg,
    int ProjectedDays, // días usados para nutrientes/energía (días al cierre o horizonte con advertencia)
    IReadOnlyList<string> Warnings);

/// <summary>Resultado de una estrategia dentro de la comparación (ALTERNATIVAS).</summary>
public sealed record SimulationAlternativeOutcome(
    string Strategy, // "Baby Leaf" | "Convencional"
    DateOnly? HarvestDate,
    int? DaysRemaining,
    bool? IsReady,
    string DecisionBasis, // "gdd" | "gdd + hipótesis de score" | "gdd + madurez (análisis de imagen)" | "pendiente"
    IReadOnlyList<string> Warnings);

/// <summary>
/// Comparación de estrategias (SIM-09): Baby Leaf vs convencional con sus
/// umbrales separados (ventana Baby Leaf configurada vs GddTarget del cultivo).
/// Incluye las hipótesis usadas para que el productor sepa qué supuso el cálculo.
/// </summary>
public sealed record SimulationAlternativeComparison(
    IReadOnlyList<SimulationAlternativeOutcome> Alternatives,
    IReadOnlyList<string> HypothesesUsed,
    IReadOnlyList<string> Warnings);

/// <summary>Resultado completo de una simulación preview (SIM-02: un único resultado por corrida).</summary>
public sealed record SimulationResult(
    string SimulationId, // determinista: misma entrada → mismo id/resultado (repetibilidad)
    SimulationRequest Request,
    bool IsSimulated, // true: entorno simulado (banner permanente)
    SimulationContextInfo Context,
    SimulationLotInfo? Lot,
    SimulationGdd Gdd,
    YieldEstimate Yield,
    SimulationHarvestOutcome? Harvest,
    SimulationAlternativeComparison Alternatives,
    SimulationCostBreakdown Costs,
    IReadOnlyList<string> Warnings);

/// <summary>Respuesta del preview: validación + resultado (la UI y la API ven lo mismo).</summary>
public sealed record SimulationPreview(
    bool IsValid,
    IReadOnlyList<string> ValidationErrors,
    SimulationResult? Result);

/// <summary>Opciones de contexto para los selectores de la UI (sin acceso a EF desde la vista).</summary>
public sealed record SimulationLotOption(int Id, string Name, string CropTypeName, string StatusName, DateOnly SowingDate, decimal AreaM2, decimal? RequiredTargetPercent);

public sealed record SimulationCropOption(int Id, string Name, decimal GddTarget, decimal BaseTemperature, decimal? YieldPerM2);

public sealed record SimulationContextOptions(
    IReadOnlyList<SimulationLotOption> Lots,
    IReadOnlyList<SimulationCropOption> Crops);

/// <summary>Problema de validación devuelto por la API (SIM-05: negativos/moneda/texto validados en servidor).</summary>
public sealed record SimulationValidationProblem(IReadOnlyList<string> Errors);