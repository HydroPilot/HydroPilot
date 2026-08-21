namespace HydroPilotWeb.Services.Anomalies;

/// <summary>
/// Opciones configurables del módulo de anomalías (plan 15 / ANO-01). Se bindean
/// desde la sección "Anomalies" de la configuración; los valores por defecto están
/// documentados acá (sin constantes ocultas). El catálogo por cultivo
/// (AnomalyRuleCatalog) puede sobreescribir ConsecutiveToOpen y CooldownMinutes por regla.
/// </summary>
public sealed class AnomalyOptions
{
    public const string SectionName = "Anomalies";

    /// <summary>Intervalo del barrido del worker de episodios, en segundos.</summary>
    public int SweepIntervalSeconds { get; set; } = 30;

    /// <summary>Ventana retrospectiva de lecturas evaluadas en cada barrido, en horas.</summary>
    public int SweepWindowHours { get; set; } = 24;

    /// <summary>
    /// Lecturas consecutivas fuera de banda que abren un episodio crítico.
    /// Menos de N consecutivas = registro en Seguimiento (sin episodio crítico).
    /// Umbral documentado del plan 15 (ANO-01): arranque 2.
    /// </summary>
    public int DefaultConsecutiveToOpen { get; set; } = 2;

    /// <summary>
    /// Lecturas normales CONSECUTIVAS posteriores a la corrida anómala que resuelven
    /// el episodio (ANO-04: "recuperación con lecturas normales consecutivas").
    /// </summary>
    public int DefaultRecoveryCount { get; set; } = 2;

    /// <summary>Cooldown en minutos: tras resolver un episodio, no se abre uno nuevo de la misma regla dentro de este lapso.</summary>
    public int DefaultCooldownMinutes { get; set; } = 30;

    /// <summary>
    /// Tolerancia en minutos para reconocer una corrida que arrancó antes de la ventana
    /// del barrido como continuación del episodio ya abierto (evita duplicados en el
    /// borde de ventana). No es normalidad: solo alinea la corrida con su episodio.
    /// </summary>
    public int ContinuationGraceMinutes { get; set; } = 1;
}