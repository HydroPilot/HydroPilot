namespace HydroPilotWeb.Services.SimulationEnvironment.Model;

/// <summary>
/// Clima adentro del invernadero (Buenos Aires) para el entorno de simulación. Lógica pura: la usan el historial de
/// la web y la Pi virtual (tools/PiSimulator compila este mismo archivo).
/// - Afuera: normales del SMN 1991–2020 (Observatorio Central), interpoladas día a día.
/// - Adentro (supuesto): de noche +2 °C con calefacción a 9 °C de mínima; de día +5 °C con ventilación y tope de
///   ~32 °C con media sombra (en una ola de calor el tope sube).
/// - Curva del día: mínima a las 6, máxima a las 15. Humedad alta de noche y baja de día.
/// Fuentes en notas/simulacion/entorno-simulacion.md.
/// </summary>
public static class GreenhouseClimate
{
    private static readonly double[] OutdoorMaxByMonth = [30.1, 28.9, 26.9, 23.2, 19.4, 16.4, 15.5, 17.9, 19.7, 22.6, 26.0, 29.0];
    private static readonly double[] OutdoorMinByMonth = [20.2, 19.4, 17.7, 14.1, 11.1, 8.4, 7.5, 8.9, 10.6, 13.4, 16.1, 18.6];
    private static readonly double[] OutdoorRhByMonth = [64, 68, 72, 76, 77, 79, 79, 74, 70, 70, 65, 63];

    public const double NightOffset = 2.0;
    public const double DayOffset = 5.0;
    public const double HeatingMin = 9.0;
    public const double VentilatedMax = 32.0;
    public const double MinHour = 6.0;
    public const double MaxHour = 15.0;

    /// <summary>Normal de un día afuera: máxima, mínima y humedad media.</summary>
    public readonly record struct DayNormal(double OutdoorMax, double OutdoorMin, double OutdoorRh);

    /// <summary>Un día adentro: máxima, mínima y humedad media de base (con la anomalía del tiempo ya aplicada).</summary>
    public readonly record struct InsideDay(double Max, double Min, double BaseRh);

    /// <summary>Normal del día, interpolada entre la mitad de cada mes.</summary>
    public static DayNormal Normal(DateOnly date)
    {
        // Mitad de mes ≈ día 15; se interpola entre el mes del día y el vecino.
        var month = date.Month - 1;
        var daysInMonth = DateTime.DaysInMonth(date.Year, date.Month);
        var position = (date.Day - 0.5) / daysInMonth - 0.5; // −0,5..+0,5 alrededor de la mitad
        var other = position >= 0 ? (month + 1) % 12 : (month + 11) % 12;
        var weight = Math.Abs(position);

        double Blend(double[] values) => values[month] * (1 - weight) + values[other] * weight;
        return new DayNormal(Blend(OutdoorMaxByMonth), Blend(OutdoorMinByMonth), Blend(OutdoorRhByMonth));
    }

    /// <summary>Máxima y mínima adentro para un día afuera con esta anomalía (°C; + más calor que lo normal).</summary>
    public static InsideDay Inside(DayNormal normal, double anomaly, double rhAnomaly = 0)
    {
        var cap = VentilatedMax + Math.Max(0, anomaly) * 0.6;
        var max = Math.Min(cap, normal.OutdoorMax + anomaly + DayOffset);
        var min = Math.Max(HeatingMin, normal.OutdoorMin + anomaly * 0.8 + NightOffset);
        min = Math.Min(min, max - 3);
        // Adentro el aire es algo más seco de día que afuera (ventilación y temperatura), con riego cerca.
        var baseRh = Math.Clamp(normal.OutdoorRh - 6 - anomaly * 1.5 + rhAnomaly, 35, 90);
        return new InsideDay(max, min, baseRh);
    }

    /// <summary>
    /// Temperatura a una hora local (0–24): sube de la mínima (6 h) a la máxima (15 h) y baja hasta la mínima del día
    /// siguiente. <paramref name="previous"/> da la tarde anterior (antes de las 6 h).
    /// </summary>
    public static double TemperatureAt(InsideDay previous, InsideDay today, InsideDay next, double hour)
    {
        if (hour < MinHour)
            return Cooling(previous.Max, today.Min, hour + 24 - MaxHour);
        if (hour < MaxHour)
            return today.Min + (today.Max - today.Min) * (1 - Math.Cos(Math.PI * (hour - MinHour) / (MaxHour - MinHour))) / 2;
        return Cooling(today.Max, next.Min, hour - MaxHour);
    }

    /// <summary>De la máxima (15 h) a la mínima siguiente (6 h): 15 horas.</summary>
    private static double Cooling(double max, double nextMin, double hoursSinceMax) =>
        max - (max - nextMin) * (1 - Math.Cos(Math.PI * Math.Clamp(hoursSinceMax / (24 - MaxHour + MinHour), 0, 1))) / 2;

    /// <summary>Humedad relativa: con la temperatura mínima del día, base + 15 %; con la máxima, base − 10 %.</summary>
    public static double HumidityAt(InsideDay today, double temperature)
    {
        var span = Math.Max(1, today.Max - today.Min);
        var warmth = Math.Clamp((temperature - today.Min) / span, 0, 1);
        return Math.Clamp(today.BaseRh + 15 - 25 * warmth, 30, 97);
    }

    /// <summary>Temperatura y humedad de un instante con el tiempo normal (sin anomalía) más un corrimiento opcional.</summary>
    public static (double Temperature, double Humidity) NormalAt(DateTime local, double anomaly = 0)
    {
        var date = DateOnly.FromDateTime(local);
        var today = Inside(Normal(date), anomaly);
        var previous = Inside(Normal(date.AddDays(-1)), anomaly);
        var next = Inside(Normal(date.AddDays(1)), anomaly);
        var hour = local.TimeOfDay.TotalHours;
        var temperature = TemperatureAt(previous, today, next, hour);
        return (temperature, HumidityAt(today, temperature));
    }

    /// <summary>GDD de un día con la fórmula de la web (GddService): max(0, (min(Tmax, 30) + Tmin) / 2 − Tbase).</summary>
    public static double DailyGdd(double max, double min, double baseTemperature = 4.5) =>
        Math.Max(0, (Math.Min(max, 30) + min) / 2 - baseTemperature);
}
