namespace HydroPilotWeb.Models;

/// <summary>
/// Constantes estables del módulo de anomalías (plan 15 / ANO-01).
/// Catálogo cerrado de tipos, severidades y estados: se persisten tal cual y
/// son los mismos términos que consumen la UI, el contrato compartido con
/// notifications y los futuros consumidores (dashboard, reports, optimization).
/// </summary>
public static class AnomalyContract
{
    // --- Tipos estables de anomalía (catálogo cerrado, ANO-01) ---
    public const string TypePhFueraDeBanda = "PH_FUERA_DE_BANDA_OPERATIVA";
    public const string TypeCeFueraDeBanda = "CE_FUERA_DE_BANDA_OPERATIVA";
    public const string TypeTemperaturaFueraDeBanda = "TEMPERATURA_FUERA_DE_BANDA";
    public const string TypeHumedadFueraDeBanda = "HUMEDAD_FUERA_DE_BANDA";

    // --- Severidades estables (ANO-01) ---
    // Advertencia: lectura sospechosa aislada (registro sin episodio crítico).
    // Crítica: episodio con N lecturas consecutivas fuera de banda (N = 2 por defecto, configurable).
    public const string SeverityAdvertencia = "Advertencia";
    public const string SeverityCritica = "Crítica";

    // --- Estados del episodio (ciclo interno) ---
    // Seguimiento: 1..N-1 lecturas consecutivas fuera de banda (registro sin episodio crítico).
    // Abierta: N o más consecutivas (episodio crítico).
    // Reconocida: acción explícita del usuario; sigue siendo un riesgo activo.
    // Resuelta: lecturas normales consecutivas o cierre manual.
    public const string StatusSeguimiento = "Seguimiento";
    public const string StatusAbierta = "Abierta";
    public const string StatusReconocida = "Reconocida";
    public const string StatusResuelta = "Resuelta";

    // --- Estados del contrato compartido con notifications (plan 02, 3 estados) ---
    public const string ContractOpen = "abierta";
    public const string ContractAcknowledged = "reconocida";
    public const string ContractResolved = "resuelta";

    public static string ToContractStatus(string status) => status switch
    {
        StatusSeguimiento or StatusAbierta => ContractOpen,
        StatusReconocida => ContractAcknowledged,
        _ => ContractResolved,
    };

    // --- Origen de los datos ---
    public const string OriginTelemetria = "telemetria";
    public const string OriginManual = "manual";

    // --- Nombres de tipo de sensor consumidos por las reglas (catálogo SensorType de IoT) ---
    public const string SensorTypePh = "pH";
    public const string SensorTypeCe = "CE";
    public const string SensorTypeTemperatura = "Temperatura";
    public const string SensorTypeHumedad = "Humedad";
}