using System.Collections.Concurrent;

namespace HydroPilotWeb.Services.Lotes;

/// <summary>
/// Versiones en memoria para invalidar en el acto las vistas cacheadas de lotes,
/// plantas, tablero general, parámetros e historial cuando cambia el estado
/// de una planta o de un lote.
/// </summary>
public static class LotViewCache
{
    private static long _globalVersion = 1;
    private static long _lotsDigestVersion = 1;
    private static readonly ConcurrentDictionary<int, long> LotVersions = new();
    private static readonly ConcurrentDictionary<int, long> PlantVersions = new();

    public static long GlobalVersion => Interlocked.Read(ref _globalVersion);

    public static long LotsDigestVersion => Interlocked.Read(ref _lotsDigestVersion);

    public static long GetLotVersion(int lotId) =>
        LotVersions.GetOrAdd(lotId, 1L);

    public static long GetPlantVersion(int plantId) =>
        PlantVersions.GetOrAdd(plantId, 1L);

    public static void InvalidateAll()
    {
        Interlocked.Increment(ref _globalVersion);
        Interlocked.Increment(ref _lotsDigestVersion);
    }

    public static void InvalidateLot(int lotId)
    {
        LotVersions.AddOrUpdate(lotId, 2L, static (_, current) => current + 1);
        Interlocked.Increment(ref _lotsDigestVersion);
    }

    public static void InvalidatePlant(int plantId, int? lotId = null)
    {
        PlantVersions.AddOrUpdate(plantId, 2L, static (_, current) => current + 1);
        if (lotId is > 0)
        {
            InvalidateLot(lotId.Value);
        }
        else
        {
            Interlocked.Increment(ref _lotsDigestVersion);
        }
    }
}
