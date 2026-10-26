using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.SimulationEnvironment.Model;

namespace HydroPilotWeb.Services.SimulationEnvironment;

/// <summary>Un día del invernadero: normal de afuera, anomalía del tiempo y el día adentro.</summary>
internal sealed record ClimateDay(
    DateOnly Date,
    GreenhouseClimate.DayNormal Normal,
    GreenhouseClimate.InsideDay Inside,
    double Anomaly,
    string? Event)
{
    public double Gdd => GreenhouseClimate.DailyGdd(Inside.Max, Inside.Min);
}

/// <summary>Lo que le pasa a un lote además del día a día (cada uno deja rastro en varias tablas).</summary>
internal enum Incident
{
    None,

    /// <summary>Ola de calor: solución tibia varios días → Pythium en raíces → el lote se descarta.</summary>
    Pythium,

    /// <summary>Calor con CE alta: tipburn en hojas jóvenes y floración prematura (bolting) en el convencional.</summary>
    HeatTipburn,

    /// <summary>La Pi queda sin conexión un fin de semana: hueco de lecturas, dosis vencidas, lecturas atrasadas.</summary>
    PiOffline,

    /// <summary>La sonda de pH se descalibra (mide de más): se dosifica ácido de más hasta recalibrarla.</summary>
    PhProbeDrift,

    /// <summary>La bomba de pH Down se tapa: 3 dosis sin efecto → el control de pH se pausa.</summary>
    PhDownClogged,

    /// <summary>El bidón de nutrientes se vacía: 3 dosis sin efecto → el control de CE se pausa.</summary>
    NutrientEmpty,
}

internal sealed record PlannedLot(int Bench, int Number, DateOnly Sowing, DateOnly PlannedEnd, Incident Incident = Incident.None);

/// <summary>
/// Clima del período y calendario de lotes por mesada (lógica pura). Los lotes de una mesada van uno detrás del otro:
/// se siembra, se corta Baby Leaf en la ventana y el resto sigue a convencional hasta su óptimo (650 GDD, el de Lotes);
/// después, 3 días de limpieza. Se planifica hacia atrás desde los lotes en curso para que terminen hoy en la etapa
/// buscada.
/// </summary>
internal static class HistoryPlanner
{
    public const int CleaningDays = 3;

