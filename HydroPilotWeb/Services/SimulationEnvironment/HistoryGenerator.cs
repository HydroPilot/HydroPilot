using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Control;
using HydroPilotWeb.Services.Anomalies;
using HydroPilotWeb.Services.Control;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.SimulationEnvironment.Dataset;
using HydroPilotWeb.Services.SimulationEnvironment.Model;
using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Services.SimulationEnvironment;

/// <summary>
/// Genera el historial del entorno de simulación (lógica pura y determinística: misma semilla, mismo historial).
/// Recorre el período cada <see cref="SimulationEnvironmentOptions.ReadingMinutes"/> minutos, mesada por mesada, y en
/// cada paso hace lo que pasaría de verdad, con las reglas de la app:
/// - el tanque y las plantas (<see cref="NutrientTank"/>, <see cref="LettuceUptake"/>, <see cref="GreenhouseClimate"/>);
/// - la Pi manda lecturas, clasificadas como la ingesta (<see cref="TelemetryValidationService"/>);
/// - el control, siempre automático (como si nunca hubiera estado en manual): el motor (<see cref="ControlEngine"/>)
///   con la receta aprendida (<see cref="RecipeRules"/>), medición de efecto, calibración y pausas;
/// - una vez por día, la evaluación de cada planta (score con <see cref="BabyLeafScoreCalculator"/>) y el flujo
///   diario (<see cref="PlantDecisionEngine"/>); a los 250 GDD, el trasplante a las mesadas de Adultas
///   (<see cref="TransplantRules"/>); el corte Baby Leaf en el pico de aptas (<see cref="BabyLeafOptimumRules"/>), el de
///   las adultas en su óptimo, y los descartes;
/// - anomalías (<see cref="AnomalyEvaluator"/>) y notificaciones de lo que pasó.
/// Con un dataset (<see cref="DemoScript"/>, el Excel de la demo) los lotes, sus fechas y cuántas plantas van a cada
/// destino salen del Excel: el clima, las lecturas, el control, las evaluaciones y las alertas se generan igual, y el
/// crecimiento de cada planta se guía para que llegue a su destino (ver HistoryGenerator.Script.cs).
/// </summary>
public sealed partial class HistoryGenerator
{
    private const int ExpectedIntervalSeconds = 300;
    private const string Firmware = "pi-1.4.2";

    private readonly SimWorld _w;
    private readonly SimulationEnvironmentOptions _o;
    private readonly DateTime _nowUtc;
    private readonly DateOnly _today;
    private readonly Random _rng;
    private readonly HistoryData _data = new();
    private readonly ControlOptions _control = new();
    private readonly TelemetryValidationService _validation = new(Microsoft.Extensions.Options.Options.Create(new TelemetryOptions()));
    private readonly int _stepMinutes;
    private readonly Action<HistoryData>? _flushTelemetry;
    private readonly DemoScript? _script;

    /// <summary>Lecturas acumuladas a partir de las cuales se descargan (ver <see cref="Generate"/>).</summary>
    public const int TelemetryChunk = 50_000;

    private Dictionary<DateOnly, ClimateDay> _climate = [];
    private readonly List<BenchRun> _benches = [];
    private readonly List<LotRun> _closedLots = [];

    // Id de cada tabla (el historial se inserta con sus Id).
    private int _lotId, _assignmentId, _plantId, _historyId, _imageId, _analysisId, _evaluationId, _batchId, _readingId,
        _rejectionId, _setpointId, _pauseId, _anomalyId, _alertId, _deliveryId,
        _attemptId, _forecastId, _weatherId, _priceId, _commentId;

    private HistoryGenerator(SimWorld world, SimulationEnvironmentOptions options, DateTime nowUtc, Action<HistoryData>? flushTelemetry,
        DemoScript? script)
    {
        _flushTelemetry = flushTelemetry;
        _script = script;
        _w = world;
        _o = options;
        _nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        _today = LocalDate(_nowUtc);
        _rng = new Random(options.Seed);
        _stepMinutes = Math.Clamp(options.ReadingMinutes, 5, 30);
    }

