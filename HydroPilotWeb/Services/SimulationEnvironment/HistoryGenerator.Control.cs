using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Control;
using HydroPilotWeb.Models.Notifications;
using HydroPilotWeb.Services.Anomalies;
using HydroPilotWeb.Services.Control;
using HydroPilotWeb.Services.Notifications;

namespace HydroPilotWeb.Services.SimulationEnvironment;

/// <summary>
/// Control del tanque en el historial, con las mismas reglas que la web: en la época manual un operario corrige a ojo
/// a las 8:30, 13 y 17:30 (fines de semana, a las 10) apuntando a su costumbre; desde que se activó el automático, el
/// motor (<see cref="ControlEngine"/>) en cada lectura. Efecto medido, calibración y pausas como ControlService.
/// </summary>
public sealed partial class HistoryGenerator
{
    private static readonly Dictionary<string, double> FlowPerSecond = new()
    {
        [ControlContract.KindPhUp] = 5,
        [ControlContract.KindPhDown] = 5,
        [ControlContract.KindNutrient] = 25,
        [ControlContract.KindWater] = 2,
    };

    private ControlEngine.Settings EngineSettings => new(
        _control.MixingMinutes, _control.DirectionChangeMinutes, _control.PollStaleMinutes, _control.TrendMinutes);

    private void Control(BenchRun bench, DateTime t, DateTime local)
    {
        if (bench.Mode == ControlContract.ModeAuto)
        {
            if (!bench.IsOffline(t))
                AutoControl(bench, t);
            return;
        }

        var minute = (int)local.TimeOfDay.TotalMinutes;
        var weekend = local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        // Con una alerta crítica abierta en horario de trabajo, el operario corrige en ~30 min (una vez por alerta).
        if (local.Hour is >= 8 and < 19)
        {
            foreach (var (type, episode) in bench.Lot!.OpenEpisodes)
            {
                if (episode.Event.Status == Models.AnomalyContract.StatusAbierta && !episode.Answered
                    && t - episode.Event.CreatedAtUtc >= TimeSpan.FromMinutes(30))
                {
                    episode.Answered = true;
                    ManualCorrection(bench, t, type);
                    return;
                }
            }
        }

        int[] checks = weekend ? [10 * 60] : [8 * 60 + 30, 13 * 60, 17 * 60 + 30];
        foreach (var check in checks)
        {
            if (minute >= check && minute < check + _stepMinutes)
                ManualCorrection(bench, t, ControlContract.ParameterEc);
            else if (minute >= check + 10 && minute < check + 10 + _stepMinutes)
                ManualCorrection(bench, t, ControlContract.ParameterPh);
        }
    }

    // ------------------------------------------------------------------
    // Motor automático
    // ------------------------------------------------------------------

    private void AutoControl(BenchRun bench, DateTime t)
    {
        var run = bench.Lot!;
        var phBand = BandFor(run, ControlContract.ParameterPh);
        var ecBand = BandFor(run, ControlContract.ParameterEc);
        var fresh = 30;
        var ph = SolutionRules.Evaluate(ControlContract.ParameterPh, "pH", bench.Latest(ControlContract.ParameterPh, SolutionRules.LookbackReadings),
            null, ControlEngine.ActionBand(phBand), t, fresh);
        var ec = SolutionRules.Evaluate(ControlContract.ParameterEc, "mS/cm", bench.Latest(ControlContract.ParameterEc, SolutionRules.LookbackReadings),
            null, ControlEngine.ActionBand(ecBand), t, fresh);

        var since = t.AddMinutes(-Math.Max(30, _control.TrendMinutes));
        var input = new ControlEngine.Input(
            t,
            ControlContract.ModeAuto,
            true,
            bench.LastPollUtc,
            ControlContract.DefaultReservoirLiters,
            ControlContract.DefaultSourceWaterEc,
            new ControlEngine.Magnitude(ph, bench.Points(ControlContract.ParameterPh, since, t), bench.OpenPauses.ContainsKey(ControlContract.ParameterPh)),
            new ControlEngine.Magnitude(ec, bench.Points(ControlContract.ParameterEc, since, t), bench.OpenPauses.ContainsKey(ControlContract.ParameterEc)),
            Pumps(bench, t),
            bench.Commands.Any(c => ControlRules.IsInFlight(c.Command.Status)),
            bench.Commands
                .Where(c => c.Command.CompletedAtUtc is { } done && done >= t.AddHours(-24) && WasApplied(c.Command))
                .Select(c => new ControlEngine.AppliedDose(c.Kind, c.Command.CompletedAtUtc!.Value))
                .ToList(),
            EngineSettings);

        var decision = ControlEngine.Decide(input);
        if (decision.Outcome != ControlEngine.Outcome.Dose)
            return;

        var parameter = decision.Parameter!;
        var band = parameter == ControlContract.ParameterPh ? phBand : ecBand;
        Execute(bench, t.AddSeconds(1.5), decision.Pump!.Kind, decision.Amount, ControlContract.SourceAuto, null, band, decision);
    }

