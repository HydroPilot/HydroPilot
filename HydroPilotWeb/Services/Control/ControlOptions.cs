namespace HydroPilotWeb.Services.Control;

/// <summary>Tiempos del control de pH/CE (sección "Control" de appsettings).</summary>
public sealed class ControlOptions
{
    public const string SectionName = "Control";

    /// <summary>Minutos que vive un comando: pasado ese tiempo la nube no lo entrega y la Pi no lo ejecuta.</summary>
    public int CommandTtlMinutes { get; set; } = 5;

    /// <summary>Margen después del vencimiento para que la Pi informe el resultado de un comando entregado.</summary>
    public int UnconfirmedGraceMinutes { get; set; } = 2;

    /// <summary>Cada cuántos segundos se vencen comandos y se registra el nivel vigente de cada lote.</summary>
    public int SweepIntervalSeconds { get; set; } = 60;

    /// <summary>Minutos de mezcla después de una dosis: el motor no vuelve a dosificar ni mide el efecto antes.</summary>
    public int MixingMinutes { get; set; } = 10;

    /// <summary>Minutos sin dosificar en sentido contrario (pH Up después de pH Down, agua después de nutrientes).</summary>
    public int DirectionChangeMinutes { get; set; } = 30;

    /// <summary>Si la Pi no pidió comandos en estos minutos, el motor no crea dosis (vencerían sin ejecutarse).</summary>
    public int PollStaleMinutes { get; set; } = 2;

    /// <summary>Minutos de lecturas para la tendencia (lógica difusa) y la deriva al medir el efecto.</summary>
    public int TrendMinutes { get; set; } = 30;

    /// <summary>Dosis seguidas sin efecto que pausan el control automático de esa magnitud.</summary>
    public int NoEffectDoses { get; set; } = 3;

    /// <summary>Minutos después de la mezcla para conseguir una lectura y medir el efecto; después se da por sin datos.</summary>
    public int EffectWaitMinutes { get; set; } = 60;

    /// <summary>Horas de conversación que muestra la vista Control.</summary>
    public int TimelineHours { get; set; } = 24;

    public int TimelineMaxEntries { get; set; } = 40;
}