    /// <summary>
    /// Genera el historial. <paramref name="flushTelemetry"/>: cada <see cref="TelemetryChunk"/> lecturas se le pasa lo
    /// que ya no cambia (lecturas, lotes de telemetría, imágenes, análisis, evaluaciones e historial de plantas) para que
    /// lo guarde y vacíe esas listas; si no, queda todo en memoria (alcanza para historiales cortos de prueba).
    /// <paramref name="script"/>: el dataset de la demo ya controlado (<see cref="DemoDatasetPlanner"/>); null = al azar.
    /// </summary>
    public static HistoryData Generate(SimWorld world, SimulationEnvironmentOptions options, DateTime nowUtc,
        Action<HistoryData>? flushTelemetry = null, DemoScript? script = null)
    {
        var generator = new HistoryGenerator(world, options, nowUtc, flushTelemetry, script);
        generator.Run();
        return generator._data;
    }

    // ------------------------------------------------------------------
    // Recorrido
    // ------------------------------------------------------------------

    private void Run()
    {
        DateOnly historyStart;
        List<PlannedLot> plans;
        if (_script is { } script)
        {
            // Con un dataset: sus lotes y su clima.
            historyStart = script.Lots.Min(l => l.Lot.Sowing).AddDays(-7);
            _climate = HistoryPlanner.ScriptedClimate(historyStart.AddDays(-150), _today.AddDays(8), script.Dataset.Climate, _rng);
            plans = ScriptedPlans(script);
        }
        else
        {
            historyStart = _today.AddMonths(-Math.Clamp(_o.HistoryMonths, 2, 36));
            _climate = HistoryPlanner.Climate(historyStart.AddDays(-150), _today.AddDays(8), _rng);

            // Lotes en curso: mesada A cerca de la ventana Baby Leaf; mesada B recién sembrada. Solo se siembra en las
            // mesadas densas; las de Adultas reciben las trasplantadas.
            plans = [];
            var dense = 0;
            for (var i = 0; i < _w.Benches.Count; i++)
            {
                if (_w.Benches[i].IsAdult)
                    continue;
                plans.AddRange(HistoryPlanner.Plan(_climate, i, _today.AddDays(dense == 0 ? -12 : -4 - 4 * (dense - 1)), historyStart));
                dense++;
            }
            plans = HistoryPlanner.AssignIncidents(plans, _climate, _today);
        }

        for (var i = 0; i < _w.Benches.Count; i++)
            _benches.Add(new BenchRun(_w.Benches[i], plans.Where(p => p.Bench == i).OrderBy(p => p.Sowing).ToList(), _rng,
                exactSensor: _script is not null));

        AddPriceHistory(historyStart);

        var firstSowing = plans.Min(p => p.Sowing);
        var start = ToUtc(firstSowing.AddDays(-2).ToDateTime(TimeOnly.MinValue));
        start = new DateTime(start.Ticks - start.Ticks % TimeSpan.FromMinutes(_stepMinutes).Ticks, DateTimeKind.Utc);
        var step = TimeSpan.FromMinutes(_stepMinutes);

        var last = start;
        for (var t = start; t <= _nowUtc; t += step)
        {
            var local = ToLocal(t);
            if (t.TimeOfDay == TimeSpan.Zero)
                RefreshRecipes(t);
            foreach (var bench in _benches)
                StepBench(bench, t, local);
            last = t;
        }

        // La evaluación de hoy aunque se regenere antes de las 8 (desde 2026-11-10): el historial llega a hoy con el estado
        // del día. Si no, la última evaluación sería la de ayer y, por ejemplo, un lote en su día de corte no lo mostraría.
        var lastLocal = ToLocal(last);
        if (DateOnly.FromDateTime(lastLocal) == _today)
        {
            foreach (var bench in _benches)
            {
                if (bench.Lot is { Closed: false } lot && lot.EvaluatedOn != _today)
                    DailyFlow(bench, last, lastLocal);
            }
        }

        Finish(historyStart);
    }