    private List<ControlEngine.Pump> Pumps(BenchRun bench, DateTime t) =>
        bench.Pumps.Values
            .Select(p => new ControlEngine.Pump(
                p.Actuator.Id, p.Actuator.Kind, p.Actuator.Name, p.Actuator.Unit, p.Actuator.MaxPerCommand, p.Actuator.MaxPerDay,
                p.Actuator.IsActive, Used24h(bench, p.Actuator.Id, t), p.Effect))
            .ToList();

    private static decimal Used24h(BenchRun bench, int actuatorId, DateTime t) =>
        bench.Commands
            .Where(c => c.Command.ActuatorId == actuatorId && c.Command.CreatedAtUtc >= t.AddHours(-24))
            .Sum(c => ControlRules.UsedAmount(c.Command.Status, c.Command.Amount, c.Command.ExecutedAmount));

    private static bool WasApplied(ActuatorCommand c) => c.Status switch
    {
        ControlContract.StatusExecuted => (c.ExecutedAmount ?? c.Amount) > 0m,
        ControlContract.StatusFailed => c.ExecutedAmount is > 0m,
        ControlContract.StatusUnconfirmed => true,
        _ => false,
    };

    // ------------------------------------------------------------------
    // Operario (época manual) y reposición de nivel
    // ------------------------------------------------------------------

    /// <summary>El operario mira la última lectura y, si se fue de lo que él busca, pide una dosis a ojo con la vista.</summary>
    private void ManualCorrection(BenchRun bench, DateTime t, string parameter)
    {
        var run = bench.Lot!;
        if (bench.Commands.Any(c => ControlRules.IsInFlight(c.Command.Status)))
            return;
        var last = bench.Last(parameter);
        if (last is null)
            return;

        var band = BandFor(run, parameter);
        var isPh = parameter == ControlContract.ParameterPh;
        var aim = (band.Target ?? last.Value) + (decimal)(isPh ? run.PhBias : run.EcBias);
        if (band is { Min: { } min, Max: { } max })
        {
            var margin = (max - min) * 0.25m;
            aim = Math.Clamp(aim, min + margin, max - margin);
        }
        var value = last.Value;
        if (Math.Abs(value - aim) <= (isPh ? 0.15m : 0.12m))
            return;

        var kind = isPh
            ? value > aim ? ControlContract.KindPhDown : ControlContract.KindPhUp
            : value < aim ? ControlContract.KindNutrient : ControlContract.KindWater;
        var pump = bench.Pumps[kind];
        var effect = pump.Effect ?? ControlContract.DefaultFor(kind)?.Effect ?? 1m;
        if (ControlEngine.RawDose(kind, value, aim, ControlContract.DefaultReservoirLiters, effect, ControlContract.DefaultSourceWaterEc) is not { } raw
            || raw <= 0m)
            return;

        var step = ControlEngine.Step(pump.Actuator.Unit);
        var amount = Math.Round(raw * (decimal)(0.75 + _rng.NextDouble() * 0.3) / (step * 5), MidpointRounding.AwayFromZero) * step * 5;
        amount = Math.Min(Math.Max(step, amount), pump.Actuator.MaxPerCommand);
        amount = Math.Min(amount, Math.Floor((pump.Actuator.MaxPerDay - Used24h(bench, pump.Actuator.Id, t)) / step) * step);
        if (amount < step)
            return;

        Execute(bench, t.AddMinutes(2), kind, amount, ControlContract.SourceManual, _w.Operator.Name, band, null);
    }

