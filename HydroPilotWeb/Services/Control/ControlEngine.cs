using HydroPilotWeb.Models.Control;
using HydroPilotWeb.Services.Anomalies;

namespace HydroPilotWeb.Services.Control;

/// <summary>
/// Motor del control automático de pH y CE (fase 2, niveles fijos de la etapa). Mira el tanque de un nodo y
/// decide: nada, esperar, o una dosis de una bomba. Actúa al salir de su banda de acción (<see cref="ActionBand"/>,
/// adentro de la banda del lote) para corregir antes de que la solución salga de rango. Corrige primero la CE y
/// después el pH (salvo pH crítico), una corrección por vez, apuntando al objetivo; espera la mezcla y dos lecturas
/// nuevas antes de volver a dosificar. El tamaño de la dosis sale del volumen del tanque y del efecto por ml de la bomba, y se aplica
/// solo una parte según una lógica difusa (cuánto se desvió y si se está acercando o alejando). La CE se sube con la Solución A
/// y la B por separado (no se mezclan concentradas), una dosis por vez, en la proporción de la etapa
/// (<see cref="ChooseNutrient"/>). Lógica pura, testeable sin I/O (notas/ux/logica-vista-control.md, sección 7).
/// </summary>
public static class ControlEngine
{
    /// <summary>Una dosis "tuvo efecto" si movió al menos esta parte de lo esperado, en la dirección esperada.</summary>
    public const decimal MinEffectRatio = 0.2m;

    /// <summary>Peso del efecto medido al recalibrar (promedio móvil).</summary>
    public const decimal CalibrationWeight = 0.3m;

    /// <summary>La tendencia necesita lecturas que cubran al menos este lapso.</summary>
    public const int TrendMinSpanMinutes = 5;

    /// <summary>Parte del camino del objetivo a cada borde de la banda en la que el motor ya actúa.</summary>
    public const decimal ActionBandShare = 0.7m;

    /// <summary>Parte de Solución A si no se indica: la de la ventana Baby Leaf (2,75 : 1,1 ml/L).</summary>
    public static readonly decimal DefaultNutrientShareA =
        HydroPilotWeb.Services.Simulation.NutrientConsumptionRules.SolutionAShare(adult: false);

    public static class Codes
    {
        public const string Manual = "manual";
        public const string NoLot = "no-lot";
        public const string PiSilent = "pi-silent";
        public const string SensorFault = "sensor-fault";
        public const string InFlight = "in-flight";
        public const string Mixing = "mixing";
        public const string AwaitReading = "await-reading";
        public const string Unconfirmed = "unconfirmed";
        public const string NoData = "no-data";
        public const string Paused = "paused";
        public const string InBand = "in-band";
        public const string PumpOff = "pump-off";
        public const string Direction = "direction";
        public const string DailyLimit = "daily-limit";
        public const string WaterTarget = "water-target";
        public const string Approaching = "approaching";
        public const string Dose = "dose";
    }

    public enum Outcome { Off, InBand, Waiting, Blocked, Paused, Dose }

    public sealed record Settings(int MixingMinutes, int DirectionChangeMinutes, int PollStaleMinutes, int TrendMinutes);

    /// <summary>
    /// Bomba del nodo. Effect: por ml y litro (null = la estimación de fábrica). RecentlyRejected: la Pi rechazó su último
    /// comando (p. ej. todavía no tiene esa bomba): para A y B, se usa la otra.
    /// </summary>
    public sealed record Pump(
        int Id,
        string Kind,
        string Name,
        string Unit,
        decimal MaxPerCommand,
        decimal MaxPerDay,
        bool IsActive,
        decimal UsedLast24h,
        decimal? Effect,
        bool RecentlyRejected = false);

    public sealed record Point(DateTime AtUtc, decimal Value);

    /// <summary>Estado de pH o CE (SolutionRules), sus lecturas recientes y si su control está pausado.</summary>
    public sealed record Magnitude(SolutionParameterStatus Status, IReadOnlyList<Point> Recent, bool Paused);

    /// <summary>Dosis que pudo llegar al tanque (ejecutada, fallida a medias o sin confirmar).</summary>
    public sealed record AppliedDose(string Kind, DateTime AppliedAtUtc);

    /// <summary>Error de sensor de pH o CE que frena el control de ese parámetro (Kind: SensorFaultKinds).</summary>
    public sealed record SensorIssue(string Parameter, string Kind);

