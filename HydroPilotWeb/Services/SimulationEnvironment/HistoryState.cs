using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Control;
using HydroPilotWeb.Models.Notifications;
using HydroPilotWeb.Services.Anomalies;
using HydroPilotWeb.Services.Control;
using HydroPilotWeb.Services.SimulationEnvironment.Model;

namespace HydroPilotWeb.Services.SimulationEnvironment;

/// <summary>Una mesada durante el recorrido: su lote en curso, el tanque, las bombas y lo que sabe la web de ella.</summary>
internal sealed class BenchRun
{
    public BenchRun(SimBench bench, List<PlannedLot> plans, Random rng)
    {
        Bench = bench;
        Plans = plans;
        TemperatureOffset = Math.Round(-0.3 + rng.NextDouble() * 0.6, 2);
        foreach (var actuator in bench.Actuators)
        {
            Pumps[actuator.Kind] = new PumpRun(actuator)
            {
                Effect = ControlContract.DefaultFor(actuator.Kind)?.Effect,
            };
        }
        foreach (var sensor in bench.Sensors.Values)
            Calibrations[sensor.Id] = null;
    }

    public SimBench Bench { get; }
    public List<PlannedLot> Plans { get; }
    public int NextPlan { get; set; }
    public LotRun? Lot { get; set; }
    public NutrientTank? Tank { get; set; }
    public string Mode { get; set; } = ControlContract.ModeManual;
    public double TemperatureOffset { get; }
    public Dictionary<string, PumpRun> Pumps { get; } = [];
    public List<CommandRun> Commands { get; } = [];
    public Dictionary<string, ControlPause> OpenPauses { get; } = [];
    public Dictionary<string, DateTime> LastResume { get; } = [];
    public Dictionary<int, DateTime?> Calibrations { get; } = [];

    public long Sequence { get; set; }
    public DateTime? LastConnectionUtc { get; set; }
    public DateTime? LastAcceptedUtc { get; set; }
    public DateTime? LastRejectedUtc { get; set; }
    public DateTime? LastPollUtc { get; set; }

    // --- Incidentes de hardware ---
    public double PhProbeOffset { get; set; }
    public DateTime? OfflineFrom { get; set; }
    public DateTime? OfflineUntil { get; set; }
    public List<PendingBatch> Buffer { get; } = [];
    public string? BrokenPump { get; set; }
    public DateOnly? ToppedUpOn { get; set; }
    public Dictionary<string, NotificationAlert> PauseAlerts { get; } = [];

    public bool IsOffline(DateTime t) => OfflineFrom is { } from && OfflineUntil is { } until && t >= from && t < until;

    // --- Lecturas que mira el control (últimas usables) ---
    private readonly Dictionary<string, List<SensorReading>> _recent = new()
    {
        [AnomalyContract.SensorTypePh] = [],
        [AnomalyContract.SensorTypeCe] = [],
    };

    public void Remember(string type, SensorReading reading)
    {
        var list = _recent[type];
        list.Add(reading);
        var since = reading.ObservedAtUtc.AddHours(-3);
        if (list.Count > 40 && list[0].ObservedAtUtc < since)
            list.RemoveAll(r => r.ObservedAtUtc < since);
    }

    public void ClearReadings()
    {
        foreach (var list in _recent.Values)
            list.Clear();
    }

    /// <summary>Las últimas lecturas usables de una magnitud, de la más nueva a la más vieja (como la web).</summary>
    public IReadOnlyList<SolutionReading> Latest(string type, int take) =>
        _recent[type].AsEnumerable().Reverse().Take(take)
            .Select(r => new SolutionReading(type, r.Quality, r.Value, r.ObservedAtUtc))
            .ToList();

    public IReadOnlyList<ControlEngine.Point> Points(string type, DateTime since, DateTime until) =>
        _recent[type].Where(r => r.ObservedAtUtc >= since && r.ObservedAtUtc <= until)
            .Select(r => new ControlEngine.Point(r.ObservedAtUtc, r.Value))
            .ToList();

    public SensorReading? FirstAfter(string type, DateTime at) =>
        _recent[type].FirstOrDefault(r => r.ObservedAtUtc >= at);

    public SensorReading? Last(string type) => _recent[type].Count > 0 ? _recent[type][^1] : null;
}

