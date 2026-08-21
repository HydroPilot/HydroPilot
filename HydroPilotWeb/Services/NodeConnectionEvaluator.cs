using HydroPilotWeb.Models;

namespace HydroPilotWeb.Services;

/// <summary>
/// Cálculo puro del estado de conexión de un nodo (IOT-06).
/// NEVER_CONNECTED hasta la primera telemetría aceptada; luego ONLINE si la última
/// aceptación está dentro del intervalo esperado, DEGRADED dentro del factor de
/// degradación, OFFLINE más allá. El estado administrativo (Status) no equivale a online.
/// </summary>
public static class NodeConnectionEvaluator
{
    public static string Evaluate(
        DateTime? lastAcceptedAt,
        DateTime nowUtc,
        int expectedIntervalSeconds,
        int degradedIntervalFactor)
    {
        if (lastAcceptedAt is null)
            return TelemetryContract.ConnectionNeverConnected;

        if (expectedIntervalSeconds <= 0)
            expectedIntervalSeconds = 300;

        var age = nowUtc - lastAcceptedAt.Value;
        var interval = TimeSpan.FromSeconds(expectedIntervalSeconds);

        if (age <= interval)
            return TelemetryContract.ConnectionOnline;

        var degraded = TimeSpan.FromSeconds(Math.Max(degradedIntervalFactor, 1) * expectedIntervalSeconds);
        return age <= degraded
            ? TelemetryContract.ConnectionDegraded
            : TelemetryContract.ConnectionOffline;
    }
}