using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Control;
using HydroPilotWeb.Models.Notifications;
using HydroPilotWeb.Models.Optimization;

namespace HydroPilotWeb.Services.SimulationEnvironment;

/// <summary>Sensor de un nodo: tipo (pH, CE, Temperatura, Humedad), unidad y clave técnica.</summary>
public sealed record SimSensor(int Id, string Type, int? UnitId, string UnitSymbol, string TechnicalKey);

/// <summary>
/// Una mesada del invernadero = un circuito: su Pi, sus sensores (pH y CE; la primera mesada lleva además los de
/// temperatura y humedad del invernadero) y sus bombas. Cada mesada tiene un lote por vez.
/// </summary>
public sealed record SimBench(
    int NodeId,
    string Identifier,
    string Letter,
    IReadOnlyDictionary<string, SimSensor> Sensors,
    IReadOnlyList<Actuator> Actuators,
    int CircuitId = 0,
    string CircuitName = "")
{
    /// <summary>La mesada que manda la temperatura y la humedad del invernadero (una sola).</summary>
    public bool HasAmbient => Sensors.ContainsKey(AnomalyContract.SensorTypeTemperatura);
}

public sealed record SimUser(int Id, string Email, string Name, string Role);

/// <summary>
/// Lo que ya existe en la base (catálogos, infraestructura y usuarios) y el historial necesita: el generador es puro y
/// trabaja sobre esto, sin leer la base.
/// </summary>
public sealed record SimWorld(
    int GreenhouseId,
    TimeZoneInfo TimeZone,
    CropType Crop,
    IReadOnlyList<PhenologicalStage> Stages,
    IReadOnlyDictionary<string, int> CommercialStageIds,
    BabyLeafConfig Config,
    IReadOnlyList<BabyLeafCriterion> Criteria,
    IReadOnlyDictionary<string, int> LotStatusIds,
    IReadOnlyDictionary<string, AnomalyRuleCatalog> AnomalyRules,
    IReadOnlyList<SimBench> Benches,
    SimUser Producer,
    SimUser Operator,
    IReadOnlyList<SimUser> Recipients,
    decimal BabyLeafGramsPerPlant,
    decimal ConventionalGramsPerPlant);

/// <summary>Todo lo que genera el historial, con sus Id ya asignados (se inserta tal cual, en orden de dependencias).</summary>
public sealed class HistoryData
{
    public List<Lot> Lots { get; } = [];
    public List<CircuitAssignment> Assignments { get; } = [];
    public List<Plant> Plants { get; } = [];
    public List<PlantStageHistory> StageHistories { get; } = [];
    public List<PlantImage> Images { get; } = [];
    public List<PlantImageAnalysis> Analyses { get; } = [];
    public List<BabyLeafEvaluation> Evaluations { get; } = [];
    public List<TelemetryBatch> Batches { get; } = [];
    public List<SensorReading> Readings { get; } = [];
    public List<TelemetryRejection> Rejections { get; } = [];
    public List<ActuatorCommand> Commands { get; } = [];
    public List<LotSetpointHistory> Setpoints { get; } = [];
    public List<LotRecipe> Recipes { get; } = [];
    public List<ControlPause> Pauses { get; } = [];
    public List<AnomalyEvent> Anomalies { get; } = [];
    public List<NotificationAlert> Alerts { get; } = [];
    public List<NotificationDelivery> Deliveries { get; } = [];
    public List<NotificationAttempt> Attempts { get; } = [];
    public List<DailyWeatherForecast> Forecasts { get; } = [];
    public List<WeatherRecord> Weather { get; } = [];
    public List<CostPriceCatalog> Prices { get; } = [];

    /// <summary>Estado final de cada mesada: su circuito (modo), la Pi (última señal), las bombas (calibración) y sensores.</summary>
    public List<NodeFinalState> Nodes { get; } = [];

    /// <summary>Parámetros del sistema (Ajustes &gt; Parámetros) con los que se valorizó el historial.</summary>
    public Dictionary<string, string> Settings { get; } = [];

    /// <summary>Resumen para el log y la vista de mantenimiento.</summary>
    public List<string> Story { get; } = [];
}

public sealed record NodeFinalState(
    int NodeId,
    string ControlMode,
    DateTime? LastConnectionUtc,
    DateTime? LastAcceptedUtc,
    DateTime? LastRejectedUtc,
    DateTime? LastCommandPollUtc,
    string FirmwareVersion,
    IReadOnlyDictionary<int, (decimal? Effect, int Samples)> Actuators,
    IReadOnlyDictionary<int, DateTime?> SensorCalibrations,
    int CircuitId = 0);
