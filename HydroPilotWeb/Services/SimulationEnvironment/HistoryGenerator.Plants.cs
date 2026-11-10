using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Control;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.SimulationEnvironment.Model;

namespace HydroPilotWeb.Services.SimulationEnvironment;

/// <summary>
/// Plantas: crecen con el GDD según qué tan cerca del óptimo estuvo la solución de su tanque (pH 5,8; CE de la
/// estación, más baja con calor), se evalúan una vez por día (imagen → métricas → score y tamaño) y el flujo diario
/// les da su estado comercial. A los 250 GDD las que no llegan a Baby Leaf y el excedente sobre la meta pasan a las
/// mesadas de Adultas (si entran); el productor corta Baby Leaf en el pico de aptas y las adultas en su óptimo. En el
/// denso, la que se pasó de tamaño o no se cortó antes de cerrar la ventana se descarta.
/// </summary>
public sealed partial class HistoryGenerator
{
    /// <summary>pH óptimo de la lechuga (Cornell CEA / Arkansas: 5,8). La web no lo sabe: lo aprende la receta.</summary>
    internal const double OptimalPh = 5.8;

    /// <summary>
    /// CE óptima de la etapa con la temperatura media del día: establecimiento 1,0; vegetativo 1,35; formación 1,45,
    /// 0,0125 menos por cada °C sobre 18 (verano ~1,2, invierno ~1,45: Arkansas). La de la etapa en la web es más alta
    /// (1,5 / 1,7): la receta tiene de dónde aprender.
    /// </summary>
    internal static double OptimalEc(PhenologicalStage stage, double meanTemperature) =>
        (double)stage.EcObjective - stage.Order switch { 1 => 0.0, 2 => 0.15, _ => 0.25 }
        - (stage.Order == 1 ? 0.008 : 0.0125) * (meanTemperature - 18);