/// <summary>Una bomba: su fila (Id, límites) y la calibración que va aprendiendo la web.</summary>
internal sealed class PumpRun(Actuator actuator)
{
    public Actuator Actuator { get; } = actuator;
    public decimal? Effect { get; set; }
    public int Samples { get; set; }
}

/// <summary>Un comando y lo que el recorrido necesita para medir su efecto.</summary>
internal sealed class CommandRun(ActuatorCommand command, string kind, DateTime? triggerAtUtc)
{
    public ActuatorCommand Command { get; } = command;
    public string Kind { get; } = kind;
    public DateTime? TriggerAtUtc { get; } = triggerAtUtc;
}

/// <summary>Lo que pasó en el tanque desde la última evaluación de las plantas (para el estrés del día).</summary>
internal sealed class DayStats
{
    private double _ph, _ec, _temperature, _humidity;
    private int _phCount, _ecCount, _temperatureCount, _humidityCount;

    public double MaxTemperature { get; private set; } = double.MinValue;
    public double MaxEc { get; private set; }
    public double MaxPh { get; private set; }
    public double MinPh { get; private set; } = double.MaxValue;

    public void AddPh(double value) { _ph += value; _phCount++; MaxPh = Math.Max(MaxPh, value); MinPh = Math.Min(MinPh, value); }
    public void AddEc(double value) { _ec += value; _ecCount++; MaxEc = Math.Max(MaxEc, value); }
    public void AddTemperature(double value) { _temperature += value; _temperatureCount++; MaxTemperature = Math.Max(MaxTemperature, value); }
    public void AddHumidity(double value) { _humidity += value; _humidityCount++; }

    public double? MeanPh => _phCount > 0 ? _ph / _phCount : null;
    public double? MeanEc => _ecCount > 0 ? _ec / _ecCount : null;
    public double? MeanTemperature => _temperatureCount > 0 ? _temperature / _temperatureCount : null;
    public double? MeanHumidity => _humidityCount > 0 ? _humidity / _humidityCount : null;
}

/// <summary>Período de un nivel guardado: con qué base se mide cuánto se apartó el lote (como RecipeService).</summary>
internal sealed class SetpointPeriod(DateTime validFromUtc, decimal? basePh, decimal? baseEc)
{
    public DateTime ValidFromUtc { get; } = validFromUtc;
    public decimal? BasePh { get; } = basePh;
    public decimal? BaseEc { get; } = baseEc;
    public decimal PhSum { get; set; }
    public int PhCount { get; set; }
    public decimal EcSum { get; set; }
    public int EcCount { get; set; }
}

/// <summary>Un lote durante el recorrido.</summary>
internal sealed class LotRun(PlannedLot plan, Lot lot, CircuitAssignment assignment, PhenologicalStage stage)
{
    public PlannedLot Plan { get; } = plan;
    public Lot Lot { get; } = lot;
    public CircuitAssignment Assignment { get; } = assignment;
    public PhenologicalStage Stage { get; set; } = stage;
    public List<PlantRun> Plants { get; } = [];

    public DateTime StartUtc { get; init; }
    public DateTime? EndUtc { get; set; }
    public bool Closed { get; set; }
    public double PhBias { get; init; }
    public double EcBias { get; set; }

    public decimal Gdd { get; set; }
    public decimal GddAtLastFlow { get; set; }
    public Dictionary<DateOnly, (double Max, double Min)> GddDays { get; } = [];

    /// <summary>Bomba que se va a tapar (o bidón que se va a vaciar) en la próxima dosis desde esa fecha.</summary>
    public (string Kind, DateTime From)? ArmedIncident { get; set; }

    /// <summary>Episodio de anomalía en curso por magnitud y última resolución (para el cooldown).</summary>
    public Dictionary<string, EpisodeRun> OpenEpisodes { get; } = [];
    public Dictionary<string, DateTime> LastResolved { get; } = [];

    /// <summary>Alerta de cosecha inminente del lote (una por lote).</summary>
    public NotificationAlert? HarvestAlert { get; set; }

    public LotRecipe? Recipe { get; set; }
    public LotSetpointHistory? LastSetpoint { get; set; }
    public List<SetpointPeriod> Periods { get; } = [];

