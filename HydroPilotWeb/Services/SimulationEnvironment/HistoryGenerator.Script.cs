using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.SimulationEnvironment.Dataset;

namespace HydroPilotWeb.Services.SimulationEnvironment;

/// <summary>
/// El historial con un dataset (el Excel de la demo): cada planta nace con su destino (el del mapa o, sin mapa, uno por
/// vigor según las cantidades de Lotes) y su crecimiento se guía para llegar a él. La que se corta como Baby Leaf llega
/// al tamaño ideal el día del corte (la evaluación la da Baby Leaf); la que se trasplanta queda entre las de score más
/// bajo el día del trasplante; la que se descarta por tamaño se pasa justo ese día. Los cortes, los descartes y el
/// trasplante pasan en las fechas del Excel, con las plantas de ese destino.
/// </summary>
public sealed partial class HistoryGenerator
{
    private const string DefaultDenseReason = "Fuera de ventana al cierre del ciclo";
    private const string DefaultAdultReason = "Floración prematura (bolting) por calor";

    /// <summary>Los lotes del dataset en su mesada densa, en orden de siembra.</summary>
    private List<PlannedLot> ScriptedPlans(DemoScript script)
    {
        var plans = new List<PlannedLot>();
        for (var i = 0; i < _w.Benches.Count; i++)
        {
            var bench = _w.Benches[i];
            if (bench.IsAdult)
                continue;
            var number = 0;
            foreach (var lot in script.Lots.Where(l => l.Dense.Name.Equals(bench.CircuitName, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(l => l.Lot.Sowing))
            {
                var incident = lot.Lot.Incident is { } name && DemoDatasetReader.Incidents.TryGetValue(name, out var found) ? found : Incident.None;
                plans.Add(new PlannedLot(i, ++number, lot.Lot.Sowing, lot.Lot.LeavesDense(lot.Plants) ?? _today, incident, lot));
            }
        }
        return plans;
    }

    /// <summary>GDD que da el clima de <paramref name="from"/> a la mañana de <paramref name="to"/>.</summary>
    private double ExpectedGdd(DateOnly from, DateOnly to)
    {
        var sum = 0.0;
        for (var d = from; d < to; d = d.AddDays(1))
            sum += _climate.TryGetValue(d, out var day) ? day.Gdd : 0;
        return sum;
    }

    // ------------------------------------------------------------------
    // Siembra: hueco y destino de cada planta
    // ------------------------------------------------------------------

    private void AddScriptedPlants(LotRun run, BenchRun bench, DateTime t)
    {
        var script = run.Plan.Script!;
        var lot = script.Lot;
        double Vigor() => Math.Clamp(1 + HistoryPlanner.Gaussian(_rng) * 0.25, 0.4, 1.45);

        // Huecos: los del mapa o, sin mapa, los del circuito (si se sembraron menos, quedan huecos vacíos al azar).
        List<(int Row, int Column)> holes;
        if (script.Map is { } map)
        {
            holes = map.Codes.Keys.OrderBy(k => k.Row).ThenBy(k => k.Column).ToList();
        }
        else
        {
            holes = [];
            for (var row = 1; row <= bench.Bench.GridRows; row++)
                for (var column = 1; column <= bench.Bench.GridColumns; column++)
                    holes.Add((row, column));
            holes = holes.OrderBy(_ => _rng.Next()).Take(script.Plants).OrderBy(h => h.Row).ThenBy(h => h.Column).ToList();
        }
        var vigor = holes.ToDictionary(h => h, _ => Vigor());
        var fates = script.Map is { } m ? FatesFromMap(m, lot, vigor) : FatesByVigor(lot, script.Plants, vigor);

        var denseReason = lot.DenseReason ?? DefaultReason(run.Plan.Incident, lot, adult: false);
        var adultReason = lot.AdultReason ?? DefaultReason(run.Plan.Incident, lot, adult: true);
        var gddToday = ExpectedGdd(lot.Sowing, _today);
        var developing = _w.CommercialStageIds[CommercialStageNames.EnDesarrollo];
        foreach (var hole in holes)
        {
            var plant = new Plant
            {
                Id = ++_plantId,
                LotId = run.Lot.Id,
                CircuitId = bench.Bench.CircuitId,
                Row = hole.Row,
                Column = hole.Column,
                PhenologicalStageId = run.Stage.Id,
                CommercialStageId = developing,
                OperationalState = PlantOperationalState.Activa,
                CreatedAtUtc = t,
            };
            _data.Plants.Add(plant);
            _data.StageHistories.Add(new PlantStageHistory
            {
                Id = ++_historyId,
                PlantId = plant.Id,
                NewPhenologicalStageId = run.Stage.Id,
                NewCommercialStageId = developing,
                NewOperationalState = PlantOperationalState.Activa.ToString(),
                Source = "seed",
                Reason = "Siembra",
                ChangedAtUtc = t,
            });

            var fate = fates[hole];
            var runPlant = new PlantRun(plant, vigor[hole]) { Bench = bench, Fate = fate };
            if (fate == PlantFate.DenseDiscard)
                (runPlant.FateReason, runPlant.FateDate) = (denseReason, lot.DenseDiscard!.Date);
            else if (fate == PlantFate.TransplantDiscard)
                (runPlant.FateReason, runPlant.FateDate) = (adultReason, lot.AdultDiscard!.Date);
            SetTarget(runPlant, lot, gddToday);
            run.Plants.Add(runPlant);
        }
    }

    /// <summary>Destino de cada hueco según su código; las cortadas como Baby Leaf de más vigor van al primer corte.</summary>
    private static Dictionary<(int, int), PlantFate> FatesFromMap(DemoMap map, DemoLot lot, Dictionary<(int Row, int Column), double> vigor)
    {
        var fates = new Dictionary<(int, int), PlantFate>();
        foreach (var (hole, code) in map.Codes)
        {
            fates[hole] = code switch
            {
                DemoMapCodes.CutAdult => PlantFate.TransplantCut,
                DemoMapCodes.DiscardedAdult => PlantFate.TransplantDiscard,
                DemoMapCodes.Adult => PlantFate.TransplantActive,
                DemoMapCodes.DiscardedDense => PlantFate.DenseDiscard,
                DemoMapCodes.Candidate => PlantFate.ActiveCandidate,
                DemoMapCodes.BabyLeaf => PlantFate.ActiveBabyLeaf,
                DemoMapCodes.CutBabyLeaf => PlantFate.CutBabyLeaf2,
                _ => PlantFate.ActiveDeveloping,
            };
        }
        var firstCut = lot.Cut1?.Plants ?? 0;
        foreach (var hole in map.Codes.Where(c => c.Value == DemoMapCodes.CutBabyLeaf).Select(c => c.Key)
                     .OrderByDescending(h => vigor[h]).Take(firstCut))
            fates[hole] = PlantFate.CutBabyLeaf1;
        return fates;
    }

    /// <summary>
    /// Sin mapa: de más a menos vigor, las que se descartan por tamaño (si es el motivo), las del 1er y 2do corte, las que
    /// siguen en el denso, el resto de las descartadas y, las de menos vigor, las que se trasplantan.
    /// </summary>
    private static Dictionary<(int, int), PlantFate> FatesByVigor(DemoLot lot, int plants, Dictionary<(int Row, int Column), double> vigor)
    {
        var oversize = lot.DenseReason?.Contains("tamaño", StringComparison.OrdinalIgnoreCase) == true;
        var order = new List<PlantFate>();
        void Add(PlantFate fate, int count) => order.AddRange(Enumerable.Repeat(fate, Math.Max(0, count)));
        if (oversize)
            Add(PlantFate.DenseDiscard, lot.DenseDiscarded);
        Add(PlantFate.CutBabyLeaf1, lot.Cut1?.Plants ?? 0);
        Add(PlantFate.CutBabyLeaf2, lot.Cut2?.Plants ?? 0);
        Add(PlantFate.None, plants - lot.Transplanted - lot.CutAsBabyLeaf - lot.DenseDiscarded);
        if (!oversize)
            Add(PlantFate.DenseDiscard, lot.DenseDiscarded);
        Add(PlantFate.TransplantCut, lot.AdultsCut);
        Add(PlantFate.TransplantDiscard, lot.AdultsDiscarded);
        Add(PlantFate.TransplantActive, lot.Transplanted - lot.AdultsCut - lot.AdultsDiscarded);

        var fates = new Dictionary<(int, int), PlantFate>();
        var i = 0;
        foreach (var hole in vigor.OrderByDescending(v => v.Value).Select(v => v.Key))
            fates[hole] = i < order.Count ? order[i++] : PlantFate.None;
        return fates;
    }

    /// <summary>Motivo de descarte cuando el Excel no lo dice: el del incidente, damping-off si es temprano o fuera de ventana.</summary>
    private string DefaultReason(Incident incident, DemoLot lot, bool adult)
    {
        if (incident == Incident.Pythium)
            return "Pudrición de raíz (Pythium)";
        if (adult)
            return DefaultAdultReason;
        if (incident == Incident.HeatTipburn)
            return "Tipburn severo: hojas quemadas";
        return lot.DenseDiscard is { } discard && ExpectedGdd(lot.Sowing, discard.Date) < 150 ? "Damping-off en plántula" : DefaultDenseReason;
    }

    /// <summary>A qué tamaño (× el ideal de Baby Leaf) tiene que llegar y cuándo, según su destino.</summary>
    private void SetTarget(PlantRun plant, DemoLot lot, double gddToday)
    {
        double Between(double low, double high) => low + _rng.NextDouble() * (high - low);
        double Natural() => plant.Vigor * gddToday * 0.95 / 300;
        var reason = plant.FateReason ?? string.Empty;
        (DateOnly Date, double Ratio)? target = plant.Fate switch
        {
            PlantFate.CutBabyLeaf1 => (lot.Cut1!.Date, Between(0.95, 1.12)),
            PlantFate.CutBabyLeaf2 => (lot.Cut2!.Date, Between(0.95, 1.12)),
            PlantFate.TransplantActive or PlantFate.TransplantCut or PlantFate.TransplantDiscard => (lot.Transplant!.Date, Between(0.25, 0.36)),
            PlantFate.DenseDiscard when reason.Contains("tamaño", StringComparison.OrdinalIgnoreCase) => (plant.FateDate!.Value, Between(1.33, 1.45)),
            PlantFate.DenseDiscard when reason.Contains("Damping", StringComparison.OrdinalIgnoreCase) => (plant.FateDate!.Value, Between(0.06, 0.18)),
            PlantFate.DenseDiscard when reason.Contains("Pythium", StringComparison.OrdinalIgnoreCase)
                                     || reason.Contains("Tipburn", StringComparison.OrdinalIgnoreCase)
                                     || reason.Contains("bolting", StringComparison.OrdinalIgnoreCase) => (plant.FateDate!.Value, Between(0.5, 0.8)),
            PlantFate.DenseDiscard => (plant.FateDate!.Value, Between(0.45, 0.6)),
            PlantFate.ActiveDeveloping => (_today, (decimal)gddToday >= _w.Config.GddMin ? Between(0.25, 0.38) : Math.Clamp(Natural(), 0.05, 0.9)),
            PlantFate.ActiveCandidate => (_today, Between(0.55, 0.7)),
            PlantFate.ActiveBabyLeaf => (_today, Between(0.95, 1.1)),
            PlantFate.None => (_today, Math.Clamp(Natural(), 0.05, 1.1)),
            _ => null,
        };
        if (target is { } t)
            (plant.TargetDate, plant.TargetSize) = (t.Date, t.Ratio * 300);
    }

    // ------------------------------------------------------------------
    // Crecimiento y daños guiados
    // ------------------------------------------------------------------

    /// <summary>
    /// Eficiencia del día para llegar al tamaño de su destino en su fecha: lo que falta repartido en el GDD que queda (el
    /// de ayer, que crece hoy, más el que da el clima hasta esa mañana). Se corrige todos los días con lo que creció.
    /// </summary>
    private double GuidedEfficiency(PlantRun plant, DateOnly date, double grown, double natural)
    {
        if (plant.TargetSize is not { } target || plant.TargetDate is not { } until || date > until)
            return natural;
        var remaining = Math.Max(0, grown) + ExpectedGdd(date, until);
        return remaining < 0.5 ? natural : Math.Clamp((target - plant.Size) / remaining, 0.02, 1.6);
    }

    private static bool FatedTo(PlantRun plant, string reason) =>
        plant.FateReason?.Contains(reason, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Las que se descartan por tipburn se queman los últimos días; las de Pythium se enferman 5 días antes.</summary>
    private static void ScriptedDamage(PlantRun plant, DateOnly date)
    {
        if (plant.FateDate is not { } when || plant.Fate is not (PlantFate.DenseDiscard or PlantFate.TransplantDiscard))
            return;
        var daysLeft = when.DayNumber - date.DayNumber;
        if (FatedTo(plant, "Tipburn") && daysLeft is >= 0 and <= 5)
            plant.Damage = Math.Min(80, plant.Damage + 9);
        if (FatedTo(plant, "Pythium") && daysLeft <= 5 && plant.InfectedDays is null)
            plant.InfectedDays = 0;
    }

    // ------------------------------------------------------------------
    // Movimientos del Excel
    // ------------------------------------------------------------------

    /// <summary>Cortes y descartes del día (10 h), con las plantas de ese destino.</summary>
    private void ScriptedHarvest(BenchRun bench, DateTime t, DateTime local)
    {
        var run = bench.Lot!;
        var date = DateOnly.FromDateTime(local);
        run.HarvestCheckedOn = date;
        var lot = run.Plan.Script!.Lot;
        if (run.Plants.All(p => !p.Active))
        {
            CloseLot(run, t.AddMinutes(1));
            return;
        }

        void Cut(DemoEvent? scripted, PlantFate fate, bool babyLeaf, string name, int minutes)
        {
            if (scripted?.Date != date)
                return;
            var plants = run.Plants.Where(p => p.Active && p.Fate == fate).ToList();
            var notReady = babyLeaf ? plants.Count(p => !p.EvaluatedApt) : 0;
            foreach (var plant in plants)
                HarvestPlant(run, plant, date, t.AddMinutes(minutes), babyLeaf);
            Record(run, name, date, plants.Count, notReady);
            if (!babyLeaf)
            {
                run.Events.Add((t, "conv-cut", string.Empty));
                return;
            }
            run.LastBabyLeafCut = date;
            run.Events.Add((t, "bl-cut", $"{plants.Count}"));
            if (run.HarvestAlert is { IsResolved: false } alert)
            {
                alert.IsResolved = true;
                alert.ResolvedAtUtc = t.AddMinutes(30);
            }
        }

        void Drop(DemoEvent? scripted, PlantFate fate, string name, int minutes)
        {
            if (scripted?.Date != date)
                return;
            var plants = run.Plants.Where(p => p.Active && p.Fate == fate).ToList();
            foreach (var plant in plants)
                Discard(run, plant, date, t.AddMinutes(minutes), plant.FateReason ?? DefaultDenseReason);
            Record(run, name, date, plants.Count);
        }

        Cut(lot.Cut1, PlantFate.CutBabyLeaf1, babyLeaf: true, "1er corte Baby Leaf", 0);
        Cut(lot.Cut2, PlantFate.CutBabyLeaf2, babyLeaf: true, "2do corte Baby Leaf", 0);
        Drop(lot.DenseDiscard, PlantFate.DenseDiscard, "Descarte en el denso", 20);
        Cut(lot.AdultCut, PlantFate.TransplantCut, babyLeaf: false, "Corte de adultas", 30);
        Drop(lot.AdultDiscard, PlantFate.TransplantDiscard, "Descarte en Adultas", 40);
        ReleaseEmptyBenches(run, t);
    }

    /// <summary>El trasplante del Excel (11 h): las de ese destino, a los circuitos de Adultas que le tocaron.</summary>
    private void ScriptedTransplant(LotRun run, DateOnly date, DateTime t)
    {
        var script = run.Plan.Script!;
        if (run.TransplantDone || script.Lot.Transplant?.Date != date)
            return;
        run.TransplantDone = true;
        var movers = run.Plants.Where(p => p.Active && !p.AdultRoute && p.Transplants)
            .OrderBy(p => p.Plant.Row).ThenBy(p => p.Plant.Column).ToList();
        var benches = new List<BenchRun>();
        foreach (var name in script.AdultCircuits)
        {
            var adult = _benches.FirstOrDefault(b => b.IsAdult && b.Bench.CircuitName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (adult is { Lot: null })
                benches.Add(adult);
            else
                _data.Story.Add($"{run.Lot.Name}: {name} no estaba libre el día del trasplante");
        }
        var queue = new Queue<PlantRun>(movers);
        MoveToAdults(run, benches, queue, date, t);
        if (queue.Count > 0)
            _data.Story.Add($"{run.Lot.Name}: {queue.Count} plantas no entraron en Adultas");

        run.StressUntil = date.AddDays(_w.TransplantOrDefault.StressDays);
        run.Lot.TransplantCostPerPlant = _w.TransplantOrDefault.CostPerPlant;
        run.Events.Add((t, "transplant", $"{movers.Count - queue.Count} a Adultas"));
        Record(run, "Trasplante", date, movers.Count - queue.Count);
        ReleaseEmptyBenches(run, t);
    }

    /// <summary>El denso y cada circuito de Adultas quedan libres con su última planta; sin plantas activas, el lote se cierra.</summary>
    private void ReleaseEmptyBenches(LotRun run, DateTime t)
    {
        if (run.DenseBench is { } dense && dense.Lot == run && !run.Plants.Any(p => p.Active && p.Bench == dense))
            ReleaseBench(dense, run, t.AddMinutes(45));
        foreach (var hosting in _benches.Where(b => b.IsAdult && b.Lot == run).ToList())
        {
            if (!run.Plants.Any(p => p.Active && p.Bench == hosting))
                ReleaseBench(hosting, run, t.AddMinutes(50));
        }
        if (run.Plants.All(p => !p.Active))
            CloseLot(run, t.AddHours(1));
    }

    private void Record(LotRun run, string name, DateOnly date, int plants, int notReady = 0) =>
        _data.ScriptedEvents.Add(new ScriptedEventRecord(run.Lot.Id, run.Lot.Name ?? string.Empty, name, date, Math.Round(run.Gdd, 0), plants, notReady));
}