    private void StepBench(BenchRun bench, DateTime t, DateTime local)
    {
        var date = DateOnly.FromDateTime(local);
        var minute = (int)local.TimeOfDay.TotalMinutes;

        // Siembra a la medianoche del día planificado (si el lote anterior seguía en la mesada, se termina antes).
        if (bench.NextPlan < bench.Plans.Count && bench.Plans[bench.NextPlan].Sowing <= date)
        {
            if (bench.Lot is not null)
                ForceClose(bench, t);
            StartLot(bench, bench.Plans[bench.NextPlan++], t);
        }

        var climate = ClimateAt(local);
        var lot = bench.Lot;
        if (lot is not null && bench.Tank is { } tank)
        {
            var alive = lot.Plants.Count(p => p.Active && p.Bench == bench);
            var rootHealth = lot.RootHealth;
            var uptake = LettuceUptake.For(alive, (double)lot.Gdd, _stepMinutes * 60, local.TimeOfDay.TotalHours,
                climate.Temperature, climate.Humidity, tank.Ec, tank.Liters, rootHealth);
            tank.Step(_stepMinutes * 60, uptake);
        }

        Incidents(bench, t, local);
        SendReadings(bench, t, local, climate);
        MeasureEffects(bench, t);

        if (lot is not null && !lot.Closed)
        {
            Control(bench, t);
            if (minute is >= 8 * 60 and < 8 * 60 + 10 && lot.EvaluatedOn != date)
                DailyFlow(bench, t, local);
            if (minute is >= 9 * 60 and < 9 * 60 + 20)
                TopUp(bench, t);
            if (minute is >= 10 * 60 and < 10 * 60 + 10 && lot.HarvestCheckedOn != date)
            {
                if (lot.Plan.Script is null)
                    Harvest(bench, t, local);
                else
                    ScriptedHarvest(bench, t, local);
            }
            // Con un dataset el trasplante va a las 11, después de los cortes del día (un circuito de Adultas que se
            // vacía hoy ya puede recibir).
            if (minute is >= 11 * 60 and < 11 * 60 + 10 && lot.Plan.Script is not null && bench == lot.DenseBench)
                ScriptedTransplant(lot, date, t);
        }
    }

    // ------------------------------------------------------------------
    // Clima
    // ------------------------------------------------------------------

    private readonly record struct ClimateNow(double Temperature, double Humidity, ClimateDay Day);

    private double _microTemperature;

    private ClimateNow ClimateAt(DateTime local)
    {
        var date = DateOnly.FromDateTime(local);
        var today = _climate[date];
        var previous = _climate.GetValueOrDefault(date.AddDays(-1)) ?? today;
        var next = _climate.GetValueOrDefault(date.AddDays(1)) ?? today;
        _microTemperature = 0.8 * _microTemperature + HistoryPlanner.Gaussian(_rng) * 0.25;
        var temperature = GreenhouseClimate.TemperatureAt(previous.Inside, today.Inside, next.Inside, local.TimeOfDay.TotalHours)
                          + _microTemperature;
        return new ClimateNow(temperature, GreenhouseClimate.HumidityAt(today.Inside, temperature), today);
    }

    // ------------------------------------------------------------------
    // Lotes: inicio y cierre
    // ------------------------------------------------------------------

