using HydroPilotWeb.Models.Control;

namespace HydroPilotWeb.Services.Control;

/// <summary>
/// Reglas del ciclo de vida de un comando de dosis: validar una dosis manual, vencer los que nadie tomó,
/// aplicar el resultado que informa la Pi y armar la conversación de la vista Control. Lógica pura,
/// testeable sin I/O (notas/ux/logica-vista-control.md).
/// </summary>
public static class ControlRules
{
    /// <summary>Tolerancia sobre la cantidad pedida al informar lo ejecutado (redondeo de la bomba).</summary>
    public const decimal ExecutedTolerance = 1.05m;

    /// <summary>Magnitud que corrige cada actuador: nutrientes y agua mueven la CE; pH Up y pH Down, el pH.</summary>
    public static string ParameterFor(string kind) =>
        kind is ControlContract.KindNutrient or ControlContract.KindWater ? ControlContract.ParameterEc : ControlContract.ParameterPh;

    /// <summary>Pendiente o entregado: la bomba puede estar por actuar.</summary>
    public static bool IsInFlight(string status) =>
        status is ControlContract.StatusPending or ControlContract.StatusDelivered;

    /// <summary>
    /// Cantidad que cuenta para el máximo de 24 h: lo ejecutado (o lo que falló a mitad), lo que está en curso y lo
    /// que quedó sin confirmar (pudo haberse aplicado). Lo vencido, rechazado o cancelado no se aplicó.
    /// </summary>
    public static decimal UsedAmount(string status, decimal amount, decimal? executedAmount) => status switch
    {
        ControlContract.StatusExecuted => executedAmount ?? amount,
        ControlContract.StatusFailed => executedAmount ?? 0m,
        ControlContract.StatusPending or ControlContract.StatusDelivered or ControlContract.StatusUnconfirmed => amount,
        _ => 0m,
    };

    /// <summary>Motivo por el que no se puede pedir la dosis, o null si se puede.</summary>
    public static string? ValidateManualDose(
        string mode,
        bool actuatorActive,
        bool hasInFlight,
        decimal amount,
        decimal maxPerCommand,
        decimal maxPerDay,
        decimal usedLast24h,
        string unit)
    {
        if (mode == ControlContract.ModeOff)
            return "El control del nodo está apagado.";
        if (!actuatorActive)
            return "La bomba está desactivada.";
        if (hasInFlight)
            return "Ya hay una dosis en curso para esta bomba.";
        if (amount <= 0m)
            return "Ingresá una cantidad mayor a 0.";
        if (amount > maxPerCommand)
            return $"Máximo {Number(maxPerCommand)} {unit} por dosis.";
        if (usedLast24h + amount > maxPerDay)
            return $"Supera el máximo de {Number(maxPerDay)} {unit} en 24 h (quedan {Number(Math.Max(0m, maxPerDay - usedLast24h))} {unit}).";
        return null;
    }

    /// <summary>
    /// True si el nivel vigente del lote es distinto de la última fila guardada (o no hay ninguna): cambió la
    /// etapa aplicada o algún mínimo, objetivo o máximo de pH o CE.
    /// </summary>
    public static bool SetpointChanged(LotSetpointHistory? previous, LotSetpointHistory current) =>
        previous is null
        || previous.StageId != current.StageId
        || previous.PhMin != current.PhMin || previous.PhTarget != current.PhTarget || previous.PhMax != current.PhMax
        || previous.EcMin != current.EcMin || previous.EcTarget != current.EcTarget || previous.EcMax != current.EcMax;

    // ------------------------------------------------------------------
    // Resultado informado por la Pi
    // ------------------------------------------------------------------

    public sealed record DeviceResult(string? Status, decimal? ExecutedAmount, DateTime? StartedAtUtc, DateTime? FinishedAtUtc, string? Error);

    public enum ResultOutcome { Applied, Duplicate, Conflict, Invalid }

    public sealed record ResultCheck(ResultOutcome Outcome, string? Status, decimal? ExecutedAmount, string? Message);

    /// <summary>
    /// Valida el resultado contra el comando. Un mismo resultado repetido es Duplicate (la Pi reintenta); uno
    /// distinto sobre un comando ya cerrado por la Pi es Conflict. Un resultado sobre un comando vencido o sin
    /// confirmar se acepta: lo que dice la Pi es lo que pasó en el tanque.
    /// </summary>
    public static ResultCheck CheckResult(
        string commandStatus,
        decimal commandAmount,
        decimal? commandExecutedAmount,
        DeviceResult result)
    {
        var status = result.Status?.Trim().ToUpperInvariant();
        if (status is not (ControlContract.StatusExecuted or ControlContract.StatusRejected or ControlContract.StatusFailed))
            return new(ResultOutcome.Invalid, null, null, "status tiene que ser EXECUTED, REJECTED o FAILED.");

        if (result.ExecutedAmount is < 0m)
            return new(ResultOutcome.Invalid, null, null, "executedAmount no puede ser negativo.");
        if (result.ExecutedAmount > commandAmount * ExecutedTolerance)
            return new(ResultOutcome.Invalid, null, null, "executedAmount supera la cantidad pedida.");
        if (status == ControlContract.StatusRejected && result.ExecutedAmount is > 0m)
            return new(ResultOutcome.Invalid, null, null, "Un comando rechazado no aplica cantidad.");

        var executed = status switch
        {
            ControlContract.StatusExecuted => result.ExecutedAmount ?? commandAmount,
            ControlContract.StatusFailed => result.ExecutedAmount ?? 0m,
            _ => 0m,
        };

        if (commandStatus is ControlContract.StatusExecuted or ControlContract.StatusRejected or ControlContract.StatusFailed)
        {
            return commandStatus == status && commandExecutedAmount == executed
                ? new(ResultOutcome.Duplicate, status, executed, null)
                : new(ResultOutcome.Conflict, null, null, $"El comando ya tiene resultado {commandStatus}.");
        }

        if (commandStatus == ControlContract.StatusCancelled)
            return new(ResultOutcome.Conflict, null, null, "El comando fue cancelado antes de entregarse.");

        return new(ResultOutcome.Applied, status, executed, null);
    }

