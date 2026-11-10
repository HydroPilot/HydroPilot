namespace HydroPilotWeb.Services.SimulationEnvironment;

/// <summary>
/// Entorno de simulación (perfil "simulacion", base hydropilot_sim): un historial de lotes de ejemplo generado con la
/// misma lógica de la app, para probar todo con datos realistas y la Pi virtual en tiempo real. Apagado por defecto;
/// solo corre contra LocalDB (nunca contra Azure). Ver notas/simulacion/entorno-simulacion.md.
/// </summary>
public sealed class SimulationEnvironmentOptions
{
    public const string SectionName = "SimulationEnvironment";

    public bool Enabled { get; set; }

    /// <summary>Meses de historial hacia atrás desde hoy.</summary>
    public int HistoryMonths { get; set; } = 24;

    /// <summary>Cada cuántos minutos mandaba lecturas la Pi en el historial.</summary>
    public int ReadingMinutes { get; set; } = 10;

    /// <summary>Semilla: el mismo número da el mismo historial.</summary>
    public int Seed { get; set; } = 5301;

    /// <summary>
    /// El dataset de la demo (el Excel con los lotes), relativo a la carpeta del proyecto. Está en el repositorio: con él
    /// se arma el historial al arrancar y con "Regenerar historial"; si no está, el historial es al azar.
    /// </summary>
    public string DatasetFile { get; set; } = Path.Combine("SimulationData", "dataset-demo.xlsx");
}
