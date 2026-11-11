namespace HydroPilotWeb.Services.SimulationEnvironment;

/// <summary>
/// Entorno de simulación: un historial de lotes de ejemplo (los del dataset de la demo) generado con la misma lógica de la
/// app, para probar todo con datos realistas y la Pi virtual en tiempo real. Desde 2026-11-10 lo enciende appsettings.json
/// y corre con el dotnet run normal (LocalDB, base hydropilot_sim) y en Azure (su base); antes, solo el perfil
/// "simulacion" contra LocalDB. Ver notas/simulacion/entorno-simulacion.md.
/// </summary>
public sealed class SimulationEnvironmentOptions
{
    public const string SectionName = "SimulationEnvironment";

    public bool Enabled { get; set; }

    /// <summary>Meses de historial hacia atrás desde hoy.</summary>
    public int HistoryMonths { get; set; } = 24;

    /// <summary>
    /// Cada cuántos minutos manda lecturas la Pi en el historial: una por hora desde 2026-11-10 (antes 10). El tanque y
    /// las plantas se calculan igual cada 10 minutos. Es el intervalo esperado del nodo del entorno.
    /// </summary>
    public int ReadingMinutes { get; set; } = 60;

    /// <summary>Semilla: el mismo número da el mismo historial.</summary>
    public int Seed { get; set; } = 5301;

    /// <summary>
    /// El dataset de la demo (el Excel con los lotes), relativo a la carpeta del proyecto. Está en el repositorio: con él
    /// se arma el historial al arrancar y con "Regenerar historial"; si no está, el historial es al azar.
    /// </summary>
    public string DatasetFile { get; set; } = Path.Combine("SimulationData", "dataset-demo.xlsx");
}
