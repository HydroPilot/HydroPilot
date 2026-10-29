namespace HydroPilotWeb.Services.SimulationEnvironment;

/// <summary>
/// Pausa del trabajo de fondo mientras se regenera el historial desde Admin > Mantenimiento. El historial se carga con
/// sus Id desde 1: cualquier fila que agreguen en ese rato los barridos (errores de sensores, anomalías, control,
/// notificaciones, conexión de nodos, clima) o la API (lecturas de la Pi, comandos) choca con las del historial. Cada
/// vuelta de trabajo entra con <see cref="TryEnter"/> (si devuelve null, se saltea) y la regeneración espera a que
/// terminen las que ya estaban en curso. Al arrancar no hace falta: el historial se genera antes de que corran.
/// </summary>
public static class RegenerationPause
{
    private static int _running;
    private static volatile bool _paused;

    public static bool Paused => _paused;

    /// <summary>Entra a una vuelta de trabajo; null si se está regenerando el historial (no hacer nada).</summary>
    public static IDisposable? TryEnter()
    {
        Interlocked.Increment(ref _running);
        if (_paused)
        {
            Interlocked.Decrement(ref _running);
            return null;
        }
        return new Lease();
    }

    /// <summary>Lo mismo, pero espera a que termine la regeneración (para lo que no se puede saltear).</summary>
    public static async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        while (true)
        {
            if (TryEnter() is { } lease)
                return lease;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    /// <summary>Frena las vueltas nuevas y espera a que terminen las que están en curso.</summary>
    internal static async Task PauseAsync(CancellationToken ct)
    {
        _paused = true;
        while (Volatile.Read(ref _running) > 0)
            await Task.Delay(100, ct);
    }

    internal static void Resume() => _paused = false;

    private sealed class Lease : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref _running);
        }
    }
}