    /// <summary>A las 9 el operario repone el nivel con agua de red si bajó más del 12 % (y completa a las 9:10).</summary>
    private void TopUp(BenchRun bench, DateTime t)
    {
        var tank = bench.Tank!;
        var local = ToLocal(t);
        var first = local.Minute < 10;
        if ((first && tank.Level >= 0.88) || (!first && (bench.ToppedUpOn != DateOnly.FromDateTime(local) || tank.Level >= 0.97)))
            return;
        if (bench.Commands.Any(c => ControlRules.IsInFlight(c.Command.Status)))
            return;

        var pump = bench.Pumps[ControlContract.KindWater];
        var missing = (decimal)(tank.CapacityLiters - tank.Liters);
        var amount = Math.Min(pump.Actuator.MaxPerCommand, Math.Floor(missing * 2) / 2);
        amount = Math.Min(amount, Math.Floor((pump.Actuator.MaxPerDay - Used24h(bench, pump.Actuator.Id, t)) * 2) / 2);
        if (amount < 1m)
            return;

        bench.ToppedUpOn = DateOnly.FromDateTime(local);
        Execute(bench, t.AddMinutes(1), ControlContract.KindWater, amount, ControlContract.SourceManual, _w.Operator.Name,
            BandFor(bench.Lot!, ControlContract.ParameterEc), null);
    }

    // ------------------------------------------------------------------
    // Comandos
    // ------------------------------------------------------------------

    /// <summary>
    /// Un comando completo como lo dejaría la conversación con la Pi: pedido, entregado al consultar, ejecutado y con su
    /// resultado. Sin conexión vence sin entregarse. Si la bomba está tapada (o el bidón vacío) la Pi la prende igual y
    /// responde ejecutado, pero al tanque no llega nada.
    /// </summary>
    private void Execute(BenchRun bench, DateTime created, string kind, decimal amount, string source, string? requestedBy,
        SolutionBand band, ControlEngine.Decision? decision)
    {
        var run = bench.Lot!;
        var pump = bench.Pumps[kind];
        var parameter = ControlRules.ParameterFor(kind);
        var trigger = bench.Last(parameter);
        var effect = pump.Effect ?? ControlContract.DefaultFor(kind)?.Effect ?? 1m;

        if (run.ArmedIncident is { } armed && armed.Kind == kind && created >= armed.From)
        {
            bench.BrokenPump = kind;
            run.ArmedIncident = null;
            run.Events.Add((created, kind == ControlContract.KindPhDown ? "ph-down-clogged" : "nutrient-empty", string.Empty));
        }

        var command = new ActuatorCommand
        {
            Id = NewGuid(),
            NodeId = bench.Bench.NodeId,
            CircuitId = bench.Bench.CircuitId,
            ActuatorId = pump.Actuator.Id,
            LotId = run.Lot.Id,
            Source = source,
            RequestedBy = requestedBy,
            Amount = amount,
            Unit = pump.Actuator.Unit,
            CreatedAtUtc = created,
            ExpiresAtUtc = created.AddMinutes(Math.Max(1, _control.CommandTtlMinutes)),
            Parameter = parameter,
            TriggerReadingId = trigger?.Id,
            ValueBefore = trigger?.Value ?? decision?.Value,
            BandMin = band.Min,
            Target = band.Target,
            BandMax = band.Max,
            StageId = run.Stage.Id,
            ControllerVersion = decision is null ? ControlContract.ControllerManual : ControlContract.ControllerAuto,
            Strength = decision?.Strength,
            ExpectedEffect = decision?.ExpectedEffect
                ?? (trigger is null ? null : ControlEngine.ExpectedEffect(kind, amount, ControlContract.DefaultReservoirLiters, effect,
                    trigger.Value, ControlContract.DefaultSourceWaterEc)),
        };

        if (bench.IsOffline(created))
        {
            command.Status = ControlContract.StatusExpired;
        }
        else
        {
            var delivered = created.AddSeconds(1 + _rng.NextDouble() * 4);
            var started = delivered.AddMilliseconds(300);
            var roll = _rng.NextDouble();
            var executed = roll < 0.005 ? Math.Round(amount * (decimal)(0.4 + _rng.NextDouble() * 0.4), 1) : amount;
            var finished = started.AddSeconds(Math.Max(0.5, (double)executed / FlowPerSecond[kind]));
            command.DeliveredAtUtc = delivered;
            command.DeviceStartedAtUtc = started;
            command.DeviceFinishedAtUtc = finished;
            command.CompletedAtUtc = finished.AddSeconds(0.5 + _rng.NextDouble());
            command.ExecutedAmount = executed;
            command.Status = executed < amount ? ControlContract.StatusFailed : ControlContract.StatusExecuted;
            command.ResultError = executed < amount ? "caudal bajo: la bomba se cortó antes de terminar" : null;
            if (bench.BrokenPump != kind)
                bench.Tank!.Dose(kind, (double)executed);
        }

        _data.Commands.Add(command);
        bench.Commands.Add(new CommandRun(command, kind, trigger?.ObservedAtUtc));
    }