    public sealed record Input(
        DateTime NowUtc,
        string Mode,
        bool HasLot,
        DateTime? LastPollUtc,
        decimal ReservoirLiters,
        decimal SourceWaterEc,
        Magnitude Ph,
        Magnitude Ec,
        IReadOnlyList<Pump> Pumps,
        bool HasInFlight,
        IReadOnlyList<AppliedDose> RecentDoses,
        Settings Settings,
        IReadOnlyList<SensorIssue>? Sensors = null,
        decimal? NutrientShareA = null);

    public sealed record Decision(
        Outcome Outcome,
        string Code,
        string? Parameter = null,
        Pump? Pump = null,
        decimal Amount = 0m,
        decimal RawAmount = 0m,
        decimal Strength = 0m,
        decimal? Value = null,
        decimal? Target = null,
        decimal? ExpectedEffect = null,
        DateTime? UntilUtc = null,
        string? SensorFaultKind = null);

    // ------------------------------------------------------------------
    // Decisión
    // ------------------------------------------------------------------

    public static Decision Decide(Input input)
    {
        if (input.Mode != ControlContract.ModeAuto)
            return new(Outcome.Off, Codes.Manual);
        if (!input.HasLot)
            return new(Outcome.Blocked, Codes.NoLot);
        // Si la Pi no está pidiendo comandos, cualquier dosis vencería sin ejecutarse.
        if (input.LastPollUtc is not { } poll || input.NowUtc - poll > TimeSpan.FromMinutes(input.Settings.PollStaleMinutes))
            return new(Outcome.Blocked, Codes.PiSilent);
        if (input.HasInFlight)
            return new(Outcome.Waiting, Codes.InFlight);

        // Sensor de pH o CE sin datos, fuera de lo normal o con un salto: no se corrige ese parámetro con lecturas
        // dudosas; el otro sigue con su control.
        var issues = input.Sensors ?? [];
        SensorIssue? IssueOf(string parameter) => issues.FirstOrDefault(i => i.Parameter == parameter);

        DateTime? settledAt = null;
        var last = input.RecentDoses.MaxBy(d => d.AppliedAtUtc);
        if (last is not null)
        {
            var until = last.AppliedAtUtc.AddMinutes(input.Settings.MixingMinutes);
            if (input.NowUtc < until)
                return new(Outcome.Waiting, Codes.Mixing, UntilUtc: until);
            // Las lecturas del parámetro frenado no se esperan (con el sensor sin datos no llegarían nunca).
            if ((IssueOf(ControlContract.ParameterPh) is null && PostMixReadings(input.Ph, until) < 2)
                || (IssueOf(ControlContract.ParameterEc) is null && PostMixReadings(input.Ec, until) < 2))
                return new(Outcome.Waiting, Codes.AwaitReading);
            settledAt = until;
        }

        // Primero CE (los nutrientes mueven el pH), salvo que el pH esté fuera del rango crítico. Si la primera no se
        // puede corregir ahora (límite de 24 h, bomba apagada, el agua no alcanza, cambio de sentido) o vuelve sola,
        // se corrige la otra: un bloqueo de la CE no deja al pH sin control todo el día.
        var order = IsCritical(input.Ph)
            ? new[] { (ControlContract.ParameterPh, input.Ph), (ControlContract.ParameterEc, input.Ec) }
            : new[] { (ControlContract.ParameterEc, input.Ec), (ControlContract.ParameterPh, input.Ph) };
        Decision? held = null;
        foreach (var (parameter, magnitude) in order)
        {
            if (magnitude.Paused || IssueOf(parameter) is not null || !IsOutOfBand(magnitude))
                continue;
            var decision = DoseFor(input, parameter, magnitude, settledAt);
            if (decision.Outcome == Outcome.Dose)
                return decision;
            held ??= decision;
        }
        if (held is not null)
            return held;

        foreach (var (parameter, _) in order)
        {
            if (IssueOf(parameter) is { } issue)
                return new(Outcome.Blocked, Codes.SensorFault, parameter, SensorFaultKind: issue.Kind);
        }
        foreach (var (parameter, magnitude) in order)
        {
            if (magnitude.Paused && IsOutOfBand(magnitude))
                return new(Outcome.Paused, Codes.Paused, parameter, Value: magnitude.Status.Value, Target: magnitude.Status.Band.Target);
        }
        if (input.Ph.Status.State == SolutionState.Unconfirmed || input.Ec.Status.State == SolutionState.Unconfirmed)
            return new(Outcome.Waiting, Codes.Unconfirmed);
        if (!IsEvaluable(input.Ph) || !IsEvaluable(input.Ec))
            return new(Outcome.Blocked, Codes.NoData);
        return new(Outcome.InBand, Codes.InBand);
    }