    /// <summary>Aplica un resultado ya validado (<see cref="CheckResult"/> = Applied).</summary>
    public static void ApplyResult(ActuatorCommand command, ResultCheck check, DeviceResult result, DateTime nowUtc)
    {
        command.Status = check.Status!;
        command.ExecutedAmount = check.ExecutedAmount;
        command.ResultError = string.IsNullOrWhiteSpace(result.Error) ? null : Truncate(result.Error.Trim(), 300);
        command.DeviceStartedAtUtc = result.StartedAtUtc;
        command.DeviceFinishedAtUtc = result.FinishedAtUtc;
        command.DeliveredAtUtc ??= nowUtc;
        command.CompletedAtUtc = nowUtc;
    }

    // ------------------------------------------------------------------
    // Conversación (vista Control)
    // ------------------------------------------------------------------

    /// <summary>Lectura de pH o CE del nodo.</summary>
    public sealed record ReadingPoint(DateTime ObservedAtUtc, string Parameter, decimal Value);

    /// <summary>
    /// Arma la conversación más reciente primero: cada comando aporta su pedido, su entrega, su cierre y el
    /// efecto medido después de la mezcla; las pausas, su inicio y su reanudación. Las lecturas seguidas entre dos
    /// mensajes se juntan en una sola fila (cantidad y últimos valores).
    /// </summary>
    public static IReadOnlyList<ControlTimelineEntry> BuildTimeline(
        IEnumerable<ReadingPoint> readings,
        IEnumerable<ControlCommandDto> commands,
        int maxEntries,
        IEnumerable<ControlPauseDto>? pauses = null)
    {
        var events = new List<ControlTimelineEntry>();

        foreach (var snapshot in readings.GroupBy(r => r.ObservedAtUtc))
        {
            var ph = snapshot.FirstOrDefault(r => r.Parameter == ControlContract.ParameterPh)?.Value;
            var ec = snapshot.FirstOrDefault(r => r.Parameter == ControlContract.ParameterEc)?.Value;
            events.Add(new ControlTimelineEntry(snapshot.Key, ControlTimelineKind.Readings,
                new TimelineReadings(snapshot.Key, snapshot.Key, 1, ph, ec), null));
        }

        foreach (var command in commands)
        {
            events.Add(new ControlTimelineEntry(command.CreatedAtUtc, ControlTimelineKind.Requested, null, command));
            if (command.DeliveredAtUtc is { } delivered)
                events.Add(new ControlTimelineEntry(delivered, ControlTimelineKind.Delivered, null, command));
            if (command.CompletedAtUtc is { } completed && ClosingKind(command.Status) is { } closing)
                events.Add(new ControlTimelineEntry(completed, closing, null, command));
            if (command.EffectEvaluatedAtUtc is { } evaluated && command.Effective is not null)
                events.Add(new ControlTimelineEntry(evaluated, ControlTimelineKind.Effect, null, command));
        }

        foreach (var pause in pauses ?? [])
        {
            events.Add(new ControlTimelineEntry(pause.PausedAtUtc, ControlTimelineKind.Paused, null, null, pause));
            if (pause.ResumedAtUtc is { } resumed)
                events.Add(new ControlTimelineEntry(resumed, ControlTimelineKind.Resumed, null, null, pause));
        }

        // Más reciente primero; a igual hora, el cierre antes que la entrega y la entrega antes que el pedido.
        var ordered = events
            .OrderByDescending(e => e.AtUtc)
            .ThenByDescending(e => (int)e.Kind)
            .ToList();

        var result = new List<ControlTimelineEntry>();
        foreach (var entry in ordered)
        {
            if (entry.Kind == ControlTimelineKind.Readings
                && result.Count > 0
                && result[^1] is { Kind: ControlTimelineKind.Readings, Readings: { } run })
            {
                var current = entry.Readings!;
                result[^1] = result[^1] with
                {
                    Readings = run with
                    {
                        FirstUtc = current.FirstUtc,
                        Count = run.Count + 1,
                        Ph = run.Ph ?? current.Ph,
                        Ec = run.Ec ?? current.Ec,
                    },
                };
                continue;
            }

            if (result.Count == maxEntries)
                break;
            result.Add(entry);
        }

        return result;
    }

    private static ControlTimelineKind? ClosingKind(string status) => status switch
    {
        ControlContract.StatusExecuted or ControlContract.StatusRejected or ControlContract.StatusFailed => ControlTimelineKind.Result,
        ControlContract.StatusExpired => ControlTimelineKind.Expired,
        ControlContract.StatusUnconfirmed => ControlTimelineKind.Unconfirmed,
        ControlContract.StatusCancelled => ControlTimelineKind.Cancelled,
        _ => null,
    };

    private static string Number(decimal value) => value.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("es-AR"));

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
