using HydroPilotWeb.Services.SimulationEnvironment.Model;

namespace HydroPilot.PiSimulator;

/// <summary>
/// Mesada virtual: tanque, plantas y clima del invernadero con el mismo modelo que el historial del entorno de
/// simulación (archivos enlazados de HydroPilotWeb/Services/SimulationEnvironment/Model):
/// - las plantas toman agua y nutrientes según su edad y el clima: con calor la CE sube, con frío baja, y el pH sube;
/// - cada dosis se mezcla de a poco (95 % a los 2 min);
/// - un flotante repone agua de red cuando el nivel baja del 85 %.
/// Con <see cref="Acceleration"/> el tiempo de las plantas y del clima corre más rápido (×60: una hora de plantas
/// por minuto) para ver en minutos lo que pasa en horas; la mezcla y la conversación con la web siguen en tiempo real.
/// La química del tanque es distinta a propósito de la que supone la web (que la aprende calibrando cada bomba).
/// </summary>
public sealed class Tank
{
    private const double StuckPh = 6.40;
    private static readonly TimeZoneInfo ArgentinaTime = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");

    private readonly object _gate = new();
    private readonly Random _random = new();
    private readonly NutrientTank _tank;
    private readonly Scenario _scenario;
    private int _plants;
    private DateTime _startUtc;
    private DateTime _simulatedUtc;
    private double _gdd;
    private double _temperature;
    private double _humidity;

    public Tank(double liters, double ph, double ec, Scenario scenario, int plants, double days, double acceleration)
    {
        _tank = new NutrientTank(liters, ph, ec);
        _scenario = scenario;
        _plants = plants;
        Acceleration = acceleration;
        _startUtc = DateTime.UtcNow;
        _simulatedUtc = _startUtc;
        StartDays = days;

        _gdd = GddOf(days, Local(_simulatedUtc));
        (_temperature, _humidity) = GreenhouseClimate.NormalAt(Local(_simulatedUtc), Anomaly);
    }

    /// <summary>GDD de un lote de <paramref name="days"/> días, con el clima normal de esos días.</summary>
    private static double GddOf(double days, DateTime localNow)
    {
        var gdd = 0.0;
        var today = DateOnly.FromDateTime(localNow);
        for (var d = 1; d <= Math.Ceiling(days); d++)
        {
            var day = GreenhouseClimate.Inside(GreenhouseClimate.Normal(today.AddDays(-d)), 0);
            gdd += GreenhouseClimate.DailyGdd(day.Max, day.Min) * Math.Min(1, days - (d - 1));
        }
        return gdd;
    }

    public int Plants
    {
        get
        {
            lock (_gate)
                return _plants;
        }
    }

    /// <summary>
    /// Lo que dice la web del lote del circuito: cuántas plantas hay (trasplantes, cortes) y, si el circuito recibió un
    /// lote (estaba vacío) o el lote cambió (menos días que antes), sus días desde la siembra.
    /// </summary>
    public void Sync(int plants, double days, bool newLot)
    {
        lock (_gate)
        {
            _plants = plants;
            if (!newLot)
                return;
            StartDays = days;
            _startUtc = _simulatedUtc;
            _gdd = GddOf(days, Local(_simulatedUtc));
        }
    }

    /// <summary>Cuántas veces más rápido corre el tiempo de las plantas y del clima.</summary>
    public double Acceleration { get; set; }

    public double StartDays { get; private set; }

    /// <summary>Ola de calor: 7 °C más que lo normal.</summary>
    private double Anomaly => _scenario == Scenario.Heat ? 7 : 0;

    /// <summary>Se llamó cuando el flotante repuso agua (litros), para avisarlo en la consola.</summary>
    public event Action<double>? Refilled;

    /// <summary>Avanza el tiempo real (la mezcla) y el simulado (plantas y clima).</summary>
    public void Step(double realSeconds)
    {
        double refilled = 0;
        lock (_gate)
        {
            var simulatedSeconds = realSeconds * Acceleration;
            var local = Local(_simulatedUtc);
            (_temperature, _humidity) = GreenhouseClimate.NormalAt(local, Anomaly);
            var uptake = LettuceUptake.For(_plants, _gdd, simulatedSeconds, local.TimeOfDay.TotalHours,
                _temperature, _humidity, _tank.Ec, _tank.Liters);
            _tank.Step(realSeconds, uptake);

            // Escenarios de prueba (en tiempo real, para que se vean siempre igual).
            if (_scenario == Scenario.PhRising)
                _tank.Nudge(1.0 * realSeconds / 3600, 0);
            if (_scenario == Scenario.EcFalling)
                _tank.Nudge(0, -0.6 * realSeconds / 3600);

            var day = GreenhouseClimate.Inside(GreenhouseClimate.Normal(DateOnly.FromDateTime(local)), Anomaly);
            _gdd += GreenhouseClimate.DailyGdd(day.Max, day.Min) * simulatedSeconds / 86_400;
            _simulatedUtc = _simulatedUtc.AddSeconds(simulatedSeconds);

            if (_tank.Level < 0.85)
                refilled = _tank.Refill();
        }
        if (refilled > 0)
            Refilled?.Invoke(refilled);
    }

    /// <summary>Agrega una dosis (ml de producto, o litros de agua) que se mezcla de a poco.</summary>
    public void Dose(string actuator, double amount)
    {
        lock (_gate)
            _tank.Dose(actuator, amount);
    }

    /// <summary>Cambio brusco (teclas): un derrame, una sobredosis a mano.</summary>
    public void Nudge(double ph, double ec)
    {
        lock (_gate)
            _tank.Nudge(ph, ec);
    }

    /// <summary>Reposición de nivel a mano (tecla r).</summary>
    public double Refill()
    {
        lock (_gate)
            return _tank.Refill();
    }

    /// <summary>Lo que miden las sondas: el valor real con ruido (o pegado, en el escenario sensor-pegado).</summary>
    public (double Ph, double Ec) Read()
    {
        lock (_gate)
        {
            var ph = _scenario == Scenario.StuckSensor ? StuckPh : _tank.Ph + Noise(0.012);
            return (Math.Round(ph, 2), Math.Round(Math.Max(0, _tank.Ec + Noise(0.012)), 2));
        }
    }

    /// <summary>Temperatura y humedad ambiente de la hora simulada (ciclo diario de Buenos Aires, adentro del invernadero).</summary>
    public (double Temperature, double Humidity) Climate()
    {
        lock (_gate)
            return (Math.Round(_temperature + Noise(0.15), 1), Math.Round(Math.Clamp(_humidity + Noise(1.2), 5, 100), 0));
    }

    /// <summary>Estado para la consola: litros, día del ciclo, GDD y hora simulada.</summary>
    public (double Liters, double Day, double Gdd, DateTime LocalTime) Status()
    {
        lock (_gate)
        {
            return (_tank.Liters, StartDays + (_simulatedUtc - _startUtc).TotalDays, _gdd, Local(_simulatedUtc));
        }
    }

    private static DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, ArgentinaTime);

    private double Noise(double amplitude) => (_random.NextDouble() * 2 - 1) * amplitude;
}
