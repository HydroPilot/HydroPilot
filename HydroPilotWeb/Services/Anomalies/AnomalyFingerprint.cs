using System.Security.Cryptography;
using System.Text;

namespace HydroPilotWeb.Services.Anomalies;

/// <summary>
/// Fingerprint de deduplicación de episodios (ANO-03): estable por corrida de
/// lecturas — (regla, lote, sensor, primera observación) — de modo que el barrido
/// tras un reinicio del worker reencuentre el MISMO episodio y lo actualice en
/// lugar de duplicarlo. Un cambio de lote o de sensor produce otro fingerprint.
/// </summary>
public static class AnomalyFingerprint
{
    public static string Compute(int lotId, int? sensorId, string ruleCode, DateTime firstObservationUtc)
    {
        var canonical = $"{ruleCode}|{lotId}|{sensorId?.ToString() ?? "-"}|{firstObservationUtc.Ticks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}