using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.SimulationEnvironment.Dataset;

namespace HydroPilotWeb.Services.SimulationEnvironment;

/// <summary>
/// El historial con un dataset (el Excel de la demo): cada planta nace con su destino (el del mapa o, sin mapa, uno por
/// vigor según las cantidades de Lotes) y su crecimiento se guía para llegar a él. Las que se cortan como Baby Leaf se
/// vuelven aptas de a poco, la última justo el día del corte: la curva de aptas sube hasta ahí y la cosecha óptima del
/// lote cae en el corte. La que se trasplanta queda entre las de score más bajo el día del trasplante; la que se descarta
/// por tamaño se pasa justo ese día. Los cortes, los descartes y el trasplante pasan en las fechas del Excel, con las
/// plantas de ese destino.
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
        var developing = _w.CommercialStageIds[CommercialStageNames.Activa];
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

        // Cuándo se vuelve apta cada Baby Leaf: las del 1er corte desde que abre la ventana; las del 2do, después del 1er
        // corte; las que hoy son Baby Leaf en el mapa, hasta hoy.
        var windowOpens = DayAtGdd(lot.Sowing, (double)_w.Config.GddMin);
        List<PlantRun> Of(PlantFate fate) => run.Plants.Where(p => p.Fate == fate).OrderByDescending(p => p.Vigor).ToList();
        if (lot.Cut1 is { } cut1)
            ScheduleBabyLeaf(Of(PlantFate.CutBabyLeaf1), lot.Sowing, windowOpens, cut1.Date);
        if (lot.Cut2 is { } cut2)
            ScheduleBabyLeaf(Of(PlantFate.CutBabyLeaf2), lot.Sowing, Max(windowOpens, (lot.Cut1?.Date ?? windowOpens).AddDays(1)), cut2.Date);
        // Un lote en curso que todavía no cortó y hoy tiene la mitad o más del denso como Baby Leaf está en su día de corte
        // (si no, ya se habría cortado): sus Baby Leaf se vuelven aptas hasta ayer y hoy casi no crecen, así el score
        // general sube hasta ayer y hoy deja de mejorar (la app marca "Cortar ahora" sin perder plantas). Si no, se
        // vuelven aptas hasta hoy (el score general sigue subiendo).
        var babyLeafToday = Of(PlantFate.ActiveBabyLeaf);
        var denseToday = run.Plants.Count(p => p.Fate is PlantFate.ActiveBabyLeaf or PlantFate.ActiveCandidate
                                                   or PlantFate.ActiveDeveloping or PlantFate.None);
        var cutToday = lot.CutAsBabyLeaf == 0 && babyLeafToday.Count > 0 && _today > windowOpens.AddDays(1)
                       && babyLeafToday.Count >= denseToday * (double)BabyLeafOptimumRules.MinCutShare;
        ScheduleBabyLeaf(babyLeafToday, lot.Sowing, windowOpens, cutToday ? _today.AddDays(-1) : _today);
        if (cutToday)
        {
            // Hoy casi no crecen (el score general deja de mejorar), pero siguen aptas con margen: la evaluación de hoy es
            // la que muestra la app (las que llegaron ayer, justo en el umbral, si no bajarían de Baby Leaf).
            var keep = RatioForScore((double)_w.Config.ScoreMinReady + 5, ExpectedGdd(lot.Sowing, _today), quality: 0.75) * 300;
            foreach (var plant in babyLeafToday)
                plant.Milestones.Add((_today, Math.Max(plant.Milestones[^1].Size * 1.005, keep)));
        }

        // Las trasplantadas pasan de Activa a Adulta de a poco desde que empieza la etapa adulta (cierra la ventana Baby
        // Leaf): las del corte de adultas, hasta ese día; las que siguen en Adultas hoy, hasta hoy.
        var adultOpens = DayAtGdd(lot.Sowing, (double)_w.Config.GddMax);
        if (lot.AdultCut is { } adultCut)
            ScheduleAdults(Of(PlantFate.TransplantCut), adultOpens, adultCut.Date);
        if (_today >= adultOpens)
            ScheduleAdults(Of(PlantFate.TransplantActive), adultOpens, _today);
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    /// <summary>El primer día cuya mañana llega a <paramref name="gdd"/> desde la siembra, con el clima del historial.</summary>
    private DateOnly DayAtGdd(DateOnly sowing, double gdd)
    {
        var day = sowing.AddDays(1);
        for (var i = 0; i < 400 && ExpectedGdd(sowing, day) < gdd; i++)
            day = day.AddDays(1);
        return day;
    }

    /// <summary>
    /// Las Baby Leaf (de más vigor primero) se vuelven aptas entre <paramref name="start"/> y <paramref name="end"/>, más
    /// cerca del final, y un 15 % justo en <paramref name="end"/>: la curva de aptas sube hasta ese día, que es el pico.
    /// Cada una queda un poco debajo del umbral de Baby Leaf el día antes de volverse apta y después sigue creciendo,
    /// más cuanto antes llegó (las primeras terminan en el tamaño ideal; las del final, apenas pasado el umbral).
    /// </summary>
    private void ScheduleBabyLeaf(List<PlantRun> plants, DateOnly sowing, DateOnly start, DateOnly end)
    {
        if (plants.Count == 0)
            return;
        if (start > end)
            start = end;
        var span = end.DayNumber - start.DayNumber;
        var ready = (double)_w.Config.ScoreMinReady;
        var onEnd = (int)Math.Ceiling(plants.Count * 0.15);
        var days = Enumerable.Range(0, plants.Count)
            .Select(i => i < onEnd ? end : end.AddDays(-(int)Math.Round(span * Math.Pow(_rng.NextDouble(), 2))))
            .OrderBy(d => d)
            .ToList();
        // Calidad que se supone para las metas: la de un ciclo flojo (calor, CE alejada), así la evaluación da lo pedido.
        const double quality = 0.8;
        var gddEnd = ExpectedGdd(sowing, end);
        for (var i = 0; i < plants.Count; i++)
        {
            var plant = plants[i];
            var apt = days[i];
            var full = 0.95 + _rng.NextDouble() * 0.17;
            var share = span == 0 ? 0 : (end.DayNumber - apt.DayNumber) / (double)span;
            plant.Milestones.Clear();
            // Las que se vuelven aptas al abrir la ventana lo hacen por el salto del criterio de GDD: no necesitan el paso.
            // Las demás: debajo del umbral dos días antes y arriba ese día (una planta no sube tanto en un día; sin esa meta
            // dependen de lo que crezcan después y llegan tarde).
            if (apt > start)
            {
                var before = Max(start, apt.AddDays(-2));
                if (before < apt)
                    plant.Milestones.Add((before, RatioForScore(ready - 3, ExpectedGdd(sowing, before), quality) * 300));
                if (apt < end)
                    plant.Milestones.Add((apt, RatioForScore(ready + 3, ExpectedGdd(sowing, apt), quality) * 300));
            }
            // Al final, con margen para el ruido de la evaluación: más cuanto antes llegó (2,5 puntos por día, hasta 12; la
            // que llega ese día, 3,5 arriba). Desde 2026-11-10 el crecimiento es el acumulado y una planta chica pierde
            // por los dos lados (forma y crecimiento), así que las que llegaron antes terminan cerca del tamaño ideal.
            var margin = Math.Min(12, 3.5 + 2.5 * (end.DayNumber - apt.DayNumber));
            var atEnd = RatioForScore(ready + margin, gddEnd, quality);
            plant.Milestones.Add((end, Math.Max(atEnd, atEnd + (full - atEnd) * share) * 300));
            plant.BabyLeafFrom = apt;
        }
    }

    /// <summary>
    /// Las trasplantadas (de más vigor primero) llegan al tamaño de cosecha adulta entre <paramref name="start"/> (empieza
    /// la etapa adulta) y <paramref name="end"/>, más cerca del final y un 15 % justo ese día: hasta ahí son "Activa"
    /// (posibles adultas) y desde ahí "Adulta" (aptas para cortar), como la Baby Leaf en su ventana.
    /// </summary>
    private void ScheduleAdults(List<PlantRun> plants, DateOnly start, DateOnly end)
    {
        if (plants.Count == 0 || end < start)
            return;
        var span = end.DayNumber - start.DayNumber;
        var ready = (double)_w.TransplantOrDefault.AdultReadySize * 300;
        var onEnd = (int)Math.Ceiling(plants.Count * 0.15);
        var days = Enumerable.Range(0, plants.Count)
            .Select(i => i < onEnd ? end : end.AddDays(-(int)Math.Round(span * Math.Pow(_rng.NextDouble(), 2))))
            .OrderBy(d => d)
            .ToList();
        for (var i = 0; i < plants.Count; i++)
        {
            var plant = plants[i];
            var adult = days[i];
            var share = span == 0 ? 0 : (end.DayNumber - adult.DayNumber) / (double)span;
            // El día antes, apenas debajo del tamaño de cosecha; ese día, apenas arriba; al final, más cuanto antes llegó.
            plant.Milestones.Add((adult.AddDays(-1), ready * 0.985));
            if (adult < end)
                plant.Milestones.Add((adult, ready * 1.01));
            plant.Milestones.Add((end, ready * (1.03 + 0.3 * share)));
            plant.AdultFrom = adult;
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

    /// <summary>
    /// A qué tamaño (× el ideal de Baby Leaf) tiene que llegar y cuándo, según su destino. Los que dependen de un estado
    /// (candidata, en desarrollo, las que se trasplantan por score bajo) salen de las bandas de score de la configuración
    /// (candidata desde ScoreMinCandidate, Baby Leaf desde ScoreMinReady), con margen para el ruido de la evaluación.
    /// </summary>
    private void SetTarget(PlantRun plant, DemoLot lot, double gddToday)
    {
        double Between(double low, double high) => low + _rng.NextDouble() * (high - low);
        double Natural() => plant.Vigor * gddToday * 0.95 / 300;
        var candidate = (double)_w.Config.ScoreMinCandidate;
        var ready = (double)_w.Config.ScoreMinReady;
        double ForScore(DateOnly date, double low, double high) => RatioForScore(Between(low, high), ExpectedGdd(lot.Sowing, date));
        var reason = plant.FateReason ?? string.Empty;
        (DateOnly Date, double Ratio)? target = plant.Fate switch
        {
            PlantFate.CutBabyLeaf1 => (lot.Cut1!.Date, Between(0.95, 1.12)),
            PlantFate.CutBabyLeaf2 => (lot.Cut2!.Date, Between(0.95, 1.12)),
            PlantFate.TransplantActive or PlantFate.TransplantCut or PlantFate.TransplantDiscard =>
                (lot.Transplant!.Date, ForScore(lot.Transplant.Date, candidate - 12, candidate - 2)),
            PlantFate.DenseDiscard when reason.Contains("tamaño", StringComparison.OrdinalIgnoreCase) => (plant.FateDate!.Value, Between(1.33, 1.45)),
            PlantFate.DenseDiscard when reason.Contains("Damping", StringComparison.OrdinalIgnoreCase) => (plant.FateDate!.Value, Between(0.06, 0.18)),
            PlantFate.DenseDiscard when reason.Contains("Pythium", StringComparison.OrdinalIgnoreCase)
                                     || reason.Contains("Tipburn", StringComparison.OrdinalIgnoreCase)
                                     || reason.Contains("bolting", StringComparison.OrdinalIgnoreCase) => (plant.FateDate!.Value, Between(0.5, 0.8)),
            PlantFate.DenseDiscard => (plant.FateDate!.Value, ForScore(plant.FateDate!.Value, candidate + 4, ready - 5)),
            PlantFate.ActiveDeveloping => (_today, (decimal)gddToday >= _w.Config.GddMin
                ? ForScore(_today, candidate - 10, candidate - 4)
                : Math.Clamp(Natural(), 0.05, 0.9)),
            PlantFate.ActiveCandidate => (_today, ForScore(_today, candidate + 7, ready - 7)),
            PlantFate.ActiveBabyLeaf => (_today, Between(0.95, 1.1)),
            PlantFate.None => (_today, Math.Clamp(Natural(), 0.05, 1.1)),
            _ => null,
        };
        if (target is { } t)
            plant.Milestones.Add((t.Date, t.Ratio * 300));
    }

    /// <summary>
    /// Tamaño (× el ideal) con el que la evaluación da más o menos <paramref name="score"/> en la ventana Baby Leaf, con el
    /// crecimiento guiado: la ventana GDD suma completa, el estado visual según <paramref name="visual"/> (un poco de
    /// amarillo o daño es lo común), y la morfología y el crecimiento suben casi en línea recta con el tamaño (morfología ≈
    /// 100 × tamaño × (0,35 + 0,65 × calidad); crecimiento ≈ 100 × 300 × tamaño ÷ GDD a esa fecha, el acumulado).
    /// <paramref name="quality"/>: la calidad promedio del ciclo que se supone (más baja = más tamaño).
    /// </summary>
    private double RatioForScore(double score, double gdd, double quality = 0.9, double visual = 0.9)
    {
        var criteria = _w.Criteria.Where(c => c.IsActive).ToList();
        double Weight(string type) => (double)criteria.Where(c => c.DataType == type).Sum(c => c.Weight);
        var total = Math.Max(1, (double)criteria.Sum(c => c.Weight));
        var full = 100 * (Weight(BabyLeafScoreCalculator.DataTypeGdd) + Weight("VISUAL") * visual) / total;
        var shape = 0.35 + 0.65 * Math.Min(1, quality);
        var perRatio = 100 * (Weight("MORFOLOGIA") * shape + Weight("CRECIMIENTO") * Math.Min(1.6, 300 / Math.Max(1, gdd))) / total;
        return Math.Clamp((score - full) / Math.Max(1, perRatio), 0.04, 1.1);
    }

    /// <summary>
    /// Si ya tendría que verse Baby Leaf (o Adulta) y la evaluación de hoy no la da, se agrandan sus próximas metas: al día
    /// siguiente lo es (un ciclo más caluroso o con la CE más lejos que lo supuesto la deja atrás).
    /// </summary>
    private void EnsureReady(PlantRun plant, DateOnly date)
    {
        var behind = plant.BabyLeafFrom is { } babyLeaf && date >= babyLeaf && !plant.EvaluatedApt
                     || plant.AdultFrom is { } adult && date >= adult && SizeRatio(plant) < _w.TransplantOrDefault.AdultReadySize;
        if (!behind)
            return;
        // Una Baby Leaf no se agranda más allá de la meseta de la forma: más grande deja de ser Baby Leaf y quedaría
        // atrasada para siempre (cada día se agrandaría más).
        var cap = plant.AdultRoute ? double.MaxValue : ShapePlateauEnd * 300;
        for (var i = 0; i < plant.Milestones.Count; i++)
        {
            var (day, size) = plant.Milestones[i];
            if (day > date)
                plant.Milestones[i] = (day, Math.Max(size, Math.Min(cap, plant.Size * 1.12 + 12)));
        }
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
        foreach (var (until, target) in plant.Milestones)
        {
            if (date > until)
                continue;
            var remaining = Math.Max(0, grown) + ExpectedGdd(date, until);
            return remaining < 0.5 ? natural : Math.Clamp((target - plant.Size) / remaining, 0.02, 1.6);
        }
        return natural;
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
            var adultId = _w.CommercialStageIds[CommercialStageNames.CosechaConvencional];
            var notReady = babyLeaf ? plants.Count(p => !p.EvaluatedApt) : plants.Count(p => p.Plant.CommercialStageId != adultId);
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
