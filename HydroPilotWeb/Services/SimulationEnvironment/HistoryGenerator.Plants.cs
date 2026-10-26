using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.SimulationEnvironment.Model;

namespace HydroPilotWeb.Services.SimulationEnvironment;

/// <summary>
/// Plantas: crecen con el GDD según qué tan cerca del óptimo estuvo la solución (pH 5,8; CE de la estación, más baja
/// con calor), se evalúan una vez por día (imagen → métricas → score) y el flujo diario les da su estado comercial;
/// el productor corta Baby Leaf en la ventana y el resto a convencional.
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

    private void AddPlants(LotRun run, DateTime t)
    {
        var empty = new HashSet<(int, int)>();
        var gaps = _rng.Next(0, 4);
        while (empty.Count < gaps)
            empty.Add((_rng.Next(1, 7), _rng.Next(1, 11)));

        var developing = _w.CommercialStageIds[CommercialStageNames.EnDesarrollo];
        for (var row = 1; row <= 6; row++)
        {
            for (var column = 1; column <= 10; column++)
            {
                if (empty.Contains((row, column)))
                    continue;
                var plant = new Plant
                {
                    Id = ++_plantId,
                    LotId = run.Lot.Id,
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
                run.Plants.Add(new PlantRun(plant, Math.Clamp(1 + HistoryPlanner.Gaussian(_rng) * 0.25, 0.4, 1.45)));
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
        var day = run.Day;
        run.Day = new DayStats();

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

        // Estrés del último día.
        var meanT = day.MeanTemperature ?? 20;
        var meanRh = day.MeanHumidity ?? 65;
        var quality = Quality(day, stage, meanT);
        UpdateRoots(run, meanT, date);
        var tipburnDay = day.MaxEc >= 1.75 && day.MaxTemperature >= 27 && meanRh >= 62 && (double)gdd > 180;
        var yellowing = day.MeanPh switch
        {
            > 6.6 => 4.0,
            > 6.4 => 2.0,
            < 5.3 => 3.0,
            _ => -1.0,
        };

        foreach (var plant in run.Plants.Where(p => p.Active))
        {
            var health = plant.InfectedDays is { } d ? Math.Max(0.2, 1 - 0.15 * d) : 1.0;
            var efficiency = plant.Vigor * quality * health;
            plant.Size += Math.Max(0, grown) * efficiency;
            plant.QualitySum += quality;
            plant.QualityDays++;
            plant.RecentEfficiency.Enqueue(efficiency);
            while (plant.RecentEfficiency.Count > 3)
                plant.RecentEfficiency.Dequeue();
            if (tipburnDay && _rng.NextDouble() < 0.25)
                plant.Damage = Math.Min(80, plant.Damage + 2 + _rng.NextDouble() * 3);
            plant.Yellowing = Math.Clamp(plant.Yellowing + yellowing, 0, 60);
            if (plant.InfectedDays is { } infected)
                plant.InfectedDays = infected + 1;
        }

        Discards(run, date, t, meanT, day);

        // Con tipburn en un cuarto de las plantas, la productora baja la CE (deja su costumbre de CE alta).
        if (run.Plan.Incident == Incident.HeatTipburn && run.EcBias > 0
            && run.Plants.Count(p => p.Active && p.Damage >= 8) >= run.AliveCount / 4)
        {
            run.EcBias = 0;
            run.Events.Add((t, "tipburn", "se baja la CE"));
        }

        // Imagen → métricas → score de cada planta activa (desde que se ven, día 3).
        var risk = OpenEpisodeReason(run);
        var thresholds = new PlantDecisionEngine.PlantDecisionThresholds(
            _w.Config.GddMin, _w.Config.GddMax, _w.Stages[^1].GddMax, _w.Config.ScoreMinCandidate, _w.Config.ScoreMinReady);
        var scores = new List<decimal>();
        foreach (var plant in run.Plants.Where(p => p.Active))
        {
            if (cycleDay >= 3)
            {
                var score = Evaluate(run, plant, t.AddMinutes(-6), gdd, grown);
                scores.Add(score.Score);
                run.ScoreSamples.Add(new Control.RecipeRules.ScoreSample(plant.Plant.Id, DateOnly.FromDateTime(t), score.Score, Math.Round(gdd, 2)));
                plant.LastScore = score.Score;
                ApplyDecision(run, plant, PlantDecisionEngine.Evaluate(
                    new PlantDecisionEngine.PlantEvaluationInput(gdd, score.Score, score.Mandatory, null, risk is not null, risk),
                    thresholds), stage, t);
            }
            else if (plant.Plant.PhenologicalStageId != stage.Id)
            {
                ApplyDecision(run, plant, new PlantDecisionEngine.PlantDecision(CommercialStageNames.EnDesarrollo,
                    $"GDD {gdd} por debajo de la ventana Baby Leaf ({_w.Config.GddMin})"), stage, t);
            }
        }
        if (scores.Count > 0)
            run.DailyScores.Add((DateOnly.FromDateTime(t), Math.Round(scores.Average(), 2)));
        run.ScoreSamples.RemoveAll(s => s.Day < DateOnly.FromDateTime(t).AddDays(-12));

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

    /// <summary>Qué tan bien le fue a la planta ayer (0,3–1,03) según la solución y el calor.</summary>
    private double Quality(DayStats day, PhenologicalStage stage, double meanTemperature)
    {
        var q = 1.0 + HistoryPlanner.Gaussian(_rng) * 0.02;
        if (day.MeanEc is { } ec)
            q -= 0.17 * Math.Pow((ec - OptimalEc(stage, meanTemperature)) / 0.3, 2);
        if (day.MeanPh is { } ph)
            q -= 0.14 * Math.Pow((ph - OptimalPh) / 0.35, 2);
        if (day.MaxEc > 1.85)
            q -= 0.05;
        if (day.MaxTemperature > 30)
            q -= 0.03 * (day.MaxTemperature - 30);
        if (day.MeanPh is > 6.6 or < 5.2)
            q -= 0.1;
        return Math.Clamp(q, 0.3, 1.03);
    }

    /// <summary>Solución tibia (≥ 24 °C) 3 días seguidos: Pythium (UTS/Kentucky). Solo en el lote del incidente se propaga en serio.</summary>
    private void UpdateRoots(LotRun run, double meanTemperature, DateOnly date)
    {
        var solution = meanTemperature - 0.5;
        run.WarmDays = solution >= 24 ? run.WarmDays + 1 : 0;
        if (run.WarmDays < 3)
            return;

        var chance = run.Plan.Incident == Incident.Pythium ? 0.12 * (run.WarmDays - 2) : 0.004;
        foreach (var plant in run.Plants.Where(p => p.Active && p.InfectedDays is null))
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
        // Tamaño respecto del de Baby Leaf en el óptimo, por la forma: con malas condiciones la planta se estira.
        var morphology = Math.Clamp(100 * Math.Min(1, plant.Size / 300) * (0.35 + 0.65 * Math.Min(1, plant.AverageQuality))
                                    + HistoryPlanner.Gaussian(_rng) * 2.5, 0, 100);
        var efficiency = plant.RecentEfficiency.Count > 0 ? plant.RecentEfficiency.Average() : plant.Vigor;
        var growth = Math.Clamp(100 * efficiency + HistoryPlanner.Gaussian(_rng) * 3, 0, 100);
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
        var dayRate = plant.Size > 1 ? 100 * Math.Max(0, efficiency) * Math.Max(0, grown) / plant.Size : 0;
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
            ModelVersion = "CV-sim-1.0",
            AnalyzedAtUtc = at.AddMinutes(2),
        });
        _data.Evaluations.Add(new BabyLeafEvaluation
        {
            Id = ++_evaluationId,
            PlantId = plant.Plant.Id,
            BabyLeafConfigId = _w.Config.Id,
            PlantImageId = image.Id,
            EvaluatedAtUtc = at.AddMinutes(4),
            BabyLeafScore = score,
            Result = PlantDecisionEngine.Evaluate(
                new PlantDecisionEngine.PlantEvaluationInput(gdd, score, result.AllMandatoryMet, null, false, null),
                new PlantDecisionEngine.PlantDecisionThresholds(_w.Config.GddMin, _w.Config.GddMax, _w.Stages[^1].GddMax,
                    _w.Config.ScoreMinCandidate, _w.Config.ScoreMinReady)).CommercialStageName,
            Confidence = Math.Round((decimal)Math.Clamp(0.78 + _rng.NextDouble() * 0.18 - plant.Damage / 400, 0.5, 0.97), 2),
            ModelVersion = "BL-1.0",
            GddAtEvaluation = Math.Round(gdd, 2),
            MandatoryCriteriaMet = result.AllMandatoryMet,
            FoliarAreaUsed = Round(0.62 * Math.PI * diameter * diameter / 4),
            LeafLengthUsed = Round(diameter * 0.46),
            GrowthRateUsed = Round(dayRate),
        });
        return new ScoreResult(score, result.AllMandatoryMet);
    }

    private static decimal Round(double value) => Math.Round((decimal)value, 2);

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
        var apt = _w.CommercialStageIds[CommercialStageNames.BabyLeafApta];
        var active = run.Plants.Where(p => p.Active).ToList();

        if (active.Count == 0)
        {
            CloseLot(bench, t);
            return;
        }

        // Corte Baby Leaf: la regla híbrida de Lotes (ventana + % de aptas) o el último día útil de la ventana.
        var goal = (int)Math.Round((run.Lot.BabyLeafGoalPercent ?? 0m) / 100m * run.Plants.Count);
        var cut = run.Plants.Count(p => p.CutAsBabyLeaf);
        var aptPlants = active.Where(p => p.Plant.CommercialStageId == apt).OrderByDescending(p => p.LastScore).ToList();
        if (cut < goal && aptPlants.Count > 0 && gdd >= _w.Config.GddMin && gdd < _w.Config.GddMax)
        {
            var readiness = HarvestReadinessRules.Evaluate(new HarvestReadinessRules.Input(
                gdd, _w.Config.GddMin, _w.Config.GddMax, aptPlants.Count, active.Count, run.Lot.BabyLeafHarvestTargetPercent));
            var closing = gdd >= _w.Config.GddMax - 50m;
            var second = run.LastBabyLeafCut is { } last && date.DayNumber - last.DayNumber >= 2 && aptPlants.Count >= 5;
            if (readiness.IsReady == true || closing || second)
            {
                foreach (var plant in aptPlants.Take(goal - cut))
                    HarvestPlant(run, plant, date, t, babyLeaf: true);
                run.LastBabyLeafCut = date;
                run.Events.Add((t, "bl-cut", $"{Math.Min(aptPlants.Count, goal - cut)}"));
                if (run.HarvestAlert is { IsResolved: false } alert)
                {
                    alert.IsResolved = true;
                    alert.ResolvedAtUtc = t.AddMinutes(30);
                }
            }
        }

        // Corte convencional el día del óptimo (o el planificado), y lo que queda en riesgo se descarta.
        if (gdd >= CycleTimelineLogic.ConventionalOptimumGdd || date >= run.Plan.PlannedEnd)
        {
            // Las que están en riesgo (anomalía abierta) esperan al día siguiente; al final del ciclo se descartan.
            var risk = _w.CommercialStageIds[CommercialStageNames.RiesgoFueraDeVentana];
            var riskNow = OpenEpisodeReason(run) is not null;
            var last = date >= run.Plan.PlannedEnd.AddDays(2) || gdd >= _w.Stages[^1].GddMax - 30m;
            foreach (var plant in run.Plants.Where(p => p.Active).ToList())
            {
                if (plant.Plant.CommercialStageId != risk || !riskNow)
                    HarvestPlant(run, plant, date, t.AddMinutes(30), babyLeaf: false);
                else if (last)
                    Discard(run, plant, date, t.AddMinutes(40), "Fuera de ventana al cierre del ciclo");
            }
            run.Events.Add((t, "conv-cut", string.Empty));
        }

        if (run.Plants.All(p => !p.Active))
            CloseLot(bench, t.AddHours(1));
    }

    private void HarvestPlant(LotRun run, PlantRun plant, DateOnly date, DateTime at, bool babyLeaf)
    {
        var p = plant.Plant;
        var previous = p.CommercialStageId;
        if (!babyLeaf)
            p.CommercialStageId = _w.CommercialStageIds[CommercialStageNames.CosechaConvencional];
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
