namespace HydroPilotWeb.Services.Optimization;

/// <summary>
/// Hook de bloqueo por anomalías abiertas del lote (plan 02, contrato "Evento de
/// anomalía"; plan 16 dependencia Anomalías).
///
/// El módulo de anomalies implementa este contrato; mientras no exista
/// <c>AnomalyEvent</c> en la base, se registra <see cref="NoOpenAnomalyProvider"/>
/// (sin anomalías) y Optimization continúa. El agente de anomalies debe registrar
/// su implementación DESPUÉS de esta línea en DI (último registro gana en el
/// contenedor, mismo patrón que IPlantRiskProvider en lotes).
///
/// Cuando haya anomalías abiertas, la recomendación química del lote se genera
/// como BLOCKED con motivo visible ("anomalía abierta: revisar antes de actuar")
/// en lugar de sugerir cambios sobre una solución bajo sospecha.
/// </summary>
public interface IOpenAnomalyProvider
{
    /// <summary>Devuelve true si el lote tiene episodios de anomalía abiertos.</summary>
    Task<bool> HasOpenAnomaliesAsync(int lotId, CancellationToken ct = default);
}

/// <summary>Proveedor por defecto: sin anomalías abiertas (anomalies aún no implementado).</summary>
public sealed class NoOpenAnomalyProvider : IOpenAnomalyProvider
{
    public Task<bool> HasOpenAnomaliesAsync(int lotId, CancellationToken ct = default)
        => Task.FromResult(false);
}