    private void StartLot(BenchRun bench, PlannedLot plan, DateTime t)
    {
        // Meta Baby Leaf del ciclo: más convencional en verano (crece rápido), más Baby Leaf en invierno. Con un
        // dataset, la del Excel (vacía: la de la estación).
        var bl = plan.Sowing.Month switch
        {
            12 or 1 or 2 => 60m,
            6 or 7 or 8 => 80m,
            _ => 70m,
        };
        if (plan.Script is null && _rng.NextDouble() < 0.2)
            bl += 10m;
        if (plan.Script?.Lot.BabyLeafGoal is { } goal)
            bl = goal;

        var lot = new Lot
        {
            Id = ++_lotId,
            GreenhouseId = _w.GreenhouseId,
            CropTypeId = _w.Crop.Id,
            StatusId = _w.LotStatusIds[LotStatusNames.Activo],
            Name = plan.Script?.Lot.Name ?? LotName(bench, plan),
            SowingDate = plan.Sowing,
            // Superficie de la mesada densa (huecos × separación²): la usa el rendimiento por m² del pronóstico.
            PlantedAreaM2 = Math.Round(bench.Bench.Holes * _w.TransplantOrDefault.DenseHoleM2, 2),
            GridRows = bench.Bench.GridRows,
            GridColumns = bench.Bench.GridColumns,
            BabyLeafHarvestTargetPercent = 70m,
            BabyLeafGoalPercent = bl,
            ExpectedActivePercent = plan.Script?.Lot.ActiveGoal ?? 90m,
            CreatedAt = t.AddDays(-1).AddHours(14),
            AppliedPhenologicalStageId = _w.Stages[0].Id,
            PredominantPhenologicalStageId = _w.Stages[0].Id,
        };
        lot.CircuitId = bench.Bench.CircuitId;
        _data.Lots.Add(lot);
        // El comentario del Excel sobre la siembra: de la productora, a la mañana del día que se sembró.
        if (plan.Script?.Lot.Comment is { } comment)
        {
            _data.Comments.Add(new LotComment
            {
                Id = ++_commentId,
                LotId = lot.Id,
                UserId = _w.Producer.Id,
                AuthorName = _w.Producer.Name,
                Text = comment,
                CreatedAtUtc = ToUtc(plan.Sowing.ToDateTime(new TimeOnly(9, 15))),
            });
        }

        var assignment = new CircuitAssignment
        {
            Id = ++_assignmentId,
            CircuitId = bench.Bench.CircuitId,
            LotId = lot.Id,
            ValidFromUtc = t,
            Source = TelemetryContract.AssignmentSourceManual,
            CreatedAt = t.AddHours(-2),
        };
        _data.Assignments.Add(assignment);

        var run = new LotRun(plan, lot, assignment, _w.Stages[0])
        {
            StartUtc = t,
            // CE con la que se arma el tanque: cada lote arranca un poco distinto (el del calor, con CE alta).
            EcBias = plan.Incident == Incident.HeatTipburn ? 0.3 : Math.Round(-0.25 + _rng.NextDouble() * 0.45, 2),
            DenseBench = bench,
        };
        if (plan.Script is null)
            AddPlants(run, bench, t);
        else
            AddScriptedPlants(run, bench, t);
        bench.Lot = run;
        bench.Assignment = assignment;
        bench.Day = new DayStats();

        // Sondas calibradas al armar el lote; incidentes de hardware programados.
        bench.Calibrations[bench.Bench.Sensors[AnomalyContract.SensorTypePh].Id] = t.AddMinutes(-40);
        bench.Calibrations[bench.Bench.Sensors[AnomalyContract.SensorTypeCe].Id] = t.AddMinutes(-35);
        bench.PhProbeOffset = 0;
        switch (plan.Incident)
        {
            case Incident.PiOffline:
                var saturday = plan.Sowing.AddDays(8);
                while (saturday.DayOfWeek != DayOfWeek.Saturday)
                    saturday = saturday.AddDays(1);
                var offlineFrom = ToUtc(saturday.ToDateTime(new TimeOnly(20, 0)));
                foreach (var each in _benches)
                {
                    each.OfflineFrom = offlineFrom;
                    each.OfflineUntil = offlineFrom.AddHours(31);
                }
                run.Events.Add((offlineFrom, "offline", "router del invernadero caído"));
                break;
            case Incident.PhDownClogged:
                run.ArmedIncident = (ControlContract.KindPhDown, t.AddDays(9));
                break;
            case Incident.NutrientEmpty:
                run.ArmedIncident = (ControlContract.KindNutrientA, t.AddDays(12));
                break;
        }

        // Solución nueva: CE de la etapa de establecimiento (más la costumbre del operario) y pH ~6.
        var stage = _w.Stages[0];
        bench.Tank = new NutrientTank((double)bench.Bench.ReservoirLiters, 5.95 + _rng.NextDouble() * 0.15, (double)stage.EcObjective + run.EcBias * 0.5);
        bench.Mode = ControlContract.ModeAuto;

        run.Recipe = ComputeRecipe(run, t, previous: null);
        RecordSetpoint(run, t);
        _data.Story.Add($"{lot.Name}: siembra {plan.Sowing:dd/MM/yyyy}{(plan.Incident != Incident.None ? $" · {IncidentText(plan.Incident)}" : null)}"
                        + (plan.Script is not null ? $" · {run.Plants.Count} plantas (dataset)" : null));
    }