    private static Decision DoseFor(Input input, string parameter, Magnitude magnitude, DateTime? settledAt)
    {
        var band = magnitude.Status.Band;
        var value = magnitude.Status.Value!.Value;
        var target = band.Target!.Value;
        var raise = magnitude.Status.State == SolutionState.Raise;
        var kind = parameter == ControlContract.ParameterPh
            ? (raise ? ControlContract.KindPhUp : ControlContract.KindPhDown)
            : (raise ? ControlContract.KindNutrientA : ControlContract.KindWater);

        // Subir la CE: la Solución A o la B, la que quedó atrás de su parte (sin ninguna activa, la A da "bomba apagada").
        var nutrient = kind == ControlContract.KindNutrientA
            ? ChooseNutrient(input.Pumps, input.NutrientShareA ?? DefaultNutrientShareA)
            : null;
        var pump = nutrient?.Pump ?? input.Pumps.FirstOrDefault(p => p.Kind == kind);
        if (pump is null || !pump.IsActive)
            return new(Outcome.Blocked, Codes.PumpOff, parameter, pump, Value: value, Target: target);
        kind = pump.Kind;

        // Sin vaivén: no se dosifica en sentido contrario a una dosis reciente.
        var opposite = input.RecentDoses.Where(d => Opposes(d.Kind, kind)).MaxBy(d => d.AppliedAtUtc);
        if (opposite is not null && input.NowUtc < opposite.AppliedAtUtc.AddMinutes(input.Settings.DirectionChangeMinutes))
            return new(Outcome.Blocked, Codes.Direction, parameter, pump, Value: value, Target: target,
                UntilUtc: opposite.AppliedAtUtc.AddMinutes(input.Settings.DirectionChangeMinutes));

        // Con nutrientes, la dosis completa es de la mezcla A + B (con el efecto de la mezcla); a esta bomba le toca su parte.
        var effect = EffectOf(pump);
        if (RawDose(kind, value, target, input.ReservoirLiters, nutrient?.MixEffect ?? effect, input.SourceWaterEc) is not { } raw)
            return new(Outcome.Blocked, Codes.WaterTarget, parameter, pump, Value: value, Target: target);

        var step = Step(pump.Unit);
        var remaining = pump.MaxPerDay - pump.UsedLast24h;
        if (remaining < step)
            return new(Outcome.Blocked, Codes.DailyLimit, parameter, pump, Value: value, Target: target);

        var since = input.NowUtc.AddMinutes(-input.Settings.TrendMinutes);
        if (settledAt is { } settled && settled > since)
            since = settled;
        var trend = TrendPerHour(magnitude.Recent.Where(p => p.AtUtc >= since).ToList());
        var strength = Strength(DeviationRatio(value, band), TrendRatio(value, target, band, trend));
        if (strength <= 0m)
            return new(Outcome.Waiting, Codes.Approaching, parameter, pump, Value: value, Target: target);

        var planned = nutrient is null ? raw * strength : nutrient.Part(raw * strength);
        var full = nutrient is null ? raw : nutrient.Part(raw);
        var amount = Math.Max(step, Math.Round(planned / step, MidpointRounding.AwayFromZero) * step);
        amount = Math.Min(amount, pump.MaxPerCommand);
        amount = Math.Min(amount, Math.Floor(remaining / step) * step);

        return new(Outcome.Dose, Codes.Dose, parameter, pump, amount, Math.Round(full, 2), strength, value, target,
            ExpectedEffect(kind, amount, input.ReservoirLiters, effect, value, input.SourceWaterEc));
    }

    private static int PostMixReadings(Magnitude magnitude, DateTime settledAt) =>
        magnitude.Recent.Count(p => p.AtUtc >= settledAt);

    private static bool IsOutOfBand(Magnitude magnitude) =>
        magnitude.Status.State is SolutionState.Raise or SolutionState.Lower;

