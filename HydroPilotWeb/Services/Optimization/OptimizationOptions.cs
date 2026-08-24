namespace HydroPilotWeb.Services.Optimization;

/// <summary>
/// Opciones configurables del módulo de optimización (sección "Optimization").
/// El umbral de rentabilidad es CONFIGURABLE y documentado (plan 16: la
/// diferencia superior al 10% es el default aprobado; los límites de pH usan
/// CropType, nunca una regla porcentual).
/// </summary>
public sealed class OptimizationOptions
{
    public const string SectionName = "Optimization";

    /// <summary>
    /// Diferencia de rentabilidad mínima (puntos porcentuales) para recomendar
    /// cambiar de destino. Default 10% (plan 16, decisión de dominio).
    /// </summary>
    public decimal ProfitabilityThresholdPercent { get; set; } =
        Models.Optimization.OptimizationContract.DefaultProfitabilityThresholdPercent;

    /// <summary>Ventana de frescura para lecturas de pH/CE (minutos). Más vieja ⇒ bloquea (OPT-02).</summary>
    public int MaxReadingAgeMinutes { get; set; } =
        Models.Optimization.OptimizationContract.DefaultMaxReadingAgeMinutes;

    /// <summary>Horas de vigencia de una recomendación PENDING antes de marcarla EXPIRED.</summary>
    public int PendingExpirationHours { get; set; } =
        Models.Optimization.OptimizationContract.DefaultPendingExpirationHours;

    /// <summary>
    /// Cantidad de lecturas consecutivas consistentes para pasar de confianza
    /// baja ("verificar") a confianza operativa (OPT-02: desbalance vs dos
    /// lecturas consecutivas).
    /// </summary>
    public int ConsistentReadingCount { get; set; } =
        Models.Optimization.OptimizationContract.DefaultConsistentReadingCount;

    /// <summary>Lecturas recientes que se consideran por magnitud para pH/CE.</summary>
    public int ReadingLookbackCount { get; set; } = 5;
}