namespace HydroPilotWeb.Services.Optimization;

/// <summary>
/// Opciones del módulo de optimización (sección "Optimization"). Los mínimos para recomendar un cambio
/// de estrategia no van acá: son parámetros del negocio (Ajustes &gt; Parámetros).
/// </summary>
public sealed class OptimizationOptions
{
    public const string SectionName = "Optimization";

    /// <summary>Recalcular solas las recomendaciones de los lotes en curso.</summary>
    public bool SweepEnabled { get; set; } = true;

    /// <summary>Cada cuántos minutos se recalculan (además de al abrir la vista).</summary>
    public int SweepIntervalMinutes { get; set; } = 30;
}