    private static bool IsEvaluable(Magnitude magnitude) =>
        magnitude.Status.State is SolutionState.InRange or SolutionState.Raise or SolutionState.Lower;

    private static bool IsCritical(Magnitude ph) =>
        IsOutOfBand(ph) && ph.Status.Value is { } value
                        && (value < ControlContract.CriticalPhMin || value > ControlContract.CriticalPhMax);

    /// <summary>
    /// Banda en la que actúa el motor: el <see cref="ActionBandShare"/> del camino del objetivo a cada borde de la
    /// banda del lote (pH 5,5–6,5 con objetivo 6,0 → 5,65–6,35). Así corrige antes de que la solución salga de rango,
    /// y lo que sale de la banda del lote (Anomalías) queda para cuando el control no alcanza.
    /// </summary>
    public static SolutionBand ActionBand(SolutionBand band) =>
        band is { Min: { } min, Max: { } max, Target: { } target } && max > min && target >= min && target <= max
            ? new SolutionBand(
                Math.Round(target - (target - min) * ActionBandShare, 3),
                Math.Round(target + (max - target) * ActionBandShare, 3),
                target)
            : band;

    // ------------------------------------------------------------------
    // Dosis
    // ------------------------------------------------------------------

    /// <summary>Una dosis de <paramref name="doseKind"/> deshace lo que hace <paramref name="kind"/>: misma magnitud, sentido contrario.</summary>
    public static bool Opposes(string doseKind, string kind) =>
        ControlRules.ParameterFor(doseKind) == ControlRules.ParameterFor(kind) && Raises(doseKind) != Raises(kind);

    /// <summary>Sube su magnitud: pH Up y las soluciones A y B (el agua y pH Down bajan).</summary>
    public static bool Raises(string kind) =>
        kind is ControlContract.KindPhUp or ControlContract.KindNutrientA or ControlContract.KindNutrientB;

    /// <summary>
    /// Bomba de nutrientes que toca para subir la CE. ShareA: parte de la Solución A (1 si solo está la A, 0 si solo la B);
    /// UsedA / UsedB: lo dosificado en 24 h; MixEffect: efecto por ml/L de la mezcla en esa proporción.
    /// </summary>
    public sealed record NutrientChoice(Pump Pump, decimal ShareA, decimal UsedA, decimal UsedB, decimal MixEffect)
    {
        /// <summary>
        /// Cuánto le toca a la bomba elegida para que, con <paramref name="mixAmount"/> ml más de mezcla, lo de 24 h quede en
        /// la proporción: su parte de (A + B + mezcla) menos lo que ya puso. Con una sola bomba, toda la mezcla.
        /// </summary>
        public decimal Part(decimal mixAmount)
        {
            if (ShareA is 0m or 1m)
                return mixAmount;
            var isA = Pump.Kind == ControlContract.KindNutrientA;
            var share = isA ? ShareA : 1m - ShareA;
            return Math.Max(0m, share * (UsedA + UsedB + mixAmount) - (isA ? UsedA : UsedB));
        }
    }

    /// <summary>
    /// A o B: la que quedó más atrás de su parte en lo dosificado en 24 h (a igualdad, la A). Una desactivada, o cuya última
    /// dosis rechazó la Pi (p. ej. porque todavía no tiene esa bomba), no se usa y la otra va sola; si la Pi rechazó las dos,
    /// se reintenta igual. Null si ninguna está activa.
    /// </summary>
    public static NutrientChoice? ChooseNutrient(IReadOnlyList<Pump> pumps, decimal shareA)
    {
        var a = pumps.FirstOrDefault(p => p.Kind == ControlContract.KindNutrientA);
        var b = pumps.FirstOrDefault(p => p.Kind == ControlContract.KindNutrientB);
        var useA = a is { IsActive: true, RecentlyRejected: false };
        var useB = b is { IsActive: true, RecentlyRejected: false };
        if (!useA && !useB)
        {
            useA = a is { IsActive: true };
            useB = b is { IsActive: true };
        }
        if (!useA && !useB)
            return null;

        var share = useA && useB ? Math.Clamp(shareA, 0m, 1m) : useA ? 1m : 0m;
        var usedA = a?.UsedLast24h ?? 0m;
        var usedB = b?.UsedLast24h ?? 0m;
        var total = usedA + usedB;
        var pickB = share == 0m || (share < 1m && (1m - share) * total - usedB > share * total - usedA);
        var mix = (useA ? share * EffectOf(a!) : 0m) + (useB ? (1m - share) * EffectOf(b!) : 0m);
        return new NutrientChoice(pickB ? b! : a!, share, usedA, usedB, mix);
    }