    /// <summary>
    /// El lote anterior seguía en la mesada el día de la siguiente siembra: las Baby Leaf que quedan se cortan y el resto
    /// se descarta (no hay lugar para seguir como adulta). Sus adultas siguen en las mesadas de Adultas.
    /// </summary>
    private void ForceClose(BenchRun bench, DateTime t)
    {
        var run = bench.Lot!;
        var date = LocalDate(t).AddDays(-1);
        var apt = _w.CommercialStageIds[CommercialStageNames.BabyLeafApta];
        foreach (var plant in run.Plants.Where(p => p.Active && p.Bench == bench).ToList())
        {
            if (plant.Plant.CommercialStageId == apt)
                HarvestPlant(run, plant, date, t.AddHours(-12), babyLeaf: true);
            else
                Discard(run, plant, date, t.AddHours(-12), "Fuera de ventana al cierre del ciclo");
        }
        // Hasta la medianoche las lecturas de la mesada siguen siendo del lote.
        ReleaseBench(bench, run, t);
        if (run.Plants.All(p => !p.Active))
            CloseLot(run, t);
    }

    /// <summary>"Lote B26 · 08/10/2026": mesada, número de lote en la mesada y fecha de siembra.</summary>
    private static string LotName(BenchRun bench, PlannedLot plan) =>
        $"Lote {bench.Bench.Letter}{plan.Number:00} · {plan.Sowing.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}";

    /// <summary>El lote deja la mesada: se cierra su asignación, se resuelve lo abierto en sus sondas y se vacía el tanque.</summary>
    private void ReleaseBench(BenchRun bench, LotRun run, DateTime t)
    {
        if (bench.Assignment is { } assignment)
            assignment.ValidUntilUtc = t;
        BuildAnomalies(run, bench, t);
        bench.Lot = null;
        bench.Tank = null;
        bench.Assignment = null;
        bench.Day = new DayStats();
        bench.ClearReadings();
    }

    /// <summary>No le quedan plantas activas: el lote se cierra con su rendimiento, su pico de aptas y su receta.</summary>
    private void CloseLot(LotRun run, DateTime t)
    {
        var lot = run.Lot;
        run.Closed = true;
        run.EndUtc = t;
        foreach (var hosting in _benches.Where(b => b.Lot == run).ToList())
            ReleaseBench(hosting, run, t);

        var harvested = run.Plants.Where(p => p.Plant.OperationalState == PlantOperationalState.Cosechada).ToList();
        if (harvested.Count > 0)
        {
            lot.StatusId = _w.LotStatusIds[LotStatusNames.Cosechado];
            var date = harvested.Max(p => p.Plant.HarvestDate!.Value);
            lot.ActualHarvestDate = date;
            var bl = harvested.Count(p => p.CutAsBabyLeaf);
            var conventional = harvested.Count - bl;
            lot.HarvestBabyLeafGramsPerPlant = _w.BabyLeafGramsPerPlant;
            lot.HarvestConventionalGramsPerPlant = _w.ConventionalGramsPerPlant;
            var (blPrice, convPrice) = PricesOn(date);
            lot.HarvestBabyLeafPricePerKg = blPrice;
            lot.HarvestConventionalPricePerKg = convPrice;
            lot.HarvestPriceCurrency = _w.Currency;
            lot.ActualYieldKg = Math.Round((bl * _w.BabyLeafGramsPerPlant + conventional * _w.ConventionalGramsPerPlant) / 1000m, 2);
        }
        else
        {
            lot.StatusId = _w.LotStatusIds[LotStatusNames.Descartado];
        }

        lot.AccumulatedGdd = Math.Round(run.Gdd, 2);
        lot.CurrentPh = run.LastPh;
        lot.CurrentEc = run.LastEc;
        lot.BabyLeafPeakGdd = BabyLeafOptimumRules.Peak(run.AptCurve)?.Gdd;
        if (run.Recipe is { } recipe)
            _data.Recipes.Add(recipe);

        if (run.HarvestAlert is { IsResolved: false } harvest)
        {
            harvest.IsResolved = true;
            harvest.ResolvedAtUtc = t;
        }
        _closedLots.Add(run);
    }