    private void AddPlants(LotRun run, BenchRun bench, DateTime t)
    {
        // Una planta por hueco: un lote nunca empieza con posiciones vacías.
        var rows = bench.Bench.GridRows;
        var columns = bench.Bench.GridColumns;

        var developing = _w.CommercialStageIds[CommercialStageNames.Activa];
        for (var row = 1; row <= rows; row++)
        {
            for (var column = 1; column <= columns; column++)
            {
                var plant = new Plant
                {
                    Id = ++_plantId,
                    LotId = run.Lot.Id,
                    CircuitId = bench.Bench.CircuitId,
                    Row = row,
                    Column = column,
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
                run.Plants.Add(new PlantRun(plant, Math.Clamp(1 + HistoryPlanner.Gaussian(_rng) * 0.25, 0.4, 1.45)) { Bench = bench });
            }
        }
    }

    // ------------------------------------------------------------------
    // Evaluación diaria y flujo diario (08 h)
    // ------------------------------------------------------------------

    private void DailyFlow(BenchRun bench, DateTime t, DateTime local)
    {
        var run = bench.Lot!;
        var date = DateOnly.FromDateTime(local);
        run.EvaluatedOn = date;
        // Temperatura y humedad del invernadero; pH y CE de cada tanque donde está el lote.
        var day = run.Day;
        run.Day = new DayStats();
        var solutions = _benches.Where(b => b.Lot == run).ToDictionary(b => b, b =>
        {
            var solution = b.Day;
            b.Day = new DayStats();
            return solution;
        });

        var gdd = run.Gdd;
        var grown = (double)(gdd - run.GddAtLastFlow);
        run.GddAtLastFlow = gdd;
        var cycleDay = date.DayNumber - run.Lot.SowingDate.DayNumber;

        // Etapa fenológica por GDD (catálogo), como PlantEvaluationService.
        var stage = _w.Stages.FirstOrDefault(s => gdd >= s.GddMin && gdd < s.GddMax) ?? _w.Stages[^1];
        var stageChanged = stage.Id != run.Stage.Id;
        run.Stage = stage;
        run.Lot.AppliedPhenologicalStageId = stage.Id;
        run.Lot.PredominantPhenologicalStageId = stage.Id;

        // Estrés del último día, por tanque. Las trasplantadas frenan los días de estrés (su corte se corre ese GDD).
        var meanT = day.MeanTemperature ?? 20;
        var meanRh = day.MeanHumidity ?? 65;
        var qualities = solutions.ToDictionary(s => s.Key, s => Quality(s.Value, day, stage, meanT));
        UpdateRoots(run, meanT, date);
        var stressed = run.StressUntil is { } until && date <= until;
        if (stressed)
            run.StressGdd += Math.Max(0, (decimal)grown);

        foreach (var plant in run.Plants.Where(p => p.Active))
        {
            var solution = plant.Bench is { } b && solutions.TryGetValue(b, out var own) ? own : new DayStats();
            var quality = plant.Bench is { } pb && qualities.TryGetValue(pb, out var q) ? q : Quality(solution, day, stage, meanT);
            var tipburnDay = solution.MaxEc >= 1.75 && day.MaxTemperature >= 27 && meanRh >= 62 && (double)gdd > 180;
            var yellowing = solution.MeanPh switch
            {
                > 6.6 => 4.0,
                > 6.4 => 2.0,
                < 5.3 => 3.0,
                _ => -1.0,
            };
            var health = plant.InfectedDays is { } d ? Math.Max(0.2, 1 - 0.15 * d) : 1.0;
            var efficiency = plant.AdultRoute && stressed ? 0 : plant.Vigor * quality * health;
            // Con un dataset el crecimiento se guía (salvo los días de estrés del trasplante, que no crece).
            if (_script is not null && !(plant.AdultRoute && stressed))
                efficiency = GuidedEfficiency(plant, date, grown, efficiency);
            plant.LastGrowth = Math.Max(0, grown) * efficiency;
            plant.Size += plant.LastGrowth;
            plant.QualitySum += quality;
            plant.QualityDays++;
            if (tipburnDay && _rng.NextDouble() < 0.25 && !(_script is not null && plant.Protected))
                plant.Damage = Math.Min(_script is null || FatedTo(plant, "Tipburn") ? 80 : 12, plant.Damage + 2 + _rng.NextDouble() * 3);
            plant.Yellowing = Math.Clamp(plant.Yellowing + yellowing, 0, _script is not null && plant.Protected ? 5 : 60);
            if (plant.InfectedDays is { } infected)
                plant.InfectedDays = infected + 1;
            if (_script is not null)
                ScriptedDamage(plant, date);
        }

        if (_script is null)
            Discards(run, date, t, meanT, day);

        // Con tipburn en un cuarto de las plantas, la productora baja la CE (deja su costumbre de CE alta).
        if (run.Plan.Incident == Incident.HeatTipburn && run.EcBias > 0
            && run.Plants.Count(p => p.Active && p.Damage >= 8) >= run.AliveCount / 4)
        {
            run.EcBias = 0;
            run.Events.Add((t, "tipburn", "se baja la CE"));
        }

        // Imagen → métricas → score y tamaño de cada planta activa (desde que se ven, día 3).
        var risk = OpenEpisodeReason(run);
        var thresholds = Thresholds;
        var scores = new List<decimal>();
        var toDiscard = new List<(PlantRun Plant, string Reason)>();
        foreach (var plant in run.Plants.Where(p => p.Active))
        {
            if (cycleDay >= 3)
            {
                var score = Evaluate(run, plant, t.AddMinutes(-6), gdd, grown);
                var size = SizeRatio(plant);
                if (!plant.AdultRoute)
                {
                    scores.Add(score.Score);
                    run.ScoreSamples.Add(new Control.RecipeRules.ScoreSample(plant.Plant.Id, DateOnly.FromDateTime(t), score.Score, Math.Round(gdd, 2)));
                }
                plant.LastScore = score.Score;
                // Un patógeno que vio la cámara en esa planta (mapa R): Riesgo de ella sola desde ese día, como cuando llega
                // la foto (PlantImageController), con su anomalía y alerta el primer día.
                var plantRisk = plant.RiskFrom is { } riskFrom && date >= riskFrom ? plant.RiskReason : risk;
                var decision = PlantDecisionEngine.Evaluate(
                    new PlantDecisionEngine.PlantEvaluationInput(gdd, score.Score, score.Mandatory, plantRisk is not null, plantRisk, plant.AdultRoute, size),
                    thresholds);
                ApplyDecision(run, plant, decision, stage, t);
                if (plant.RiskFrom == date)
                    VisualRiskAnomaly(run, plant, t);
                if (decision.Discard)
                    toDiscard.Add((plant, decision.Reason ?? "Ya no puede ser Baby Leaf"));
                if (_script is not null)
                    EnsureReady(plant, date);
            }
            else if (plant.Plant.PhenologicalStageId != stage.Id)
            {
                ApplyDecision(run, plant, new PlantDecisionEngine.PlantDecision(CommercialStageNames.Activa,
                    $"GDD {gdd} por debajo de la ventana Baby Leaf ({_w.Config.GddMin})"), stage, t);
            }
        }
        if (scores.Count > 0)
            run.DailyScores.Add((DateOnly.FromDateTime(t), Math.Round(scores.Average(), 2)));
        run.ScoreSamples.RemoveAll(s => s.Day < DateOnly.FromDateTime(t).AddDays(-12));

        // Curva de la ruta Baby Leaf (las del denso, por su evaluación): aptas y score general. Se corta cuando el score
        // general deja de mejorar (BabyLeafOptimumRules.CutDay).
        if (cycleDay >= 3)
        {
            var evaluated = run.Plants.Where(p => p.Active && !p.AdultRoute).ToList();
            run.AptCurve.Add(new AptCurvePoint(date, Math.Round(gdd, 1), evaluated.Count(p => p.EvaluatedApt), evaluated.Count,
                scores.Count > 0 ? Math.Round(scores.Average(), 2) : null));
        }

        // La que se pasó de tamaño o no se cortó antes de cerrar la ventana: sin lugar para seguir como adulta. Con un
        // dataset, los descartes y el trasplante son los del Excel (ScriptedHarvest y ScriptedTransplant).
        if (_script is null)
        {
            foreach (var (plant, reason) in toDiscard)
                Discard(run, plant, date, t.AddMinutes(25), reason);

            // Trasplante a Adultas desde el GDD de decisión (una vez).
            if (!run.TransplantDone && TransplantRules.IsDue(gdd, _w.TransplantOrDefault.DecisionGdd, _w.Config.GddMax))
                Transplant(run, date, t.AddMinutes(40));
        }

        // Predominancia sobre activas (Mixto ante empate), como LotDailyFlowService.
        var counts = run.Plants.Where(p => p.Active)
            .GroupBy(p => StageName(p.Plant.CommercialStageId))
            .ToDictionary(g => g.Key, g => g.Count());
        var (winner, mixed) = Predominance.Resolve(counts);
        run.Lot.PredominantCommercialStageId = !mixed && winner is not null && _w.CommercialStageIds.TryGetValue(winner, out var id) ? id : null;
        run.Lot.IsCommercialStageMixed = mixed;

        if ((double)gdd >= LettuceUptake.BabyLeafClosesGdd && run.WindowClosedOn is null)
            run.WindowClosedOn = date;

        if (stageChanged)
            RecordSetpoint(run, t.AddMinutes(1));
        HarvestAlerts(run, t, date);
    }

    /// <summary>Qué tan bien le fue a la planta ayer (0,3–1,03) según la solución de su tanque y el calor.</summary>
    private double Quality(DayStats solution, DayStats ambient, PhenologicalStage stage, double meanTemperature)
    {
        var q = 1.0 + HistoryPlanner.Gaussian(_rng) * 0.02;
        if (solution.MeanEc is { } ec)
            q -= 0.17 * Math.Pow((ec - OptimalEc(stage, meanTemperature)) / 0.3, 2);
        if (solution.MeanPh is { } ph)
            q -= 0.14 * Math.Pow((ph - OptimalPh) / 0.35, 2);
        if (solution.MaxEc > 1.85)
            q -= 0.05;
        if (ambient.MaxTemperature > 30)
            q -= 0.03 * (ambient.MaxTemperature - 30);
        if (solution.MeanPh is > 6.6 or < 5.2)
            q -= 0.1;
        return Math.Clamp(q, 0.3, 1.03);
    }

    private PlantDecisionEngine.PlantDecisionThresholds Thresholds => new(
        _w.Config.GddMin, _w.Config.GddMax, _w.Stages[^1].GddMax, _w.Config.ScoreMinCandidate, _w.Config.ScoreMinReady,
        _w.TransplantOrDefault.MaxSizeFactor, _w.TransplantOrDefault.AdultReadySize, CycleTimelineLogic.ConventionalOptimumGdd);

    /// <summary>Tamaño respecto del ideal de Baby Leaf (300 = el de una planta perfecta en el óptimo).</summary>
    private static decimal SizeRatio(PlantRun plant) => Math.Round((decimal)(plant.Size / 300), 3);

    // ------------------------------------------------------------------
    // Trasplante a Adultas (08 h, desde el GDD de decisión)
    // ------------------------------------------------------------------

    /// <summary>
    /// Como Lotes (TransplantRules): pasan las de score bajo y el excedente sobre la meta Baby Leaf, hasta llenar los
    /// huecos de las mesadas de Adultas libres. Cada una con su asignación, su tanque y la Pi de siempre.
    /// </summary>
    private void Transplant(LotRun run, DateOnly date, DateTime t)
    {
        run.TransplantDone = true;
        var free = _benches.Where(b => b.IsAdult && b.Lot is null).ToList();
        var candidates = run.Plants.Where(p => p.Active && !p.AdultRoute)
            .Select(p => new TransplantCandidate(p.Plant.Id, p.LastScore))
            .ToList();
        var proposal = TransplantRules.Propose(candidates, run.Plants.Count, run.Lot.BabyLeafGoalPercent,
            free.Sum(b => b.Bench.Holes), _w.Config.ScoreMinCandidate, run.Plants.Count(p => p.CutAsBabyLeaf));
        if (proposal.PlantIds.Count == 0)
        {
            run.Events.Add((t, "transplant", $"nadie a Adultas, {proposal.NoRoom} sin lugar"));
            return;
        }

        var byId = run.Plants.ToDictionary(p => p.Plant.Id);
        var queue = new Queue<PlantRun>(proposal.PlantIds.Select(id => byId[id]).OrderBy(p => p.Plant.Row).ThenBy(p => p.Plant.Column));
        MoveToAdults(run, free, queue, date, t);

        run.StressUntil = date.AddDays(_w.TransplantOrDefault.StressDays);
        run.Lot.TransplantCostPerPlant = _w.TransplantOrDefault.CostPerPlant;
        run.Events.Add((t, "transplant", $"{proposal.PlantIds.Count} a Adultas, {proposal.NoRoom} sin lugar"));
    }

    /// <summary>Las plantas de la cola pasan, en orden, a los huecos de las mesadas de Adultas (cada una con su asignación).</summary>
    private void MoveToAdults(LotRun run, IEnumerable<BenchRun> benches, Queue<PlantRun> queue, DateOnly date, DateTime t)
    {
        // Como en la app: la trasplantada queda "Activa" hasta llegar a adulta.
        var developing = _w.CommercialStageIds[CommercialStageNames.Activa];
        foreach (var bench in benches)
        {
            if (queue.Count == 0)
                break;
            AttachAdultBench(bench, run, t);
            foreach (var (row, column) in TransplantRules.FreeHoles(bench.Bench.GridRows, bench.Bench.GridColumns, new HashSet<(int, int)>()))
            {
                if (queue.Count == 0)
                    break;
                var plant = queue.Dequeue();
                var p = plant.Plant;
                var previous = p.CommercialStageId;
                (p.DenseRow, p.DenseColumn) = (p.Row, p.Column);
                p.CircuitId = bench.Bench.CircuitId;
                p.Row = row;
                p.Column = column;
                p.TransplantedOn = date;
                p.CommercialStageId = developing;
                plant.Bench = bench;
                plant.AdultRoute = true;
                _data.StageHistories.Add(new PlantStageHistory
                {
                    Id = ++_historyId,
                    PlantId = p.Id,
                    PreviousOperationalState = p.OperationalState.ToString(),
                    PreviousCommercialStageId = previous,
                    NewOperationalState = p.OperationalState.ToString(),
                    NewCommercialStageId = developing,
                    Source = "transplant",
                    Reason = $"Trasplantada a {bench.Bench.CircuitName}",
                    ChangedAtUtc = t,
                });
            }
        }
    }

    /// <summary>La mesada de Adultas recibe al lote: su asignación desde ahora y la solución de la etapa en el tanque.</summary>
    private void AttachAdultBench(BenchRun bench, LotRun run, DateTime t)
    {
        var assignment = new CircuitAssignment
        {
            Id = ++_assignmentId,
            CircuitId = bench.Bench.CircuitId,
            LotId = run.Lot.Id,
            ValidFromUtc = t,
            Source = TelemetryContract.AssignmentSourceManual,
            CreatedAt = t,
        };
        _data.Assignments.Add(assignment);
        bench.Lot = run;
        bench.Assignment = assignment;
        bench.Day = new DayStats();
        bench.ClearReadings();
        foreach (var sensor in bench.Bench.Sensors.Values.Where(s => s.Type is AnomalyContract.SensorTypePh or AnomalyContract.SensorTypeCe))
            bench.Calibrations[sensor.Id] = t.AddMinutes(-30);
        bench.PhProbeOffset = 0;
        bench.Tank = new NutrientTank((double)bench.Bench.ReservoirLiters, 5.95 + _rng.NextDouble() * 0.15, (double)run.Stage.EcObjective + run.EcBias * 0.5);
        bench.Mode = ControlContract.ModeAuto;
    }

    /// <summary>Solución tibia (≥ 24 °C) 3 días seguidos: Pythium (UTS/Kentucky). Solo en el lote del incidente se propaga en serio.</summary>
    private void UpdateRoots(LotRun run, double meanTemperature, DateOnly date)
    {
        var solution = meanTemperature - 0.5;
        run.WarmDays = solution >= 24 ? run.WarmDays + 1 : 0;
        if (run.WarmDays < 3)
            return;

        var chance = run.Plan.Incident == Incident.Pythium ? 0.12 * (run.WarmDays - 2) : 0.004;
        foreach (var plant in run.Plants.Where(p => p.Active && p.InfectedDays is null && _script is null))
        {
            if (_rng.NextDouble() < chance)
                plant.InfectedDays = 0;
        }
        var infected = run.Plants.Count(p => p.Active && p.InfectedDays is not null);
        run.RootHealth = Math.Max(0.25, 1 - infected / (double)Math.Max(1, run.AliveCount) * 0.8);
        if (infected > 0 && !run.Events.Any(e => e.Kind == "pythium"))
            run.Events.Add((ToUtc(date.ToDateTime(new TimeOnly(8, 0))), "pythium", $"{infected} plantas con raíces marrones"));
    }

    /// <summary>Descartes del día: damping-off temprano, Pythium avanzado, bolting con calor en el convencional.</summary>
    private void Discards(LotRun run, DateOnly date, DateTime t, double meanTemperature, DayStats day)
    {
        var cycleDay = date.DayNumber - run.Lot.SowingDate.DayNumber;
        foreach (var plant in run.Plants.Where(p => p.Active).ToList())
        {
            string? reason = null;
            if (plant.InfectedDays >= 5)
                reason = "Pudrición de raíz (Pythium)";
            else if (cycleDay is >= 3 and <= 12 && _rng.NextDouble() < 0.0025)
                reason = "Damping-off en plántula";
            else if ((double)run.Gdd >= LettuceUptake.BabyLeafClosesGdd && day.MaxTemperature >= 29
                     && _rng.NextDouble() < (run.Plan.Incident == Incident.HeatTipburn ? 0.06 : 0.012))
                reason = "Floración prematura (bolting) por calor";
            else if (plant.Damage >= 45 && _rng.NextDouble() < 0.3)
                reason = "Tipburn severo: hojas quemadas";

            if (reason is not null)
                Discard(run, plant, date, t.AddMinutes(20), reason);
        }

        // Pythium: con más de la mitad enferma el productor descarta el lote y desinfecta la mesada.
        if (run.Plan.Incident == Incident.Pythium)
        {
            var sick = run.Plants.Count(p => p.Active && p.InfectedDays is not null);
            if (sick > 0 && sick >= run.AliveCount / 2)
            {
                foreach (var plant in run.Plants.Where(p => p.Active).ToList())
                    Discard(run, plant, date, t.AddMinutes(30), "Lote descartado por Pythium: se desinfecta la mesada");
                run.Events.Add((t.AddMinutes(30), "lot-discarded", "Pythium"));
            }
        }
    }

    private void Discard(LotRun run, PlantRun plant, DateOnly date, DateTime at, string reason)
    {
        var p = plant.Plant;
        p.OperationalState = PlantOperationalState.Descartada;
        p.DiscardDate = date;
        p.DiscardReason = reason;
        _data.StageHistories.Add(new PlantStageHistory
        {
            Id = ++_historyId,
            PlantId = p.Id,
            PreviousOperationalState = PlantOperationalState.Activa.ToString(),
            PreviousCommercialStageId = p.CommercialStageId,
            NewOperationalState = PlantOperationalState.Descartada.ToString(),
            NewCommercialStageId = p.CommercialStageId,
            Source = "discard",
            Reason = reason,
            ChangedAtUtc = at,
        });
    }

    private readonly record struct ScoreResult(decimal Score, bool Mandatory);

    /// <summary>
    /// La tarea diaria de imágenes: foto, métricas (análisis) y score con los criterios del catálogo
    /// (<see cref="BabyLeafScoreCalculator"/>): ventana GDD, morfología (tamaño respecto del óptimo), crecimiento de los
    /// últimos días y estado visual (tipburn, clorosis).
    /// </summary>
    private ScoreResult Evaluate(LotRun run, PlantRun plant, DateTime at, decimal gdd, double grown)
    {
        var size = SizeRatio(plant);
        // Tamaño respecto del de Baby Leaf en el óptimo, por la forma: con malas condiciones la planta se estira.
        var morphology = Math.Clamp(100 * ShapeFactor(plant.Size / 300) * (0.35 + 0.65 * Math.Min(1, plant.AverageQuality))
                                    + HistoryPlanner.Gaussian(_rng) * 2.5, 0, 100);
        // La morfología mide la forma de una Baby Leaf: la trasplantada, con más lugar, crece abierta y no la tiene. Así su
        // score Baby Leaf nunca llega al de una apta (ya no es de la ruta Baby Leaf).
        if (plant.AdultRoute)
            morphology = Math.Min(morphology, AdultMorphologyCap + HistoryPlanner.Gaussian(_rng) * 1.5);
        // Crecimiento acumulado: el tamaño respecto del que tendría creciendo a eficiencia 1 con el GDD del lote (desde
        // 2026-11-10; antes, la eficiencia de los últimos 3 días, que se desplomaba con un día flojo). Una planta que llega
        // a su tamaño y se frena lo baja de a poco.
        var growth = Math.Clamp(100 * plant.Size / Math.Max(1, (double)gdd) + HistoryPlanner.Gaussian(_rng) * 3, 0, 100);
        var visual = Math.Clamp(100 - 1.6 * plant.Damage - 1.2 * plant.Yellowing - (plant.InfectedDays is not null ? 25 : 0)
                                + HistoryPlanner.Gaussian(_rng) * 2, 0, 100);

        var metrics = new Dictionary<int, decimal>();
        foreach (var criterion in _w.Criteria.Where(c => c.IsActive))
        {
            var value = criterion.DataType switch
            {
                "MORFOLOGIA" => morphology,
                "CRECIMIENTO" => growth,
                "VISUAL" => visual,
                _ => (double?)null,
            };
            if (value is { } v)
                metrics[criterion.Id] = Math.Round((decimal)v, 1);
        }
        var result = BabyLeafScoreCalculator.Calculate(
            BabyLeafScoreCalculator.FromCatalog(_w.Criteria, metrics), gdd, _w.Config.GddMin, _w.Config.GddMax);
        var score = result.WeightedScore ?? 0m;

        // Imagen y análisis (la fase de visión computacional de la tarea diaria).
        var diameter = 1.5 + 13.5 * Math.Min(1.35, plant.Size / 300);
        var greenness = Math.Clamp(0.82 - plant.Yellowing / 150 - plant.Damage / 300 + HistoryPlanner.Gaussian(_rng) * 0.02, 0.3, 0.9);
        var dayRate = plant.Size > 1 ? 100 * Math.Max(0, plant.LastGrowth) / plant.Size : 0;
        var image = new PlantImage
        {
            Id = ++_imageId,
            PlantId = plant.Plant.Id,
            CapturedAtUtc = at,
            Path = $"img/sim/lechuga-{SizeClass(plant)}.svg",
            FileName = $"{run.Lot.Name?.Split(' ')[1]}-F{plant.Plant.Row}C{plant.Plant.Column}-{ToLocal(at):yyyyMMdd}.jpg",
            Width = 1280,
            Height = 960,
            Processed = true,
            ModelVersion = "CV-sim-1.0",
            ProcessedAtUtc = at.AddMinutes(2),
        };
        _data.Images.Add(image);
        _data.Analyses.Add(new PlantImageAnalysis
        {
            Id = ++_analysisId,
            PlantImageId = image.Id,
            PlantArea = Round(Math.PI * diameter * diameter / 4),
            FoliarArea = Round(0.62 * Math.PI * diameter * diameter / 4),
            LeafCount = Round(2 + plant.Size / 35),
            PlantWidth = Round(diameter),
            PlantHeight = Round(diameter * 0.55),
            RosetteDiameter = Round(diameter),
            LeafLength = Round(diameter * 0.46),
            AverageColor = Color(greenness),
            GreennessIndex = Math.Round((decimal)greenness, 3),
            DamagePercent = Round(plant.Damage),
            GrowthRate = Round(dayRate),
            ImageQuality = Math.Round((decimal)(0.82 + _rng.NextDouble() * 0.16), 2),
            // Lo que vio la cámara en una planta con patógeno (mapa R); las demás, sin diagnóstico.
            PredominantClass = plant.RiskFrom <= DateOnly.FromDateTime(ToLocal(at)) ? "FN" : null,
            HealthClass = plant.RiskFrom <= DateOnly.FromDateTime(ToLocal(at)) ? VisualRiskClass : null,
            HealthConfidence = plant.RiskFrom <= DateOnly.FromDateTime(ToLocal(at)) ? VisualRiskConfidence : null,
            ModelVersion = "CV-sim-1.0",
            AnalyzedAtUtc = at.AddMinutes(2),
        });
        var evaluatedAs = PlantDecisionEngine.Evaluate(
            new PlantDecisionEngine.PlantEvaluationInput(gdd, score, result.AllMandatoryMet, false, null, plant.AdultRoute, size),
            Thresholds).CommercialStageName;
        plant.EvaluatedApt = evaluatedAs == CommercialStageNames.BabyLeafApta;
        _data.Evaluations.Add(new BabyLeafEvaluation
        {
            Id = ++_evaluationId,
            PlantId = plant.Plant.Id,
            BabyLeafConfigId = _w.Config.Id,
            PlantImageId = image.Id,
            EvaluatedAtUtc = at.AddMinutes(4),
            BabyLeafScore = score,
            Result = evaluatedAs,
            Confidence = Math.Round((decimal)Math.Clamp(0.78 + _rng.NextDouble() * 0.18 - plant.Damage / 400, 0.5, 0.97), 2),
            ModelVersion = "BL-1.0",
            GddAtEvaluation = Math.Round(gdd, 2),
            MandatoryCriteriaMet = result.AllMandatoryMet,
            SizeRatio = size,
            FoliarAreaUsed = Round(0.62 * Math.PI * diameter * diameter / 4),
            LeafLengthUsed = Round(diameter * 0.46),
            GrowthRateUsed = Round(dayRate),
        });
        return new ScoreResult(score, result.AllMandatoryMet);
    }

    private static decimal Round(double value) => Math.Round((decimal)value, 2);

    /// <summary>Desde qué tamaño (× el ideal) la forma deja de ser la de una Baby Leaf: la hoja se estira y se abre.</summary>
    private const double ShapePlateauEnd = 1.1;

    /// <summary>
    /// Forma de Baby Leaf según el tamaño (× el ideal), como la banda comercial del modelo de visión: sube hasta el ideal,
    /// queda en meseta hasta <see cref="ShapePlateauEnd"/> y después baja de a poco (~0,78 en el tamaño máximo, 1,3).
    /// Así el score de cada planta llega a una cima y el general del lote deja de mejorar cuando la mayoría llegó.
    /// </summary>
    private static double ShapeFactor(double ratio) =>
        ratio <= 1 ? Math.Max(0, ratio)
        : ratio <= ShapePlateauEnd ? 1
        : Math.Max(0.4, 1 - 0.35 * Math.Pow((ratio - ShapePlateauEnd) / 0.25, 2));

    /// <summary>
    /// Morfología máxima de una trasplantada: con la ventana GDD, el crecimiento y el estado visual completos, su score
    /// queda 6 puntos debajo del de Baby Leaf apta (ScoreMinReady).
    /// </summary>
    private double AdultMorphologyCap
    {
        get
        {
            var criteria = _w.Criteria.Where(c => c.IsActive).ToList();
            double Weight(string type) => (double)criteria.Where(c => c.DataType == type).Sum(c => c.Weight);
            var total = Math.Max(1, (double)criteria.Sum(c => c.Weight));
            var others = 100 * (Weight(BabyLeafScoreCalculator.DataTypeGdd) + Weight("VISUAL") + Weight("CRECIMIENTO")) / total;
            var morphologyWeight = Weight("MORFOLOGIA") / total;
            return morphologyWeight <= 0 ? 0 : Math.Max(0, ((double)_w.Config.ScoreMinReady - 6 - others) / morphologyWeight);
        }
    }

    private static string SizeClass(PlantRun plant) =>
        plant.Damage >= 20 || plant.Yellowing >= 20 || plant.InfectedDays is not null ? "danada"
        : plant.Size < 60 ? "1"
        : plant.Size < 170 ? "2"
        : plant.Size < 320 ? "3"
        : "4";

    private static string Color(double greenness)
    {
        var g = (int)Math.Clamp(90 + greenness * 110, 0, 255);
        var r = (int)Math.Clamp(170 - greenness * 140, 0, 255);
        return $"#{r:X2}{g:X2}{40:X2}";
    }

    private string StageName(int? id) =>
        _w.CommercialStageIds.FirstOrDefault(kv => kv.Value == id).Key ?? "(sin estado)";

    /// <summary>Aplica la decisión del motor de Lotes a la planta y deja la transición en su historial.</summary>
    private void ApplyDecision(LotRun run, PlantRun plant, PlantDecisionEngine.PlantDecision decision, PhenologicalStage stage, DateTime t)
    {
        var p = plant.Plant;
        var target = _w.CommercialStageIds[decision.CommercialStageName];
        var phenoChanged = p.PhenologicalStageId != stage.Id;
        var commChanged = p.CommercialStageId != target;
        if (target == _w.CommercialStageIds[CommercialStageNames.BabyLeafApta])
            plant.WasApt = true;
        if (!phenoChanged && !commChanged)
            return;

        _data.StageHistories.Add(new PlantStageHistory
        {
            Id = ++_historyId,
            PlantId = p.Id,
            PreviousPhenologicalStageId = p.PhenologicalStageId,
            PreviousCommercialStageId = p.CommercialStageId,
            PreviousOperationalState = p.OperationalState.ToString(),
            NewPhenologicalStageId = phenoChanged ? stage.Id : null,
            NewCommercialStageId = commChanged ? target : null,
            NewOperationalState = p.OperationalState.ToString(),
            Source = "daily-flow",
            Reason = decision.Reason,
            ChangedAtUtc = t,
        });
        p.PhenologicalStageId = stage.Id;
        p.CommercialStageId = target;
    }

    // ------------------------------------------------------------------
    // Cosecha (10 h)
    // ------------------------------------------------------------------

    private void Harvest(BenchRun bench, DateTime t, DateTime local)
    {
        var run = bench.Lot!;
        var date = DateOnly.FromDateTime(local);
        run.HarvestCheckedOn = date;
        var gdd = run.Gdd;
        var active = run.Plants.Where(p => p.Active).ToList();

        if (active.Count == 0)
        {
            // Las lecturas de este paso ya son del lote: se cierra un minuto después.
            CloseLot(run, t.AddMinutes(1));
            return;
        }

        // Corte Baby Leaf (las del denso): en el pico de aptas (las aptas bajaron del mejor día), al cerrar la ventana
        // o, después del corte, las que llegaron (al menos 5, cada 2 días). Se corta todo lo que la evaluación dio Baby
        // Leaf, aunque haya una anomalía abierta (el riesgo es de la solución, no de la hoja).
        var aptPlants = active.Where(p => !p.AdultRoute && p.EvaluatedApt).ToList();
        if (aptPlants.Count > 0 && gdd >= _w.Config.GddMin && gdd < _w.Config.GddMax)
        {
            var peak = BabyLeafOptimumRules.PeakReached(run.AptCurve);
            var closing = gdd >= _w.Config.GddMax - 30m;
            var second = run.LastBabyLeafCut is { } last && date.DayNumber - last.DayNumber >= 2 && aptPlants.Count >= 5;
            if ((run.LastBabyLeafCut is null && peak) || closing || second)
            {
                foreach (var plant in aptPlants)
                    HarvestPlant(run, plant, date, t, babyLeaf: true);
                run.LastBabyLeafCut = date;
                run.Events.Add((t, "bl-cut", $"{aptPlants.Count}"));
                if (run.HarvestAlert is { IsResolved: false } alert)
                {
                    alert.IsResolved = true;
                    alert.ResolvedAtUtc = t.AddMinutes(30);
                }
            }
        }

        // El denso queda libre cuando no le quedan plantas del lote.
        if (run.DenseBench is { } dense && dense.Lot == run && !run.Plants.Any(p => p.Active && p.Bench == dense))
            ReleaseBench(dense, run, t.AddMinutes(45));

        // Corte de las adultas en su óptimo (650 GDD más lo que frenaron por el trasplante); las que están en riesgo
        // (anomalía abierta) esperan al día siguiente y, al final del ciclo, se descartan.
        var adults = run.Plants.Where(p => p.Active && p.AdultRoute).ToList();
        if (adults.Count > 0 && gdd >= CycleTimelineLogic.ConventionalOptimumGdd + run.StressGdd)
        {
            var risk = _w.CommercialStageIds[CommercialStageNames.RiesgoFueraDeVentana];
            var riskNow = OpenEpisodeReason(run) is not null;
            var lastDay = gdd >= _w.Stages[^1].GddMax - 30m;
            foreach (var plant in adults)
            {
                if (plant.Plant.CommercialStageId != risk || !riskNow)
                    HarvestPlant(run, plant, date, t.AddMinutes(30), babyLeaf: false);
                else if (lastDay)
                    Discard(run, plant, date, t.AddMinutes(40), "Fuera de ventana al cierre del ciclo");
            }
            run.Events.Add((t, "conv-cut", string.Empty));
        }

        // Cada mesada de Adultas queda libre con su última planta.
        foreach (var hosting in _benches.Where(b => b.IsAdult && b.Lot == run).ToList())
        {
            if (!run.Plants.Any(p => p.Active && p.Bench == hosting))
                ReleaseBench(hosting, run, t.AddMinutes(50));
        }

        if (run.Plants.All(p => !p.Active))
            CloseLot(run, t.AddHours(1));
    }

    private void HarvestPlant(LotRun run, PlantRun plant, DateOnly date, DateTime at, bool babyLeaf)
    {
        var p = plant.Plant;
        var previous = p.CommercialStageId;
        // Cosechada queda siempre "Adulta" o "Baby Leaf" (también la candidata que se corta como Baby Leaf).
        p.CommercialStageId = _w.CommercialStageIds[babyLeaf ? CommercialStageNames.BabyLeafApta : CommercialStageNames.CosechaConvencional];
        p.OperationalState = PlantOperationalState.Cosechada;
        p.HarvestDate = date;
        plant.CutAsBabyLeaf = babyLeaf;
        _data.StageHistories.Add(new PlantStageHistory
        {
            Id = ++_historyId,
            PlantId = p.Id,
            PreviousOperationalState = PlantOperationalState.Activa.ToString(),
            PreviousCommercialStageId = previous,
            NewOperationalState = PlantOperationalState.Cosechada.ToString(),
            NewCommercialStageId = p.CommercialStageId,
            Source = "harvest-bulk",
            Reason = babyLeaf ? "Cosecha masiva de Baby Leafs" : "Cosecha masiva de adultas",
            ChangedAtUtc = at,
        });
    }

    /// <summary>Resultado del ciclo como lo arma Lotes (BabyLeafOutlookService) para la receta de los lotes siguientes.</summary>
    private LotCycleOutcome Outcome(LotRun run)
    {
        var plants = run.Plants;
        var cut = plants.Count(p => p.CutAsBabyLeaf);
        var harvested = plants.Count(p => p.Plant.OperationalState == PlantOperationalState.Cosechada);
        var discarded = plants.Where(p => p.Plant.OperationalState == PlantOperationalState.Descartada).ToList();
        var early = discarded.Count(p => run.WindowClosedOn is null || p.Plant.DiscardDate <= run.WindowClosedOn);
        var measured = run.GddDays.Count > 0
            ? Math.Round((decimal)run.GddDays.Values.Average(d => GreenhouseClimate.DailyGdd(d.Max, d.Min, (double)_w.Crop.BaseTemperature)), 2)
            : (decimal?)null;
        return new LotCycleOutcome(run.Lot.Id, run.Lot.Name ?? $"Lote {run.Lot.Id}", run.Lot.SowingDate, plants.Count, cut,
            harvested - cut, discarded.Count, cut + plants.Count(p => !p.CutAsBabyLeaf && p.WasApt), early, discarded.Count - early,
            measured, false);
    }
}