    private Guid NewGuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        _rng.NextBytes(bytes);
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    // ------------------------------------------------------------------
    // Efecto medido, calibración y pausas (como ControlService)
    // ------------------------------------------------------------------

    private void MeasureEffects(BenchRun bench, DateTime t)
    {
        var mixing = TimeSpan.FromMinutes(_control.MixingMinutes);
        var touched = new HashSet<string>();
        foreach (var run in bench.Commands.Where(c => c.Command.EffectEvaluatedAtUtc is null && c.Command.CompletedAtUtc is not null
                                                     && c.Command.CompletedAtUtc + mixing <= t
                                                     && (c.Command.Status == ControlContract.StatusExecuted
                                                         || (c.Command.Status == ControlContract.StatusFailed && c.Command.ExecutedAmount > 0))))
        {
            var command = run.Command;
            var amount = command.ExecutedAmount ?? command.Amount;
            var settled = command.CompletedAtUtc!.Value + mixing;
            var evaluatedAt = t.AddSeconds(5 + _rng.NextDouble() * 50);
            if (amount <= 0m || command.ValueBefore is not { } before)
            {
                command.EffectEvaluatedAtUtc = evaluatedAt;
                continue;
            }

            var after = bench.FirstAfter(command.Parameter, settled);
            if (after is null)
            {
                if (t > settled.AddMinutes(Math.Max(1, _control.EffectWaitMinutes)))
                    command.EffectEvaluatedAtUtc = evaluatedAt;
                continue;
            }

            var beforeAt = run.TriggerAtUtc ?? command.CreatedAtUtc;
            var drift = ControlEngine.TrendPerHour(bench.Points(command.Parameter, beforeAt.AddMinutes(-_control.TrendMinutes), beforeAt));
            var pump = bench.Pumps[run.Kind];
            var expected = command.ExpectedEffect ?? ControlEngine.ExpectedEffect(run.Kind, amount, ControlContract.DefaultReservoirLiters,
                pump.Effect ?? 1m, before, ControlContract.DefaultSourceWaterEc);
            var measure = ControlEngine.MeasureEffect(before, after.Value, drift, (decimal)(after.ObservedAtUtc - beforeAt).TotalHours, expected);

            command.ExpectedEffect = expected;
            command.ValueAfter = after.Value;
            command.ObservedEffect = measure.Observed;
            command.Effective = measure.Effective;
            command.EffectEvaluatedAtUtc = evaluatedAt;
            touched.Add(command.Parameter);

            if (measure.Effective
                && ControlEngine.Calibrate(run.Kind, pump.Effect, measure.Observed, amount, ControlContract.DefaultReservoirLiters) is { } calibrated
                && calibrated != pump.Effect)
            {
                pump.Effect = calibrated;
                pump.Samples++;
            }
        }

        foreach (var parameter in touched)
            PauseIfNoEffect(bench, parameter, t);

        bench.Commands.RemoveAll(c => c.Command.CreatedAtUtc < t.AddHours(-48) && c.Command.EffectEvaluatedAtUtc is not null);
    }