    /// <summary>Clima día por día: anomalía del tiempo (autocorrelada), olas de calor en verano y frentes fríos en invierno.</summary>
    public static Dictionary<DateOnly, ClimateDay> Climate(DateOnly from, DateOnly to, Random rng)
    {
        // Eventos: 2 olas de calor por verano y 2 frentes fríos por invierno.
        var events = new Dictionary<DateOnly, (double Delta, string Name)>();
        for (var year = from.Year - 1; year <= to.Year; year++)
        {
            foreach (var (start, span) in new[] { (new DateOnly(year, 12, 20), 50), (new DateOnly(year, 12, 20), 50) })
                AddEvent(events, start.AddDays(rng.Next(span)), 4 + rng.Next(3), 6.5, "ola de calor", rng);
            foreach (var (start, span) in new[] { (new DateOnly(year, 6, 1), 75), (new DateOnly(year, 6, 1), 75) })
                AddEvent(events, start.AddDays(rng.Next(span)), 3 + rng.Next(2), -5.0, "frente frío", rng);
        }

        var days = new Dictionary<DateOnly, ClimateDay>();
        double anomaly = 0, rh = 0;
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            anomaly = 0.7 * anomaly + Gaussian(rng) * 1.8;
            rh = 0.6 * rh + Gaussian(rng) * 5;
            var rain = rng.NextDouble() < 0.08 ? 14 : 0;
            var (delta, name) = events.GetValueOrDefault(date);
            var total = anomaly + delta - (rain > 0 ? 2 : 0);
            var normal = GreenhouseClimate.Normal(date);
            days[date] = new ClimateDay(date, normal, GreenhouseClimate.Inside(normal, total, rh + rain), total,
                name ?? (rain > 0 ? "lluvia" : null));
        }
        return days;
    }

    private static void AddEvent(Dictionary<DateOnly, (double, string)> events, DateOnly start, int length, double delta, string name, Random rng)
    {
        for (var i = 0; i < length; i++)
        {
            // Rampa: el primer y el último día a medias.
            var factor = i == 0 || i == length - 1 ? 0.6 : 1.0;
            events[start.AddDays(i)] = (delta * factor + Gaussian(rng) * 0.5, name);
        }
    }

    /// <summary>Día en que un lote sembrado en <paramref name="sowing"/> llega al óptimo convencional (se corta ese día).</summary>
    public static DateOnly ConventionalCut(Dictionary<DateOnly, ClimateDay> climate, DateOnly sowing)
    {
        var target = (double)CycleTimelineLogic.ConventionalOptimumGdd;
        var sum = 0.0;
        for (var date = sowing; ; date = date.AddDays(1))
        {
            if (!climate.TryGetValue(date, out var day))
                return date;
            sum += day.Gdd;
            if (sum >= target)
                return date.AddDays(1);
        }
    }

    /// <summary>
    /// Lotes de una mesada hacia atrás desde el lote en curso (<paramref name="currentSowing"/>): cada uno es el más
    /// tardío que termina (y se limpia) antes del siguiente. Devuelve en orden cronológico, numerados desde 1.
    /// </summary>
    public static List<PlannedLot> Plan(Dictionary<DateOnly, ClimateDay> climate, int bench, DateOnly currentSowing, DateOnly historyStart)
    {
        var sowings = new List<(DateOnly Sowing, DateOnly End)>
        {
            (currentSowing, ConventionalCut(climate, currentSowing)),
        };
        var next = currentSowing;
        while (true)
        {
            var sowing = next.AddDays(-(CleaningDays + 1));
            while (ConventionalCut(climate, sowing).AddDays(CleaningDays) > next)
                sowing = sowing.AddDays(-1);
            if (sowing < historyStart)
                break;
            sowings.Add((sowing, ConventionalCut(climate, sowing)));
            next = sowing;
        }

        sowings.Reverse();
        return sowings.Select((s, i) => new PlannedLot(bench, i + 1, s.Sowing, s.End)).ToList();
    }

    /// <summary>
    /// Elige qué lote vive cada incidente: los de calor en el lote con la ola de calor más fuerte de cada verano, los
    /// de hardware en lotes de la época manual o automática según corresponda. Nunca en los lotes en curso.
    /// </summary>
    public static List<PlannedLot> AssignIncidents(
        List<PlannedLot> lots, Dictionary<DateOnly, ClimateDay> climate, DateOnly autoStart, DateOnly today)
    {
        var closed = lots.Where(l => l.PlannedEnd < today).ToList();
        var taken = new HashSet<PlannedLot>();

        PlannedLot? Pick(Func<PlannedLot, bool> where, Func<PlannedLot, double> score)
        {
            var lot = closed.Where(l => !taken.Contains(l) && where(l)).OrderByDescending(score).FirstOrDefault();
            if (lot is not null)
                taken.Add(lot);
            return lot;
        }

        double Heat(PlannedLot l)
        {
            var sum = 0.0;
            for (var d = l.Sowing.AddDays(5); d < l.PlannedEnd; d = d.AddDays(1))
                sum += Math.Max(0, climate.GetValueOrDefault(d)?.Anomaly ?? 0);
            return sum;
        }

        var firstSummer = lots.Min(l => l.Sowing).Year;
        var pythium = Pick(l => l.Bench == 0 && l.Sowing < autoStart && IsSummer(l.Sowing), Heat);
        var tipburn = Pick(l => l.Bench == 1 && IsSummer(l.Sowing) && l.Sowing.Year > firstSummer, Heat);
        var offline = Pick(l => l.Bench == 0 && l.Sowing < autoStart && l.Sowing.Month is >= 4 and <= 6, _ => 0);
        var probe = Pick(l => l.Bench == 1 && l.Sowing < autoStart && l.Sowing.Month is >= 7 and <= 9, _ => 0);
        var clogged = Pick(l => l.Bench == 0 && l.Sowing >= autoStart, l => -l.Sowing.DayNumber);
        var empty = Pick(l => l.Bench == 1 && l.Sowing >= autoStart, l => l.Sowing.DayNumber);

        var incidents = new Dictionary<PlannedLot, Incident>();
        void Set(PlannedLot? lot, Incident incident)
        {
            if (lot is not null)
                incidents[lot] = incident;
        }
        Set(pythium, Incident.Pythium);
        Set(tipburn, Incident.HeatTipburn);
        Set(offline, Incident.PiOffline);
        Set(probe, Incident.PhProbeDrift);
        Set(clogged, Incident.PhDownClogged);
        Set(empty, Incident.NutrientEmpty);

        return lots.Select(l => incidents.TryGetValue(l, out var i) ? l with { Incident = i } : l).ToList();
    }

    private static bool IsSummer(DateOnly sowing) => sowing.Month is 12 or 1 or 2;

    /// <summary>Normal estándar (Box-Muller).</summary>
    public static double Gaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