    // ------------------------------------------------------------------
    // Lecturas
    // ------------------------------------------------------------------

    private void SendReadings(BenchRun bench, DateTime t, DateTime local, ClimateNow climate)
    {
        var lot = bench.Lot;
        // Temperatura y humedad son del invernadero: las manda una sola mesada.
        var values = new List<(SimSensor Sensor, decimal Value)>();
        if (bench.Bench.HasAmbient)
        {
            values.Add((bench.Bench.Sensors[AnomalyContract.SensorTypeTemperatura],
                Math.Round((decimal)(climate.Temperature + bench.TemperatureOffset + HistoryPlanner.Gaussian(_rng) * 0.12), 1)));
            values.Add((bench.Bench.Sensors[AnomalyContract.SensorTypeHumedad],
                Math.Round((decimal)Math.Clamp(climate.Humidity + HistoryPlanner.Gaussian(_rng) * 1.2, 5, 100), 0)));
        }
        if (lot is not null && bench.Tank is { } tank)
        {
            var ph = tank.Ph + bench.PhProbeOffset + HistoryPlanner.Gaussian(_rng) * 0.012;
            var ec = tank.Ec + HistoryPlanner.Gaussian(_rng) * 0.012;
            values.Insert(0, (bench.Bench.Sensors[AnomalyContract.SensorTypeCe], Math.Round((decimal)Math.Max(0, ec), 2)));
            values.Insert(0, (bench.Bench.Sensors[AnomalyContract.SensorTypePh], Math.Round((decimal)ph, 2)));
        }

        if (values.Count == 0)
        {
            // Mesada sin lote ni sensores del invernadero: no hay nada que mandar (la Pi igual consulta).
            if (!bench.IsOffline(t))
                bench.LastPollUtc = t;
            return;
        }

        var batch = new PendingBatch(t, ++bench.Sequence, values);
        if (bench.IsOffline(t))
        {
            bench.Buffer.Add(batch);
            return;
        }

        // Al volver la conexión la Pi manda lo que guardó (las más viejas de 24 h quedan como "obsoletas"). Recién
        // reiniciada, con el reloj sin sincronizar, manda una lectura "del futuro" que la web rechaza.
        if (bench.Buffer.Count > 0)
        {
            var receivedAt = t.AddSeconds(2);
            foreach (var buffered in bench.Buffer)
            {
                Persist(bench, buffered, receivedAt);
                receivedAt = receivedAt.AddMilliseconds(40);
            }
            bench.Buffer.Clear();
            if (bench.Bench.HasAmbient)
            {
                var sensor = bench.Bench.Sensors[AnomalyContract.SensorTypeTemperatura];
                AddRejection(bench, $"pi-{bench.Bench.Identifier}-reboot-{t:yyyyMMddHHmm}", $"sim-{sensor.TechnicalKey}-reboot-{t:yyyyMMddHHmm}",
                    sensor.TechnicalKey, t.AddHours(3), receivedAt, values[^2].Value, sensor.UnitSymbol,
                    TelemetryContract.ReasonTimestampFuturo, TelemetryContract.QualityFuture, TelemetryContract.ResultInvalida);
            }
        }
        else if (bench.Bench.HasAmbient && _rng.NextDouble() < 0.0006)
        {
            // Firmware que a veces manda un sensor que la web no conoce.
            AddRejection(bench, $"pi-{bench.Bench.Identifier}-{t:yyyyMMddHHmm}", $"sim-temp-solucion-{t:yyyyMMddHHmm}", "temp-solucion",
                t, t.AddSeconds(1), Math.Round((decimal)(climate.Temperature - 1), 1), "°C",
                TelemetryContract.ReasonSensorDesconocido, TelemetryContract.QualityInvalid, TelemetryContract.ResultDesconocida);
        }

        Persist(bench, batch, t.AddSeconds(1 + _rng.NextDouble() * 2));
        bench.LastPollUtc = t;
    }