    private void PauseIfNoEffect(BenchRun bench, string parameter, DateTime t)
    {
        if (bench.OpenPauses.ContainsKey(parameter))
            return;
        var lastResume = bench.LastResume.GetValueOrDefault(parameter);
        var latest = bench.Commands
            .Where(c => c.Command.Parameter == parameter && c.Command.Effective is not null
                        && (lastResume == default || c.Command.CompletedAtUtc > lastResume))
            .OrderByDescending(c => c.Command.CompletedAtUtc)
            .Take(_control.NoEffectDoses)
            .Select(c => c.Command.Effective!.Value)
            .ToList();
        if (!ControlEngine.ShouldPause(latest, _control.NoEffectDoses))
            return;

        var pausedAt = t.AddSeconds(30);
        var pause = new ControlPause
        {
            Id = ++_pauseId,
            CircuitId = bench.Bench.CircuitId,
            Parameter = parameter,
            PausedAtUtc = pausedAt,
            Reason = $"{_control.NoEffectDoses} dosis seguidas sin efecto",
        };
        _data.Pauses.Add(pause);
        bench.OpenPauses[parameter] = pause;

        var alert = AddAlert(NotificationTypes.FallaDosificacion, NotificationSeverities.Alta,
            $"{bench.Bench.CircuitName}: control de {parameter} pausado",
            $"{pause.Reason}. Revisá bidón, bomba y sonda; después reanudá en Control.",
            ControlNotificationSink.Fingerprint(bench.Bench.CircuitId, parameter, pausedAt), bench.Lot?.Lot.Id, pausedAt);
        bench.PauseAlerts[parameter] = alert;
        bench.Lot?.Events.Add((pausedAt, "paused", parameter));
    }

    /// <summary>El operario revisa, arregla y reanuda en Control (se resuelve la alerta).</summary>
    private void Resume(BenchRun bench, string parameter, DateTime t, string fixedWhat)
    {
        if (!bench.OpenPauses.Remove(parameter, out var pause))
            return;
        pause.ResumedAtUtc = t;
        pause.ResumedBy = _w.Operator.Name;
        bench.LastResume[parameter] = t;
        bench.BrokenPump = null;
        if (bench.PauseAlerts.Remove(parameter, out var alert))
        {
            alert.IsResolved = true;
            alert.ResolvedAtUtc = t;
        }
        bench.Lot?.Events.Add((t, "resumed", fixedWhat));
    }

    // ------------------------------------------------------------------
    // Incidentes de hardware
    // ------------------------------------------------------------------

    private void Incidents(BenchRun bench, DateTime t, DateTime local)
    {
        var run = bench.Lot;
        if (run is null)
            return;
        var day = DateOnly.FromDateTime(local).DayNumber - run.Lot.SowingDate.DayNumber;

        switch (run.Plan.Incident)
        {
            case Incident.PhProbeDrift when day >= 6 && bench.PhProbeOffset >= 0:
                // La sonda se corre ~0,025 pH por día hasta que se recalibra (cuando el operario ve plantas amarillas).
                bench.PhProbeOffset += 0.025 * _stepMinutes / (24 * 60);
                if (bench.PhProbeOffset >= 0.38 && local.Hour == 9 && local.Minute < _stepMinutes)
                {
                    bench.PhProbeOffset = -1; // marca: ya se recalibró en este lote
                    bench.Calibrations[bench.Bench.Sensors[AnomalyContract.SensorTypePh].Id] = t;
                    run.Events.Add((t, "probe-recalibrated", string.Empty));
                }
                break;
            case Incident.PhProbeDrift when bench.PhProbeOffset < 0:
                bench.PhProbeOffset = 0;
                break;
        }

        // Bomba tapada o bidón vacío: se reanuda a las 9:10 del día siguiente a la pausa.
        foreach (var (parameter, pause) in bench.OpenPauses.ToList())
        {
            var pausedLocal = ToLocal(pause.PausedAtUtc);
            var resumeAt = pausedLocal.Date.AddDays(pausedLocal.Hour < 6 ? 0 : 1).AddHours(9).AddMinutes(10);
            if (local >= resumeAt)
                Resume(bench, parameter, t, parameter == ControlContract.ParameterPh ? "bomba de Bajar pH destapada" : "bidón de nutrientes repuesto");
        }
    }

    // ------------------------------------------------------------------
    // Receta aprendida y niveles guardados (como RecipeService / RecordSetpointsAsync)
    // ------------------------------------------------------------------

    private SolutionBand BandFor(LotRun run, string parameter) =>
        parameter == ControlContract.ParameterPh
            ? RecipeRules.Shift(SolutionRules.PhBand(_w.Crop), run.Recipe?.PhOffset ?? 0m)
            : RecipeRules.Shift(SolutionRules.EcBand(run.Stage, _w.Crop), run.Recipe?.EcOffset ?? 0m);

    private void RefreshRecipes(DateTime t)
    {
        foreach (var bench in _benches)
        {
            if (bench.Lot is not { Closed: false } run)
                continue;
            var before = (run.Recipe?.PhOffset, run.Recipe?.EcOffset);
            run.Recipe = ComputeRecipe(run, t, run.Recipe);
            if ((run.Recipe.PhOffset, run.Recipe.EcOffset) != before)
                RecordSetpoint(run, t.AddSeconds(30));
        }
    }