    public static decimal EffectOf(Pump pump) =>
        pump.Effect is > 0m ? pump.Effect.Value : ControlContract.DefaultFor(pump.Kind)?.Effect ?? 1m;

    /// <summary>Paso mínimo de una dosis: 1 ml o medio litro.</summary>
    public static decimal Step(string unit) => unit == ControlContract.UnitLiters ? 0.5m : 1m;

    /// <summary>
    /// Dosis completa para llevar el valor al objetivo (sin la parte difusa):
    /// pH Up/Down y soluciones A y B = desvío ÷ efecto por ml/L × litros; agua = litros × (CE − obj.) ÷ (obj. − CE del agua).
    /// Null si el agua no puede bajar la CE hasta el objetivo (objetivo ≤ CE del agua de red).
    /// </summary>
    public static decimal? RawDose(string kind, decimal value, decimal target, decimal liters, decimal effect, decimal sourceWaterEc)
    {
        var raw = kind switch
        {
            ControlContract.KindPhDown => (value - target) * liters / effect,
            ControlContract.KindPhUp or ControlContract.KindNutrientA or ControlContract.KindNutrientB => (target - value) * liters / effect,
            _ when target > sourceWaterEc => liters * (value - target) / (target - sourceWaterEc),
            _ => (decimal?)null,
        };
        return raw is { } r ? Math.Max(0m, r) : null;
    }

    /// <summary>Cuánto se espera que se mueva el valor con esta cantidad (con signo).</summary>
    public static decimal ExpectedEffect(string kind, decimal amount, decimal liters, decimal effect, decimal value, decimal sourceWaterEc)
    {
        var expected = kind switch
        {
            ControlContract.KindPhDown => -(amount / liters) * effect,
            ControlContract.KindPhUp or ControlContract.KindNutrientA or ControlContract.KindNutrientB => amount / liters * effect,
            _ => (sourceWaterEc - value) * amount / (liters + amount),
        };
        return Math.Round(expected, 4);
    }

    // ------------------------------------------------------------------
    // Lógica difusa: qué parte de la dosis aplicar
    // ------------------------------------------------------------------

    /// <summary>Distancia al objetivo en "bandas": 1 = justo en el borde, 2 = el doble de lejos.</summary>
    public static decimal DeviationRatio(decimal value, SolutionBand band)
    {
        var target = band.Target ?? value;
        var edge = value > target ? band.Max : band.Min;
        var width = edge is { } e ? Math.Abs(e - target) : 0m;
        return width == 0m ? 3m : Math.Abs(value - target) / width;
    }

    /// <summary>Velocidad hacia el objetivo, en medias bandas por hora (+ se acerca, − se aleja). Sin tendencia, 0.</summary>
    public static decimal TrendRatio(decimal value, decimal target, SolutionBand band, decimal? slopePerHour)
    {
        if (slopePerHour is not { } slope)
            return 0m;
        var half = band.Min is { } min && band.Max is { } max && max > min ? (max - min) / 2m : 1m;
        var toward = value > target ? -slope : slope;
        return toward / half;
    }

    /// <summary>
    /// Fuerza de la dosis (0–0,9). Desvío: Leve / Medio / Grande; tendencia: Alejándose / Estable / Acercándose.
    /// Reglas (salida Nula 0 · Suave 0,3 · Media 0,6 · Fuerte 0,9), mínimo como "y" y promedio ponderado:
    /// Leve: Media / Suave / Nula · Medio: Fuerte / Media / Suave · Grande: Fuerte / Fuerte / Media.
    /// </summary>
    public static decimal Strength(decimal deviationRatio, decimal trendRatio)
    {
        var mild = Falling(deviationRatio, 1.25m, 1.75m);
        var medium = Triangle(deviationRatio, 1.25m, 1.75m, 2.5m);
        var large = Rising(deviationRatio, 1.75m, 2.5m);

        var away = Falling(trendRatio, -0.5m, 0m);
        var steady = Triangle(trendRatio, -0.5m, 0m, 0.5m);
        var closer = Rising(trendRatio, 0m, 0.5m);

        const decimal none = 0m, soft = 0.3m, mid = 0.6m, strong = 0.9m;
        var rules = new (decimal Weight, decimal Output)[]
        {
            (Math.Min(mild, away), mid), (Math.Min(mild, steady), soft), (Math.Min(mild, closer), none),
            (Math.Min(medium, away), strong), (Math.Min(medium, steady), mid), (Math.Min(medium, closer), soft),
            (Math.Min(large, away), strong), (Math.Min(large, steady), strong), (Math.Min(large, closer), mid),
        };

        var weight = rules.Sum(r => r.Weight);
        return weight == 0m ? 0m : Math.Round(rules.Sum(r => r.Weight * r.Output) / weight, 2);
    }