    private void Persist(BenchRun bench, PendingBatch batch, DateTime receivedAt)
    {
        var lot = bench.Lot;
        var stamp = batch.ObservedAtUtc.ToString("yyyyMMddHHmm");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{bench.Bench.Identifier}|{stamp}|{string.Join(";", batch.Values.Select(v => $"{v.Sensor.TechnicalKey}={v.Value}"))}"))).ToLowerInvariant();

        _data.Batches.Add(new TelemetryBatch
        {
            Id = ++_batchId,
            NodeId = bench.Bench.NodeId,
            BatchId = $"pi-{bench.Bench.Identifier}-{bench.Bench.Letter.ToLowerInvariant()}-{stamp}",
            SchemaVersion = TelemetryContract.SchemaVersionV2,
            Sequence = batch.Sequence,
            SentAtUtc = receivedAt.AddMilliseconds(-300),
            ReceivedAtUtc = receivedAt,
            FirmwareVersion = Firmware,
            PayloadHash = hash,
            Result = TelemetryContract.BatchResultProcesado,
            CreatedAt = receivedAt,
        });

        var assigned = lot is not null && bench.Assignment is { } current && batch.ObservedAtUtc >= current.ValidFromUtc && !lot.Closed;
        foreach (var (sensor, value) in batch.Values)
        {
            var classification = _validation.Classify(sensor.Type, value, batch.ObservedAtUtc, sensor.UnitSymbol, sensor.UnitSymbol,
                null, receivedAt, ExpectedIntervalSeconds);
            var readingId = $"sim-{sensor.TechnicalKey}-{stamp}";
            if (!classification.Persisted)
            {
                AddRejection(bench, $"pi-{bench.Bench.Identifier}-{stamp}", readingId, sensor.TechnicalKey, batch.ObservedAtUtc, receivedAt,
                    value, sensor.UnitSymbol, classification.Reason ?? classification.Result, classification.Quality, classification.Result);
                continue;
            }

            // Las del invernadero (temperatura, humedad) no van a un lote; las de pH y CE, al lote del circuito.
            var ambient = sensor.Type is AnomalyContract.SensorTypeTemperatura or AnomalyContract.SensorTypeHumedad;
            var toLot = assigned && !ambient;
            var result = classification.Result == TelemetryContract.ResultAceptada
                ? (toLot || ambient ? TelemetryContract.ResultAceptada : TelemetryContract.ResultSinLote)
                : classification.Result;
            var reading = new SensorReading
            {
                Id = ++_readingId,
                SensorId = sensor.Id,
                NodeId = bench.Bench.NodeId,
                LotId = toLot ? lot!.Lot.Id : null,
                CircuitAssignmentId = toLot ? bench.Assignment!.Id : null,
                Value = value,
                MeasurementUnitId = sensor.UnitId,
                ObservedAtUtc = batch.ObservedAtUtc,
                ReceivedAtUtc = receivedAt,
                Timestamp = batch.ObservedAtUtc,
                ExternalReadingId = readingId,
                Quality = classification.Quality,
                QualityReason = classification.Reason,
                IngestionResult = result,
                CreatedAt = receivedAt,
            };
            _data.Readings.Add(reading);
            bench.LastAcceptedUtc = receivedAt;
            if (TelemetryQualityPolicy.IsOperationallyUsable(reading.Quality))
            {
                // El ambiente vale para los lotes de todas las mesadas (GDD de cada uno).
                if (ambient)
                    foreach (var other in _benches)
                        Observe(other, reading, sensor.Type);
                else
                    Observe(bench, reading, sensor.Type);
            }
        }
        bench.LastConnectionUtc = receivedAt;
        if (_flushTelemetry is not null && _data.Readings.Count >= TelemetryChunk)
            _flushTelemetry(_data);
    }