    public DayStats Day { get; set; } = new();
    public DateOnly? EvaluatedOn { get; set; }
    public DateOnly? HarvestCheckedOn { get; set; }
    public DateOnly? LastBabyLeafCut { get; set; }
    public DateOnly? WindowClosedOn { get; set; }
    public List<(DateOnly Day, decimal Score)> DailyScores { get; } = [];
    public List<RecipeRules.ScoreSample> ScoreSamples { get; } = [];

    public decimal? LastPh { get; set; }
    public decimal? LastEc { get; set; }

    /// <summary>Días seguidos con la solución tibia (≥ 24 °C, Pythium).</summary>
    public int WarmDays { get; set; }
    public double RootHealth { get; set; } = 1;

    /// <summary>Hitos del ciclo (para el resumen y las notificaciones).</summary>
    public List<(DateTime AtUtc, string Kind, string Detail)> Events { get; } = [];

    public void AddTemperature(DateOnly localDate, double value)
    {
        GddDays[localDate] = GddDays.TryGetValue(localDate, out var day)
            ? (Math.Max(day.Max, value), Math.Min(day.Min, value))
            : (value, value);
    }

    public void Remember(string type, SensorReading reading)
    {
        var period = Periods.Count > 0 ? Periods[^1] : null;
        if (type == AnomalyContract.SensorTypePh)
        {
            Day.AddPh((double)reading.Value);
            if (period is { BasePh: { } basePh })
            {
                period.PhSum += reading.Value - basePh;
                period.PhCount++;
            }
        }
        else
        {
            Day.AddEc((double)reading.Value);
            if (period is { BaseEc: { } baseEc })
            {
                period.EcSum += reading.Value - baseEc;
                period.EcCount++;
            }
        }
    }

    /// <summary>Con qué pH y CE trabajó el lote respecto de los niveles de su etapa (promedio pesado por lecturas).</summary>
    public (decimal? Ph, decimal? Ec) MeasuredOffsets()
    {
        var ph = Periods.Sum(p => p.PhCount);
        var ec = Periods.Sum(p => p.EcCount);
        return (
            ph > 0 ? Math.Round(Periods.Sum(p => p.PhSum) / ph, 4) : null,
            ec > 0 ? Math.Round(Periods.Sum(p => p.EcSum) / ec, 4) : null);
    }

    public int AliveCount => Plants.Count(p => p.Active);
}

/// <summary>Un episodio de anomalía abierto, las lecturas normales que lleva y su alerta.</summary>
internal sealed class EpisodeRun(AnomalyEvent anomaly, ResolvedAnomalyRule rule)
{
    public AnomalyEvent Event { get; } = anomaly;
    public ResolvedAnomalyRule Rule { get; } = rule;
    public int Normals { get; set; }
    public bool Answered { get; set; }
    public NotificationAlert? Alert { get; set; }
}

internal sealed record ResolvedAnomalyRule(string Code, string Name, int ConsecutiveToOpen, int CooldownMinutes);

/// <summary>Lecturas de un paso que la Pi no pudo mandar (sin conexión): las manda al volver.</summary>
internal sealed record PendingBatch(DateTime ObservedAtUtc, long Sequence, List<(SimSensor Sensor, decimal Value)> Values);

/// <summary>Una planta (celda de siembra densa) durante el recorrido.</summary>
internal sealed class PlantRun(Plant plant, double vigor)
{
    public Plant Plant { get; } = plant;
    public double Vigor { get; } = vigor;

    /// <summary>Crecimiento acumulado: GDD × eficiencia. Una planta perfecta llega a 300 en el óptimo Baby Leaf.</summary>
    public double Size { get; set; }

    /// <summary>Hojas dañadas (tipburn, %).</summary>
    public double Damage { get; set; }

    /// <summary>Clorosis por pH alto (%).</summary>
    public double Yellowing { get; set; }

    /// <summary>Días desde que se infectó con Pythium (null: sana).</summary>
    public int? InfectedDays { get; set; }

    public Queue<double> RecentEfficiency { get; } = new();

    /// <summary>Calidad promedio de los días que vivió (forma de la planta: compacta o estirada).</summary>
    public double QualitySum { get; set; }
    public int QualityDays { get; set; }
    public double AverageQuality => QualityDays > 0 ? QualitySum / QualityDays : 1;
    public decimal? LastScore { get; set; }
    public bool WasApt { get; set; }
    public bool CutAsBabyLeaf { get; set; }

    public bool Active => Plant.OperationalState == PlantOperationalState.Activa;
}
