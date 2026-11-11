using HydroPilotWeb.Models;
using HydroPilotWeb.Services.SimulationEnvironment.Model;

namespace HydroPilotWeb.Services.SimulationEnvironment.Dataset;

/// <summary>Un lote del guion: sus plantas, los circuitos de Adultas que le tocan al trasplantar y su mapa (si tiene).</summary>
public sealed record DemoLotScript(DemoLot Lot, int Plants, DemoCircuit Dense, IReadOnlyList<string> AdultCircuits, DemoMap? Map);

/// <summary>El dataset listo para el generador: controlado, con las fechas corridas si hacía falta.</summary>
public sealed record DemoScript(DemoDataset Dataset, IReadOnlyList<DemoLotScript> Lots)
{
    public DemoGeneral General => Dataset.General;
}

public sealed record DemoPlanResult(DemoScript? Script, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

/// <summary>
/// GDD estimado con la hoja Clima, igual que la hoja Calendario del Excel: normal del mes + olas de calor y frentes fríos,
/// adentro con el modelo del invernadero (<see cref="GreenhouseClimate"/>).
/// </summary>
public static class DemoClimateModel
{
    /// <summary>La normal del mes de la fecha del Excel (con las fechas corridas, la de <see cref="DemoClimate.ShiftDays"/> días antes).</summary>
    public static GreenhouseClimate.DayNormal Normal(DemoClimate climate, DateOnly date)
    {
        var month = date.AddDays(-climate.ShiftDays).Month;
        return GreenhouseClimate.MonthlyNormal(month, climate.OutdoorMax[month - 1], climate.OutdoorMin[month - 1]);
    }

    public static double Anomaly(DemoClimate climate, DateOnly date) =>
        climate.Events.Where(e => e.From <= date && date <= e.To).Sum(e => e.Delta);

    public static double DailyGdd(DemoClimate climate, DateOnly date)
    {
        var inside = GreenhouseClimate.Inside(Normal(climate, date), Anomaly(climate, date));
        return GreenhouseClimate.DailyGdd(inside.Max, inside.Min);
    }

    /// <summary>GDD de la siembra a la mañana de <paramref name="date"/>.</summary>
    public static decimal Gdd(DemoClimate climate, DateOnly sowing, DateOnly date)
    {
        var sum = 0.0;
        for (var d = sowing; d < date; d = d.AddDays(1))
            sum += DailyGdd(climate, d);
        return Math.Round((decimal)sum, 0);
    }
}

/// <summary>
/// Controla el dataset antes de generar (lo mismo que la columna Control de Lotes y de Mapas, más lo que el Excel solo
/// aproxima: qué circuito de Adultas recibe cada trasplante) y arma el guion. Lógica pura.
/// </summary>
public static class DemoDatasetPlanner
{
    public static DemoPlanResult Plan(DemoDataset dataset, DateOnly loadDay)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // Fechas: corridas para que "hoy" sea el día en que se carga (los lotes en curso llegan igual).
        var g = dataset.General;
        if (g.Today != loadDay)
        {
            if (g.ShiftDates)
            {
                var days = loadDay.DayNumber - g.Today.DayNumber;
                dataset = dataset.Shifted(days);
                warnings.Add($"Las fechas se corrieron {days} día(s): \"Hoy del dataset\" pasa del {g.Today:dd/MM/yyyy} al {loadDay:dd/MM/yyyy}.");
            }
            else if (g.Today > loadDay)
            {
                errors.Add($"General: \"Hoy del dataset\" ({g.Today:dd/MM/yyyy}) es después de hoy. Poné \"Correr las fechas\" en Sí.");
            }
            else
            {
                warnings.Add($"El historial termina el {g.Today:dd/MM/yyyy} (\"Correr las fechas\" en No): desde ahí no hay lecturas.");
            }
        }
        // El nombre lleva la fecha de siembra ya corrida ("Lote B26 · 08/10/2026"), así siempre coincide.
        dataset = dataset.WithSowingInNames();
        var general = dataset.General;

        // Circuitos
        var circuits = dataset.Circuits;
        if (!circuits.Any(c => c.Kind == CircuitKinds.Dense))
            errors.Add("Circuitos: tiene que haber al menos un circuito Denso.");
        foreach (var dup in circuits.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
            errors.Add($"Circuitos: el nombre \"{dup.Key}\" está repetido.");
        var byName = circuits.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var adultCircuits = circuits.Where(c => c.Kind == CircuitKinds.Adult).OrderBy(c => c.Row).ToList();

        foreach (var dup in dataset.Users.GroupBy(u => u.Email, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
            errors.Add($"Usuarios: el correo {dup.Key} está repetido.");
        foreach (var user in dataset.Users.Where(u => !u.Email.EndsWith(".test", StringComparison.OrdinalIgnoreCase)
                                                       && !u.Email.EndsWith(".local", StringComparison.OrdinalIgnoreCase)))
            warnings.Add($"Usuarios, fila {user.Row}: {user.Email} no es .test; en esta base el correo solo se loguea, pero conviene .test.");

        // Olas de calor de los incidentes de calor, si la hoja Clima no tiene una en el ciclo del lote.
        dataset = WithIncidentHeatWaves(dataset, warnings);
        var climate = dataset.Climate;

        var lots = dataset.Lots;
        foreach (var dup in lots.GroupBy(l => l.Name, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
            errors.Add($"Lotes: el nombre \"{dup.Key}\" está repetido.");
        if (lots.Count == 0)
            errors.Add("Lotes: no hay ningún lote.");

        var maps = dataset.Maps;
        foreach (var dup in maps.GroupBy(m => m.Lot, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
            errors.Add($"Mapas: el lote \"{dup.Key}\" tiene dos mapas.");
        var lotNames = lots.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var map in maps.Where(m => !lotNames.Contains(m.Lot)))
            errors.Add($"Mapas, fila {map.Row}: el lote \"{map.Lot}\" no está en Lotes.");

        var scripts = new List<DemoLotScript>();
        foreach (var lot in lots)
        {
            var where = $"Lotes, fila {lot.Row} ({lot.Name})";
            if (!byName.TryGetValue(lot.Circuit, out var dense))
            {
                errors.Add(lot.Circuit.Length == 0 ? $"{where}: falta el circuito denso." : $"{where}: el circuito \"{lot.Circuit}\" no está en Circuitos.");
                continue;
            }
            if (dense.Kind != CircuitKinds.Dense)
                errors.Add($"{where}: el circuito {dense.Name} no es Denso.");
            // Un lote nunca empieza con posiciones vacías: una planta por hueco del circuito.
            var plants = lot.Plants ?? dense.Holes;
            if (plants != dense.Holes)
                errors.Add($"{where}: {plants} plantas y el circuito tiene {dense.Holes} huecos (se siembra una por hueco).");
            CheckLot(lot, plants, general, climate, where, errors);
            var map = maps.FirstOrDefault(m => m.Lot.Equals(lot.Name, StringComparison.OrdinalIgnoreCase));
            if (map is not null)
                CheckMap(map, lot, plants, dense, general, climate, errors);
            foreach (var name in lot.AdultCircuits.Where(n => !byName.TryGetValue(n, out var c) || c.Kind != CircuitKinds.Adult))
                errors.Add($"{where}: \"{name}\" no es un circuito de Adultas de la hoja Circuitos.");
            scripts.Add(new DemoLotScript(lot, plants, dense, [], map));
        }

        CheckDenseTurns(scripts, general, errors);
        scripts = AssignAdultCircuits(scripts, adultCircuits, errors, warnings);

        if (dataset.Prices.Count > 0)
        {
            var first = dataset.Prices.Min(p => p.From);
            foreach (var lot in lots)
            {
                foreach (var cut in new[] { lot.Cut1, lot.Cut2, lot.AdultCut }.OfType<DemoEvent>().Where(c => c.Date < first))
                    errors.Add($"Lotes, fila {lot.Row} ({lot.Name}): el corte del {cut.Date:dd/MM/yyyy} no tiene precio (Precios empieza el {first:dd/MM/yyyy}).");
            }
        }

        var script = errors.Count == 0 ? new DemoScript(dataset, scripts) : null;
        return new DemoPlanResult(script, errors, warnings);
    }

    // ------------------------------------------------------------------

    private static void CheckLot(DemoLot lot, int plants, DemoGeneral general, DemoClimate climate, string where, List<string> errors)
    {
        var today = general.Today;
        var denseLeft = plants - lot.Transplanted - lot.CutAsBabyLeaf - lot.DenseDiscarded;
        var adultsLeft = lot.Transplanted - lot.AdultsCut - lot.AdultsDiscarded;
        if (denseLeft < 0)
            errors.Add($"{where}: salen del denso {plants - denseLeft} plantas y se sembraron {plants}.");
        if (adultsLeft < 0)
            errors.Add($"{where}: salen de Adultas {lot.Transplanted - adultsLeft} plantas y se trasplantaron {lot.Transplanted}.");

        var dates = new[] { lot.Transplant, lot.Cut1, lot.Cut2, lot.AdultCut, lot.DenseDiscard, lot.AdultDiscard }.OfType<DemoEvent>().ToList();
        if (lot.Sowing > today || dates.Any(e => e.Date > today))
            errors.Add($"{where}: hay fechas después de hoy ({today:dd/MM/yyyy}).");
        if (lot.Transplant is { } tr && tr.Date <= lot.Sowing
            || lot.Cut1 is { } c1 && c1.Date <= lot.Sowing
            || lot.Cut2 is { } c2 && (lot.Cut1 is null || c2.Date <= lot.Cut1.Date)
            || lot.AdultCut is { } ac && (lot.Transplant is null || ac.Date <= lot.Transplant.Date)
            || lot.DenseDiscard is { } dd && dd.Date < lot.Sowing
            || lot.AdultDiscard is { } da && (lot.Transplant is null || da.Date < lot.Transplant.Date))
            errors.Add($"{where}: fechas fuera de orden.");

        decimal Gdd(DateOnly date) => DemoClimateModel.Gdd(climate, lot.Sowing, date);
        if (lot.Transplant is { } t && Gdd(t.Date) is var gt && (gt < general.DecisionGdd || gt > general.BabyLeafGddMax))
            errors.Add($"{where}: trasplante a los {gt:0} GDD (va de {general.DecisionGdd:0} a {general.BabyLeafGddMax:0}).");
        foreach (var cut in new[] { lot.Cut1, lot.Cut2 }.OfType<DemoEvent>())
        {
            var gc = Gdd(cut.Date);
            if (gc < general.BabyLeafGddMin || gc > general.BabyLeafGddMax)
                errors.Add($"{where}: corte Baby Leaf del {cut.Date:dd/MM/yyyy} a los {gc:0} GDD (fuera de la ventana {general.BabyLeafGddMin:0}–{general.BabyLeafGddMax:0}).");
        }
        if (lot.AdultCut is { } a && Gdd(a.Date) is var ga && (ga < general.AdultCutGdd || ga > general.CycleEndGdd))
            errors.Add($"{where}: corte de adultas a los {ga:0} GDD (va de {general.AdultCutGdd:0} a {general.CycleEndGdd:0}).");

        var gddToday = Gdd(today);
        if (denseLeft > 0 && gddToday > general.BabyLeafGddMax)
            errors.Add($"{where}: quedan {denseLeft} plantas en el denso con la ventana Baby Leaf cerrada (hoy {gddToday:0} GDD).");
        if (adultsLeft > 0 && gddToday > general.CycleEndGdd)
            errors.Add($"{where}: quedan {adultsLeft} adultas después del fin del ciclo (hoy {gddToday:0} GDD).");
    }

    private static void CheckMap(DemoMap map, DemoLot lot, int plants, DemoCircuit dense, DemoGeneral general, DemoClimate climate, List<string> errors)
    {
        var where = $"Mapas, fila {map.Row} ({map.Lot})";
        if (map.Codes.Keys.Any(k => k.Row > dense.Rows || k.Column > dense.Columns))
            errors.Add($"{where}: hay plantas fuera de la grilla de {dense.Name} ({dense.Rows} × {dense.Columns}).");
        void Same(string label, int inMap, int inLots)
        {
            if (inMap != inLots)
                errors.Add($"{where}: {label}, {inMap} en el mapa y {inLots} en Lotes.");
        }
        Same("total de plantas", map.Codes.Count, plants);
        Same("activas en el denso", map.Count(DemoMapCodes.ActiveDense), plants - lot.Transplanted - lot.CutAsBabyLeaf - lot.DenseDiscarded);
        Same("adultas activas", map.Count(DemoMapCodes.Adult), lot.Transplanted - lot.AdultsCut - lot.AdultsDiscarded);
        Same("cosechadas como Baby Leaf", map.Count(DemoMapCodes.CutBabyLeaf), lot.CutAsBabyLeaf);
        Same("cosechadas como Adulta", map.Count(DemoMapCodes.CutAdult), lot.AdultsCut);
        Same("descartadas en el denso", map.Count(DemoMapCodes.DiscardedDense), lot.DenseDiscarded);
        Same("descartadas en Adultas", map.Count(DemoMapCodes.DiscardedAdult), lot.AdultsDiscarded);
        var gdd = DemoClimateModel.Gdd(climate, lot.Sowing, general.Today);
        if (map.Count(DemoMapCodes.Candidate) + map.Count(DemoMapCodes.BabyLeaf) > 0
            && (gdd < general.BabyLeafGddMin || gdd >= general.BabyLeafGddMax))
            errors.Add($"{where}: Candidatas o Baby Leaf con el lote a {gdd:0} GDD (fuera de la ventana).");
    }

    /// <summary>Un lote por circuito denso: el siguiente se siembra cuando salió la última planta, más los días de limpieza.</summary>
    private static void CheckDenseTurns(List<DemoLotScript> scripts, DemoGeneral general, List<string> errors)
    {
        foreach (var circuit in scripts.GroupBy(s => s.Dense.Name, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = circuit.OrderBy(s => s.Lot.Sowing).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var previous = ordered[i - 1];
                var next = ordered[i];
                var where = $"Lotes, fila {next.Lot.Row} ({next.Lot.Name})";
                if (previous.Lot.LeavesDense(previous.Plants) is not { } leaves)
                    errors.Add($"{where}: {circuit.Key} sigue con plantas de {previous.Lot.Name}.");
                else if (next.Lot.Sowing < leaves.AddDays(general.CleaningDays + 1))
                    errors.Add($"{where}: se siembra el {next.Lot.Sowing:dd/MM/yyyy} y {circuit.Key} queda libre el " +
                               $"{leaves.AddDays(general.CleaningDays + 1):dd/MM/yyyy} (sale {previous.Lot.Name} + {general.CleaningDays} días de limpieza).");
            }
        }
    }

    /// <summary>
    /// Qué circuitos de Adultas recibe cada trasplante, en orden de fecha: primero los que pide el lote (si están libres),
    /// después los demás en el orden de la hoja Circuitos. Un circuito tiene un solo lote a la vez y queda libre el día en
    /// que salen sus adultas.
    /// </summary>
    private static List<DemoLotScript> AssignAdultCircuits(List<DemoLotScript> scripts, List<DemoCircuit> adults, List<string> errors, List<string> warnings)
    {
        var busyUntil = new Dictionary<string, (DemoLot Lot, DateOnly? Until)>(StringComparer.OrdinalIgnoreCase);
        var result = scripts.ToDictionary(s => s, s => s);
        foreach (var script in scripts.Where(s => s.Lot.Transplant is not null).OrderBy(s => s.Lot.Transplant!.Date).ThenBy(s => s.Lot.Sowing))
        {
            var lot = script.Lot;
            var date = lot.Transplant!.Date;
            bool Free(DemoCircuit c) => !busyUntil.TryGetValue(c.Name, out var busy) || busy.Until is { } until && until <= date;
            var preferred = lot.AdultCircuits
                .Select(n => adults.FirstOrDefault(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                .OfType<DemoCircuit>()
                .ToList();
            foreach (var busy in preferred.Where(c => !Free(c)))
                warnings.Add($"Lotes, fila {lot.Row} ({lot.Name}): {busy.Name} está con {busyUntil[busy.Name].Lot.Name} el día del trasplante; uso otro.");
            var chosen = new List<string>();
            var room = 0;
            foreach (var circuit in preferred.Concat(adults.Except(preferred)).Where(Free))
            {
                if (room >= lot.Transplanted)
                    break;
                chosen.Add(circuit.Name);
                room += circuit.Holes;
            }
            if (room < lot.Transplanted)
            {
                errors.Add($"Lotes, fila {lot.Row} ({lot.Name}): el {date:dd/MM/yyyy} hay {room} huecos en circuitos de Adultas libres " +
                           $"y se trasplantan {lot.Transplanted} (un circuito de Adultas tiene un solo lote a la vez).");
                continue;
            }
            foreach (var name in chosen)
                busyUntil[name] = (lot, lot.LeavesAdults());
            result[script] = script with { AdultCircuits = chosen };
        }
        return scripts.Select(s => result[s]).ToList();
    }

    /// <summary>
    /// "Ola de calor y Pythium" y "Calor con CE alta" necesitan calor: si la hoja Clima no tiene una ola de calor durante el
    /// lote, se agrega una (+6,5 °C, 5 días) antes del descarte o del corte.
    /// </summary>
    private static DemoDataset WithIncidentHeatWaves(DemoDataset dataset, List<string> warnings)
    {
        var events = dataset.Climate.Events.ToList();
        foreach (var lot in dataset.Lots)
        {
            if (lot.Incident is null || !DemoDatasetReader.Incidents.TryGetValue(lot.Incident, out var incident)
                || incident is not (Incident.Pythium or Incident.HeatTipburn))
                continue;
            var end = new[] { lot.DenseDiscard?.Date, lot.Cut1?.Date, lot.Transplant?.Date }.OfType<DateOnly>().DefaultIfEmpty(lot.Sowing.AddDays(20)).Max();
            if (events.Any(e => e.Delta > 0 && e.From <= end && e.To >= lot.Sowing))
                continue;
            var to = end.AddDays(incident == Incident.Pythium ? -3 : -2);
            var from = to.AddDays(-4);
            if (from <= lot.Sowing)
                from = lot.Sowing.AddDays(1);
            if (to < from)
                to = from;
            events.Add(new DemoClimateEvent(from, to, "Ola de calor", 6.5));
            warnings.Add($"Clima: agregué una ola de calor del {from:dd/MM/yyyy} al {to:dd/MM/yyyy} para el incidente de {lot.Name}.");
        }
        return events.Count == dataset.Climate.Events.Count ? dataset : dataset with { Climate = dataset.Climate with { Events = events } };
    }
}