    /// <summary>Lo que se guarda de cada lectura usable para el control, el GDD, las anomalías y las plantas.</summary>
    private void Observe(BenchRun bench, SensorReading reading, string type)
    {
        var lot = bench.Lot;
        var at = reading.ObservedAtUtc;
        switch (type)
        {
            case AnomalyContract.SensorTypePh or AnomalyContract.SensorTypeCe:
                bench.Remember(type, reading);
                if (lot is not null && reading.LotId == lot.Lot.Id)
                {
                    if (type == AnomalyContract.SensorTypePh)
                        bench.Day.AddPh((double)reading.Value);
                    else
                        bench.Day.AddEc((double)reading.Value);
                    // La receta aprende de la solución del circuito denso (la de la Baby Leaf).
                    if (bench == lot.DenseBench)
                        lot.Remember(type, reading);
                    TrackAnomaly(bench, lot, type, reading);
                    if (type == AnomalyContract.SensorTypePh)
                        lot.LastPh = reading.Value;
                    else
                        lot.LastEc = reading.Value;
                }
                break;
            case AnomalyContract.SensorTypeTemperatura when lot is not null:
                // GDD como GddService: lecturas desde la siembra (00 h UTC), agrupadas por fecha local.
                var sowingUtc = lot.Lot.SowingDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                if (at >= sowingUtc && !lot.Closed)
                {
                    lot.AddTemperature(DateOnly.FromDateTime(ToLocal(at)), (double)reading.Value);
                    lot.Gdd = (decimal)lot.GddDays.Values.Sum(d => GreenhouseClimate.DailyGdd(d.Max, d.Min, (double)_w.Crop.BaseTemperature));
                    lot.Lot.AccumulatedGdd = Math.Round(lot.Gdd, 2);
                }
                lot.Day.AddTemperature((double)reading.Value);
                break;
            case AnomalyContract.SensorTypeHumedad when lot is not null:
                lot.Day.AddHumidity((double)reading.Value);
                break;
        }
    }

    private void AddRejection(BenchRun bench, string batchId, string readingId, string sensorRef, DateTime? observed, DateTime received,
        decimal? value, string? unit, string reason, string? quality, string result)
    {
        _data.Rejections.Add(new TelemetryRejection
        {
            Id = ++_rejectionId,
            NodeId = bench.Bench.NodeId,
            BatchId = batchId,
            ReadingId = readingId,
            SensorRef = sensorRef,
            ObservedAtUtc = observed,
            ReceivedAtUtc = received,
            Value = value,
            Unit = unit,
            Reason = reason,
            Quality = quality,
            Result = result,
            CreatedAt = received,
        });
        bench.LastRejectedUtc = received;
    }

    // ------------------------------------------------------------------
    // Auxiliares de tiempo
    // ------------------------------------------------------------------

    private DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _w.TimeZone);

    private DateTime ToUtc(DateTime local) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _w.TimeZone);

    private DateOnly LocalDate(DateTime utc) => DateOnly.FromDateTime(ToLocal(utc));

    private static string IncidentText(Incident incident) => incident switch
    {
        Incident.Pythium => "ola de calor y Pythium: lote descartado",
        Incident.HeatTipburn => "calor con CE alta: tipburn y bolting",
        Incident.PiOffline => "la Pi sin conexión un fin de semana",
        Incident.PhProbeDrift => "sonda de pH descalibrada",
        Incident.PhDownClogged => "bomba de Bajar pH tapada: control de pH pausado",
        Incident.NutrientEmpty => "bidón de nutrientes vacío: control de CE pausado",
        _ => string.Empty,
    };
}