    private static decimal Falling(decimal x, decimal a, decimal b) => x <= a ? 1m : x >= b ? 0m : (b - x) / (b - a);

    private static decimal Rising(decimal x, decimal a, decimal b) => x <= a ? 0m : x >= b ? 1m : (x - a) / (b - a);

    private static decimal Triangle(decimal x, decimal a, decimal peak, decimal b) =>
        x <= a || x >= b ? 0m : x <= peak ? (x - a) / (peak - a) : (b - x) / (b - peak);

    /// <summary>
    /// Pendiente (valor por hora) por mínimos cuadrados. Null con menos de 3 lecturas o si cubren menos de
    /// <see cref="TrendMinSpanMinutes"/> minutos (con pocas lecturas el ruido de la sonda domina).
    /// </summary>
    public static decimal? TrendPerHour(IReadOnlyList<Point> points)
    {
        if (points.Count < 3)
            return null;
        var first = points.Min(p => p.AtUtc);
        var last = points.Max(p => p.AtUtc);
        if (last - first < TimeSpan.FromMinutes(TrendMinSpanMinutes))
            return null;

        var xs = points.Select(p => (p.AtUtc - first).TotalHours).ToList();
        var ys = points.Select(p => (double)p.Value).ToList();
        var meanX = xs.Average();
        var meanY = ys.Average();
        var sxx = xs.Sum(x => (x - meanX) * (x - meanX));
        if (sxx == 0)
            return null;
        var sxy = xs.Zip(ys, (x, y) => (x - meanX) * (y - meanY)).Sum();
        return Math.Round((decimal)(sxy / sxx), 4);
    }

    // ------------------------------------------------------------------
    // Efecto medido y autocalibración
    // ------------------------------------------------------------------

    public sealed record EffectMeasure(decimal Observed, bool Effective);

    /// <summary>
    /// Efecto de una dosis: (después − antes) menos la deriva que traía la solución en ese lapso. Tuvo efecto si
    /// se movió en la dirección esperada al menos <see cref="MinEffectRatio"/> de lo esperado.
    /// </summary>
    public static EffectMeasure MeasureEffect(decimal before, decimal after, decimal? driftPerHour, decimal hours, decimal expected)
    {
        var observed = Math.Round(after - before - (driftPerHour ?? 0m) * hours, 4);
        var effective = expected != 0m
                        && Math.Sign(observed) == Math.Sign(expected)
                        && Math.Abs(observed) >= MinEffectRatio * Math.Abs(expected);
        return new(observed, effective);
    }

    /// <summary>
    /// Nuevo efecto por ml/L de una bomba después de una dosis con efecto: promedio móvil entre el actual y el
    /// medido. Se ignoran mediciones fuera de 1/4–4 veces la estimación de fábrica (lecturas raras). El agua no se
    /// calibra: su efecto es la dilución.
    /// </summary>
    public static decimal? Calibrate(string kind, decimal? current, decimal observed, decimal amount, decimal liters)
    {
        if (ControlContract.DefaultFor(kind)?.Effect is not { } factory || amount <= 0m || liters <= 0m)
            return current;

        var measured = Math.Abs(observed) / (amount / liters);
        if (measured < factory / 4m || measured > factory * 4m)
            return current;

        var basis = current is > 0m ? current.Value : factory;
        return Math.Round(basis * (1m - CalibrationWeight) + measured * CalibrationWeight, 4);
    }

    /// <summary>Se pausa la magnitud si las últimas <paramref name="doses"/> dosis medidas (más nueva primero) no tuvieron efecto.</summary>
    public static bool ShouldPause(IReadOnlyList<bool> latestEffective, int doses) =>
        doses > 0 && latestEffective.Count >= doses && latestEffective.Take(doses).All(effective => !effective);
}
