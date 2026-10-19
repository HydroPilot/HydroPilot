namespace HydroPilotWeb.Services.Lotes;

/// <summary>
/// Ciclos cerrados de un cultivo, con la lógica de Lotes (BabyLeafOutlookService): Baby Leaf aptas en el
/// óptimo, pérdidas y GDD diario promedio de cada lote. Lo usan la previsión de Simulación y la receta aprendida.
/// </summary>
public interface ILotHistorySource
{
    Task<IReadOnlyList<LotCycleOutcome>> GetCycleOutcomesAsync(int cropTypeId, CancellationToken ct = default);
}
