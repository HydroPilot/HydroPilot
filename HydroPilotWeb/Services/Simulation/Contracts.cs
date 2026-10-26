using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Services.Simulation;

// ============================================================================
// Contratos del módulo de simulación (plan 14, SIM-01..SIM-10).
//
// La simulación es un motor WHAT-IF puro: recibe inputs del escenario y devuelve
// un resultado calculado. NUNCA escribe producción (lotes, plantas, lecturas,
// predicciones, nodos, configuración de cultivo) y NUNCA invoca hardware.
// Los overrides (clima, % Baby Leaf, pesos, precios, costos) se aplican al
// escenario, no al dominio. El endpoint es PREVIEW sin persistencia (SIM-06).
//
// Usa la MISMA lógica que la vista Lotes (gana Lotes): ventanas y óptimos de corte
// por GDD (CycleTimelineLogic) y rendimiento por plantas × peso × precio (sin m²).
// ============================================================================

/// <summary>
/// Clima del escenario (SIM-03): manual (Tmin/Tmax del usuario, el what-if) o pronóstico
/// (persistido en DB por WeatherService; con lote, el mismo que usa la vista Lotes).
/// </summary>
public enum SimulationClimateMode
{
    Manual,
    Forecast
}

/// <summary>
/// Fuente de un punto diario de GDD DENTRO de la simulación. "Manual" solo existe en el
/// escenario. Los valores numéricos se calculan SIEMPRE con <see cref="GddService.DailyGdd"/>.
/// </summary>
public enum SimulationGddSource
{
    /// <summary>Temperatura manual del usuario aplicada al escenario (SIM-03).</summary>
    Manual,

    /// <summary>GDD derivado del pronóstico climático persistido (SIM-03).</summary>
    Forecast,

    /// <summary>Día sin pronóstico: promedio de los últimos 7 días medidos del lote (como en Lotes, informado).</summary>
    Fallback
}

/// <summary>Punto diario de GDD del escenario.</summary>
public sealed record SimulationGddPoint(DateOnly Date, decimal Gdd, SimulationGddSource Source);

/// <summary>Entrada climática del escenario (SIM-03).</summary>
public sealed record SimulationClimateInput(
    SimulationClimateMode Mode,
    decimal? ManualTempMinC,
    decimal? ManualTempMaxC,
    int? ForecastHorizonDays);

/// <summary>
/// Gastos del sistema del escenario (SIM-05): solo lo que el sistema sabe calcular (semillas =
/// plantas del lote o del escenario; litros de nutrientes según plantas vivas y etapa). Los demás
/// costos (mano de obra, energía, etc.) los arma el contador, fuera del sistema. Se cargan solo los
/// precios, en la moneda de Ajustes &gt; Parámetros. NutrientPricePerLiter null = el de Ajustes &gt;
/// Parámetros. Si falta un precio, el componente es "No calculable".
/// </summary>
public sealed record SimulationCostsInput(
    decimal? PricePerSeed,
    decimal? NutrientPricePerLiter);

/// <summary>Estrategia de cosecha que se elige para el escenario.</summary>
public enum SimulationStrategyKind
{
    /// <summary>Corta como Baby Leaf todas las que se prevén aptas en el óptimo; el resto sigue a convencional.</summary>
    MaxBabyLeaf,

    /// <summary>Corta como Baby Leaf hasta la meta (sin pasar las previstas aptas); el resto sigue a convencional.</summary>
    BabyLeafGoal,

    /// <summary>Todas siguen hasta el corte convencional.</summary>
    AllConventional
}

/// <summary>
/// Estrategia elegida y meta Baby Leaf máxima (% de las plantas vivas en el óptimo). La meta es
/// obligatoria con <see cref="SimulationStrategyKind.BabyLeafGoal"/>; con otra estrategia, si viene,
/// esa opción también se calcula para comparar.
/// </summary>
public sealed record SimulationHarvestInput(SimulationStrategyKind Strategy, decimal? BabyLeafGoalPercent);

