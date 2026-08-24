using HydroPilotWeb.Models;

namespace HydroPilotWeb.Services.Lotes;

/// <summary>
/// Nombres canónicos de los estados comerciales (catalogo CommercialStage).
/// Se usan para resolver y mostrar estados; no son valores agronómicos ocultos.
/// </summary>
public static class CommercialStageNames
{
    public const string EnDesarrollo = "En desarrollo";
    public const string CandidataBabyLeaf = "Candidata Baby Leaf";
    public const string BabyLeafApta = "Baby Leaf apta";
    public const string CosechaConvencional = "Cosecha convencional";
    public const string RiesgoFueraDeVentana = "Riesgo / fuera de ventana";

    /// <summary>Se muestra como estado predominante del lote ante empate de conteos.</summary>
    public const string Mixto = "Mixto";
}

/// <summary>Conteo de plantas activas por estado comercial.</summary>
public sealed record StageCountDto(string StageName, int Count);

/// <summary>
/// Celda del mapa del lote. Una celda puede representar una planta, una posición
/// vacía (gris claro) o una planta descartada (gris oscuro/patrón). "Posición vacía"
/// NO es un estado operativo: es la ausencia de planta en la posición configurada.
/// </summary>
public sealed record PlantCellDto(
    int Row,
    int Column,
    int? PlantId,
    string? CommercialStageName,
    PlantOperationalState? OperationalState,
    string CellKind); // plant | empty | discarded

public sealed record PlantHistoryEntryDto(
    DateTime ChangedAtUtc,
    string Source,
    string? PreviousState,
    string? NewState,
    string? Reason);

public sealed record PlantDetailDto(
    int PlantId,
    int Row,
    int Column,
    string? PhenologicalStageName,
    string? CommercialStageName,
    PlantOperationalState OperationalState,
    DateOnly? HarvestDate,
    DateOnly? DiscardDate,
    string? DiscardReason,
    decimal LotGdd,
    decimal? CurrentPh,
    decimal? CurrentEc,
    decimal? BabyLeafScore,
    decimal? GrowthRate,
    string? LastEvaluationResult,
    decimal? Confidence,
    DateTime? LastEvaluationAtUtc,
    bool HasImage,
    string? LastImagePath,
    string? LastImageFileName,
    IReadOnlyList<PlantHistoryEntryDto> History,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Regla híbrida de cosecha del lote: entrada en la ventana GDD Baby Leaf
/// MÁS porcentaje configurable de plantas "Baby Leaf apta". Si el porcentaje no
/// está definido, IsReady = null (decisión pendiente) y se expone la advertencia;
/// nunca se reemplaza por una constante oculta.
/// </summary>
public sealed record LotHarvestReadiness(
    bool GddInBabyLeafWindow,
    int AptaPlantCount,
    int TotalActivePlants,
    decimal AptaPercent,
    decimal? TargetPercent,
    bool? IsReady,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Estado agregado del lote (LOT-06): resumen, mapa, conteos, predominancia
/// (con Mixto ante empate) y readiness de cosecha. Vista-agnóstico: lo consumen
/// la página de estado de lote y los componentes reutilizables (forecasting,
/// dashboard y reports pueden reutilizarlo después).
/// </summary>
public sealed record LotStateDto(
    int LotId,
    string? Name,
    string CropTypeName,
    DateOnly SowingDate,
    decimal AreaM2,
    decimal GddAccumulated,
    string? StatusName,
    string? PhenologicalStageName,
    int? PhenologicalStageOrder,
    string? CommercialStageName,
    bool IsCommercialStageMixed,
    decimal? CurrentPh,
    decimal? CurrentEc,
    decimal? EcObjective,
    int? GridRows,
    int? GridColumns,
    int TotalConfiguredPositions,
    int TotalPlants,
    int ActivePlants,
    int HarvestedPlants,
    int DiscardedPlants,
    int EmptyPositions,
    IReadOnlyList<StageCountDto> CommercialCounts,
    IReadOnlyList<PlantCellDto> Cells,
    LotHarvestReadiness? HarvestReadiness,
    IReadOnlyList<string> Warnings);

/// <summary>Resultado de una corrida del flujo diario para un lote (LOT-07).</summary>
public sealed record LotDailyFlowResult(
    int LotId,
    decimal GddAccumulated,
    int? AppliedPhenologicalStageId,
    int PlantsEvaluated,
    int PlantsChanged,
    string? PredominantCommercialStageName,
    bool IsCommercialStageMixed,
    LotHarvestReadiness? HarvestReadiness,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Contrato de riesgo por planta que provee el módulo de anomalías (fase futura).
/// El agente de anomalies implementa este contrato; mientras no exista, se registra
/// NoPlantRiskProvider (sin riesgo), que el agente debe reemplazar registrando su
/// implementación después de la de lotes en el contenedor.
/// </summary>
public sealed record PlantRisk(int PlantId, string Reason, string? Source = null);

public interface IPlantRiskProvider
{
    Task<IReadOnlyDictionary<int, PlantRisk>> GetActiveRisksAsync(int lotId, CancellationToken ct = default);
}

/// <summary>Proveedor por defecto: sin riesgos activos (anomalies aún no implementado).</summary>
public sealed class NoPlantRiskProvider : IPlantRiskProvider
{
    public Task<IReadOnlyDictionary<int, PlantRisk>> GetActiveRisksAsync(int lotId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<int, PlantRisk>>(new Dictionary<int, PlantRisk>());
}