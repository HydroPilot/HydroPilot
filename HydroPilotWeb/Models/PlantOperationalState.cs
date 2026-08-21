namespace HydroPilotWeb.Models;

/// <summary>
/// Estado operativo de una planta (excluyente, plan 09).
/// Una planta cosechada o descartada sale del motor diario: su último estado
/// comercial queda congelado como información histórica.
/// "Posición vacía" NO es un estado operativo: es la ausencia de planta en una
/// posición configurada de la grilla.
/// </summary>
public enum PlantOperationalState
{
    /// <summary>Planta en producción, participa del flujo diario.</summary>
    Activa = 0,

    /// <summary>Levantada (Baby Leaf o convencional). Su etapa comercial queda congelada.</summary>
    Cosechada = 1,

    /// <summary>Baja por bolting, anomalía o fuera de ventana. Conserva su historial.</summary>
    Descartada = 2
}