/// <summary>
/// Pesos por planta y precios por kg del escenario. Null = el valor de Ajustes &gt; Parámetros
/// (con el precio propio del lote si lo tiene).
/// </summary>
public sealed record SimulationYieldInput(
    decimal? BabyLeafGramsPerPlant,
    decimal? ConventionalGramsPerPlant,
    decimal? BabyLeafPricePerKg,
    decimal? ConventionalPricePerKg);

/// <summary>
/// Escenario de simulación (SIM-01), siempre desde hoy. Con lote (abierto): siembra, GDD medido
/// hasta hoy y plantas activas salen del lote; desde mañana, el clima elegido. Sin lote (escenario
/// libre): cultivo, siembra (pasada, hoy o futura, para planificar) y cantidad de plantas los pone
/// el usuario, y el GDD se cuenta desde la siembra con el clima elegido. Solo viajan datos que
/// entran en algún cálculo. NO se aceptan desde el cliente resultado calculado ni comandos físicos.
/// </summary>
public sealed record SimulationRequest(
    int? LotId,
    int? CropTypeId,
    DateOnly? SowingDate,
    int? PlantCount,
    SimulationClimateInput Climate,
    SimulationCostsInput Costs,
    SimulationHarvestInput Harvest,
    SimulationYieldInput? Yield);

/// <summary>Resumen del contexto del escenario: lote real o cultivo + parámetros libres.</summary>
public sealed record SimulationContextInfo(
    string ContextKind, // "lote" | "cultivo"
    int? LotId,
    string? LotName,
    string CropTypeName,
    DateOnly SowingDate,
    int Plants, // plantas que se simulan: activas del lote o las del escenario libre
    decimal BaseTemperature);

/// <summary>
/// GDD del escenario (SIM-04): el acumulado hasta hoy (lecturas reales del lote, o el clima elegido
/// desde la siembra en el escenario libre) y la proyección del clima elegido.
/// </summary>
public sealed record SimulationGdd(
    decimal Accumulated,
    decimal BaseTemperature,
    IReadOnlyList<SimulationGddPoint> Projection,
    int HorizonDays,
    int CoveredDays,
    int MissingDays,
    decimal? CoveragePercent,
    string ProviderName, // "manual" | "pronostico"
    DateTime? ForecastFetchedAtUtc,
    bool UsedFallback,
    IReadOnlyList<string> Warnings,
    ForecastCorrection? Correction); // pronóstico de afuera corregido a la temperatura de adentro

/// <summary>
/// Información del lote real usada como contexto (SIM-08): etapa, plantas y grilla (nombre,
/// siembra y GDD están en el contexto y el GDD del resultado). SOLO LECTURA: nunca escribe el lote.
/// </summary>
public sealed record SimulationLotInfo(
    int LotId,
    string? PhenologicalStageName,
    int GridRows,
    int GridColumns,
    int TotalPlants,
    int ActivePlants,
    int HarvestedPlants,
    int DiscardedPlants,
    IReadOnlyList<PlantCellDto> Grid);

/// <summary>Pesos y precios con los que se valoriza el escenario (y si alguno vino del escenario).</summary>
public sealed record SimulationYieldReference(YieldReference Weights, YieldPrices Prices, bool HasScenarioOverrides);

/// <summary>
/// Corte de un destino según la lógica de Lotes. Date null = no se puede cortar (ventana Baby Leaf
/// ya cerrada, o sin proyección para estimar). IsNow = ya pasó el óptimo: se corta hoy.
/// IsOverdue = pasada la ventana (Baby Leaf: ya no se puede; convencional: sobremadurez).
/// </summary>
public sealed record SimulationCut(
    HarvestDestination Destination,
    DateOnly? Date,
    bool IsNow,
    bool IsExtrapolated,
    bool IsOverdue);

/// <summary>Componente de costo con fórmula transparente (SIM-05).</summary>
public sealed record SimulationCostComponent(
    string Name,
    string Formula,
    decimal? Amount,
    string? NotCalculableReason,
    string Unit);