    private LotRecipe ComputeRecipe(LotRun run, DateTime t, LotRecipe? previous)
    {
        var past = _closedLots
            .Select(l => (Lot: l, Outcome: Outcome(l), Measured: l.MeasuredOffsets()))
            .Select(x => new RecipeRules.PastLot(
                x.Lot.Lot.Id, x.Lot.Lot.SowingDate, x.Outcome.AverageDailyGdd,
                x.Outcome.AptRate ?? (x.Outcome.TotalPlants > 0 ? Math.Round((decimal)x.Outcome.HarvestedBabyLeaf / x.Outcome.TotalPlants, 4) : null),
                x.Measured.Ph, x.Measured.Ec))
            .ToList();

        var days = Math.Max(1, DateOnly.FromDateTime(t).DayNumber - run.Lot.SowingDate.DayNumber);
        decimal? expectedGdd = run.Lot.AccumulatedGdd is > 0m ? Math.Round(run.Lot.AccumulatedGdd.Value / days, 2) : null;
        var history = RecipeRules.FromHistory(past, run.Lot.SowingDate, expectedGdd);

        var wasAdjusted = previous is not null && (previous.PhOffset != 0m || previous.EcOffset != 0m);
        var since = DateOnly.FromDateTime(t.Date.AddDays(-(RecipeRules.ScoreTrendDays + RecipeRules.MaxScoreGapDays + 1)));
        var trend = RecipeRules.ScoreTrendPerDay(RecipeRules.DailyScoreChanges(
            run.ScoreSamples.Where(s => s.Day >= since).ToList(), _w.Config.GddMin, _w.Config.GddMax));
        var adjusted = history.PhOffset != 0m || history.EcOffset != 0m;
        var trust = RecipeRules.NextTrust(previous?.Trust ?? 1m, wasAdjusted ? trend : null, adjusted);
        var ph = RecipeRules.Apply(history.PhOffset, trust);
        var ec = RecipeRules.Apply(history.EcOffset, trust);

        var row = previous ?? new LotRecipe { LotId = run.Lot.Id };
        var changed = previous is null || row.PhOffset != ph || row.EcOffset != ec;
        row.HistoryPhOffset = history.PhOffset;
        row.HistoryEcOffset = history.EcOffset;
        row.PhOffset = ph;
        row.EcOffset = ec;
        row.Trust = trust;
        row.SimilarLots = history.SimilarLots;
        row.Evidence = history.Evidence;
        row.ScoreTrend = trend;
        row.Reason = RecipeService.Reason(history, trust, trend);
        row.UpdatedAtUtc = t;
        if (changed && (ph != 0m || ec != 0m || previous is not null))
            row.LastChangeAtUtc = t;
        return row;
    }

    private void RecordSetpoint(LotRun run, DateTime t)
    {
        var ph = BandFor(run, ControlContract.ParameterPh);
        var ec = BandFor(run, ControlContract.ParameterEc);
        var phStage = SolutionRules.PhBand(_w.Crop);
        var ecStage = SolutionRules.EcBand(run.Stage, _w.Crop);
        decimal? Offset(SolutionBand stage, SolutionBand shifted) =>
            stage.Target is { } a && shifted.Target is { } b && a != b ? b - a : null;
        var phOffset = Offset(phStage, ph);
        var ecOffset = Offset(ecStage, ec);

        var current = new LotSetpointHistory
        {
            LotId = run.Lot.Id,
            ValidFromUtc = t,
            PhMin = ph.Min,
            PhTarget = ph.Target,
            PhMax = ph.Max,
            EcMin = ec.Min,
            EcTarget = ec.Target,
            EcMax = ec.Max,
            PhOffset = phOffset,
            EcOffset = ecOffset,
            Source = phOffset is null && ecOffset is null ? ControlContract.SetpointSourceStage : ControlContract.SetpointSourceRecipe,
            StageId = run.Stage.Id,
            CreatedAt = t,
        };
        if (!ControlRules.SetpointChanged(run.LastSetpoint, current))
            return;

        current.Id = ++_setpointId;
        _data.Setpoints.Add(current);
        run.LastSetpoint = current;
        run.Periods.Add(new SetpointPeriod(t, phStage.Target, ecStage.Target));
    }
}