/// <summary>
/// Desglose de costos de una estrategia (SIM-05). Total null = "No calculable" (faltan precios).
/// </summary>
public sealed record SimulationCostBreakdown(
    string CurrencyCode,
    IReadOnlyList<SimulationCostComponent> Components,
    decimal? Total,
    string? TotalNotCalculableReason,
    int CycleDays, // días de la siembra al último corte de la estrategia
    int Seeds,     // plantas del lote (todas) o del escenario
    NutrientUsage? Usage, // agua y nutrientes del ciclo; null = sin fecha de corte
    IReadOnlyList<string> Warnings);

/// <summary>
/// Resultado de una estrategia: cuántas plantas van a cada destino y cuántas se prevé perder,
/// cuándo se cortan, kg, ingresos, costos del ciclo y margen. Available = false si algún destino
/// con plantas no se puede cortar.
/// </summary>
public sealed record SimulationStrategyOutcome(
    SimulationStrategyKind Kind,
    string Name, // "Máximo Baby Leaf" | "Meta Baby Leaf (X %)" | "Todo adulta"
    bool Available,
    SimulationCut? BabyLeafCut,
    SimulationCut? ConventionalCut,
    DateOnly? EndDate,
    DestinationYield BabyLeaf,
    DestinationYield Conventional,
    int LostPlants, // pérdidas previstas (tempranas + tardías)
    decimal? TotalKg,
    decimal? Revenue,
    SimulationCostBreakdown Costs,
    decimal? Margin,
    decimal? CostPerKg,
    IReadOnlyList<string> Warnings);

/// <summary>Resultado completo de una simulación preview (SIM-02: un único resultado por corrida).</summary>
public sealed record SimulationResult(
    string SimulationId, // determinista: misma entrada el mismo día → mismo id/resultado (repetibilidad)
    SimulationRequest Request,
    DateOnly Today,      // el "hoy" del escenario: hasta acá lo medido; desde mañana, el clima elegido
    bool IsSimulated,
    SimulationContextInfo Context,
    SimulationLotInfo? Lot,
    SimulationGdd Gdd,
    CycleTimeline? Timeline, // ventanas y óptimos de corte (como la línea de tiempo de Lotes)
    GddCurve? Curve,         // GDD acumulado: hasta hoy + proyección del escenario
    SimulationYieldReference Reference,
    BabyLeafOutlook Outlook, // previsión de aptas a Baby Leaf y pérdidas (histórico + estado del lote)
    IReadOnlyList<SimulationStrategyOutcome> Strategies,
    IReadOnlyList<string> Warnings);

/// <summary>Respuesta del preview: validación + resultado (la UI y la API ven lo mismo).</summary>
public sealed record SimulationPreview(
    bool IsValid,
    IReadOnlyList<string> ValidationErrors,
    SimulationResult? Result);

/// <summary>Lote para el selector de contexto, con lo que la vista usa para precargar el formulario.</summary>
public sealed record SimulationLotOption(
    int Id,
    string Name,
    string CropTypeName,
    string StatusName,
    DateOnly SowingDate,
    int TotalPlants,
    int ActivePlants,
    decimal? BabyLeafGoalPercent);

public sealed record SimulationCropOption(int Id, string Name, decimal BaseTemperature);

/// <summary>Opciones de contexto para la UI (sin acceso a EF desde la vista), con los parámetros del sistema.</summary>
public sealed record SimulationContextOptions(
    IReadOnlyList<SimulationLotOption> Lots,
    IReadOnlyList<SimulationCropOption> Crops,
    YieldParameters Parameters,
    decimal? NutrientPricePerLiter); // de Ajustes > Parámetros; null = sin definir

/// <summary>Problema de validación devuelto por la API (SIM-05: negativos y rangos validados en servidor).</summary>
public sealed record SimulationValidationProblem(IReadOnlyList<string> Errors);
