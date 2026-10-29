using System.Collections.Concurrent;
using System.Globalization;

namespace HydroPilot.PiSimulator;

/// <summary>
/// Hace lo mismo que la Raspberry Pi real contra la web: manda lecturas, recibe sus comandos, valida cada uno
/// (vencimiento, bomba, límite propio), prende la bomba y responde el resultado. El transporte puede ser HTTP
/// (la Pi consulta) o Azure IoT Hub (MQTT). Contrato en notas/control/plan-control-ph-ce.md (sección 3).
/// Una sola Pi maneja todos los circuitos del invernadero (densos y de Adultas): cada uno tiene su tanque, sus sondas y
/// sus bombas, con las claves que dice la web (<see cref="CircuitLayout"/>); temperatura y humedad son del invernadero
/// (una sonda de cada una). Cada minuto vuelve a pedir los circuitos: las plantas de cada tanque siguen a los
/// trasplantes, los cortes y los lotes nuevos.
/// </summary>
public sealed class VirtualPi
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-AR");

    /// <summary>Bombas de la Pi: unidad, caudal y límite propio por comando (el doble que la web: doble control).</summary>
    private static readonly Dictionary<string, Pump> Pumps = new()
    {
        ["ph-up"] = new("ml", 5, 100),
        ["ph-down"] = new("ml", 5, 100),
        ["nutrient"] = new("ml", 25, 1_000),   // Solución A
        ["nutrient-b"] = new("ml", 25, 400),   // Solución B
        ["water"] = new("L", 2, 100),
    };

    private sealed record Pump(string Unit, double PerSecond, double LocalMax);

    /// <summary>Lectura del batch (contrato v2).</summary>
    private sealed record Reading(string ReadingId, string SensorRef, double Value, string Unit, string ObservedAt);

    private readonly IPiTransport _transport;
    private readonly Settings _settings;
    private readonly IReadOnlyList<Tank> _tanks;
    private readonly CircuitLayout[] _circuits;
    private readonly string _temperatureKey;
    private readonly string _humidityKey;
    private readonly bool _refresh;
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;
    private readonly object _consoleGate = new();

    /// <summary>Resultado de cada commandId ya procesado: la Pi nunca ejecuta dos veces el mismo comando.</summary>
    private readonly ConcurrentDictionary<Guid, CommandResult> _results = new();

    /// <summary>Resultados que todavía no se pudieron informar (sin conexión): se reintentan.</summary>
    private readonly ConcurrentDictionary<Guid, bool> _unreported = new();

    /// <summary>Circuito al que van las teclas p, b, c, n y r (0 = el primero).</summary>
    private int _selected;

    private volatile bool _manualOffline;
    private bool _wasOffline;
    private int _sequence;

    /// <summary>Aceleraciones que recorre la tecla a.</summary>
    private static readonly double[] Accelerations = [1, 10, 60, 240];

    public VirtualPi(IPiTransport transport, Settings settings, IReadOnlyList<CircuitLayout> circuits, IReadOnlyList<Tank> tanks,
        string? temperatureKey = null, string? humidityKey = null, bool refresh = false)
    {
        _transport = transport;
        _settings = settings;
        _circuits = circuits.ToArray();
        _tanks = tanks;
        _temperatureKey = temperatureKey ?? "temp-ambiente";
        _humidityKey = humidityKey ?? "hum-ambiente";
        _refresh = refresh;
        for (var i = 0; i < tanks.Count; i++)
        {
            var label = Label(i);
            tanks[i].Refilled += liters => Print(ConsoleColor.DarkCyan, $"· {label}: el flotante repuso {liters.ToString("0", Es)} L de agua de red");
        }
    }

    private string Label(int index) => _circuits[index].Name;

    /// <summary>Sin conexión: tecla x, o el escenario sin-conexion (del minuto 1 al 11).</summary>
    private bool Offline
    {
        get
        {
            if (_manualOffline)
                return true;
            if (_settings.Scenario != Scenario.NoConnection)
                return false;
            var elapsed = DateTime.UtcNow - _startedAtUtc;
            return elapsed >= TimeSpan.FromMinutes(1) && elapsed < TimeSpan.FromMinutes(11);
        }
    }

    public Task RunAsync(CancellationToken ct) =>
        Task.WhenAll(TankLoopAsync(ct), ReadingsLoopAsync(ct), CommandsLoopAsync(ct), KeysLoopAsync(ct), RefreshLoopAsync(ct));

    // ------------------------------------------------------------------
    // Circuitos (cada minuto, lo que dice la web)
    // ------------------------------------------------------------------

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        if (!_refresh)
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await Tick(timer, ct))
        {
            if (Offline)
                continue;
            DeviceConfig? config;
            try
            {
                config = await _transport.GetConfigAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                continue;
            }
            if (config is null)
                continue;

            foreach (var circuit in CircuitLayout.FromWeb(config, _settings))
            {
                var index = Array.FindIndex(_circuits, c => c.PhKey == circuit.PhKey);
                if (index < 0)
                    continue; // un circuito nuevo: hay que reiniciar la Pi virtual
                var previous = _circuits[index];
                if (previous.Plants == circuit.Plants && Math.Abs(previous.Days - circuit.Days) < 1)
                    continue;
                // Lote nuevo en el circuito: estaba vacío o tiene menos días que antes.
                var newLot = (previous.Plants == 0 && circuit.Plants > 0) || circuit.Days < previous.Days - 1;
                _tanks[index].Sync(circuit.Plants, circuit.Days, newLot);
                _circuits[index] = circuit;
                Print(ConsoleColor.DarkCyan, $"· {circuit.Name}: {circuit.Plants} plantas" + (newLot ? $", lote de {circuit.Days:0} días" : string.Empty));
            }
        }
    }

    // ------------------------------------------------------------------
    // Tanque y conexión
    // ------------------------------------------------------------------

    private async Task TankLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await Tick(timer, ct))
        {
            foreach (var tank in _tanks)
                tank.Step(1);

            var offline = Offline;
            if (offline != _wasOffline)
            {
                _wasOffline = offline;
                Print(ConsoleColor.Yellow, offline ? "✕ sin conexión" : "✓ conexión restablecida");
            }
        }
    }

    private async Task KeysLoopAsync(CancellationToken ct)
    {
        if (Console.IsInputRedirected)
            return;

        while (!ct.IsCancellationRequested)
        {
            while (Console.KeyAvailable)
            {
                var key = char.ToLowerInvariant(Console.ReadKey(intercept: true).KeyChar);
                var tank = _tanks[_selected];
                var label = Label(_selected);
                switch (key)
                {
                    case >= '1' and <= '9' when key - '1' < _tanks.Count:
                        _selected = key - '1';
                        Print(ConsoleColor.Magenta, $"· teclas para el {Label(_selected)}");
                        break;
                    case 'p':
                        tank.Nudge(0.5, 0);
                        Print(ConsoleColor.Magenta, $"· {label}: pH +0,5");
                        break;
                    case 'b':
                        tank.Nudge(-0.5, 0);
                        Print(ConsoleColor.Magenta, $"· {label}: pH −0,5");
                        break;
                    case 'c':
                        tank.Nudge(0, -0.3);
                        Print(ConsoleColor.Magenta, $"· {label}: CE −0,3");
                        break;
                    case 'n':
                        tank.Nudge(0, 0.3);
                        Print(ConsoleColor.Magenta, $"· {label}: CE +0,3 (sobredosis de nutrientes)");
                        break;
                    case 'r':
                        var liters = tank.Refill();
                        Print(ConsoleColor.Magenta, $"· {label}: se repusieron {liters.ToString("0", Es)} L de agua de red");
                        break;
                    case 'a':
                        var next = Accelerations.FirstOrDefault(a => a > _tanks[0].Acceleration);
                        foreach (var each in _tanks)
                            each.Acceleration = next > 0 ? next : Accelerations[0];
                        Print(ConsoleColor.Magenta, $"· tiempo de las plantas ×{_tanks[0].Acceleration:0}");
                        break;
                    case 'x':
                        _manualOffline = !_manualOffline;
                        break;
                    case 'q':
                        Environment.Exit(0);
                        break;
                }
            }

            try
            {
                await Task.Delay(100, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // ------------------------------------------------------------------
    // Lecturas
    // ------------------------------------------------------------------

    private async Task ReadingsLoopAsync(CancellationToken ct)
    {
        await SendReadingsAsync(ct);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_settings.ReadingSeconds));
        while (await Tick(timer, ct))
            await SendReadingsAsync(ct);
    }

    private async Task SendReadingsAsync(CancellationToken ct)
    {
        if (Offline)
            return;

        var now = DateTime.UtcNow;
        var stamp = now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var observed = now.ToString("o", CultureInfo.InvariantCulture);

        // pH y CE de cada circuito; temperatura y humedad, una sola vez (son del invernadero).
        var readings = new List<Reading>();
        var solution = new List<string>();
        var tanks = new List<string>();
        for (var i = 0; i < _tanks.Count; i++)
        {
            var (ph, ec) = _tanks[i].Read();
            var circuit = _circuits[i];
            readings.Add(new Reading($"pv-{circuit.PhKey}-{stamp}", circuit.PhKey, ph, "pH", observed));
            readings.Add(new Reading($"pv-{circuit.EcKey}-{stamp}", circuit.EcKey, ec, "mS/cm", observed));
            solution.Add($"C{i + 1} pH {ph.ToString("0.00", Es)} CE {ec.ToString("0.00", Es)}");
            var tankStatus = _tanks[i].Status();
            tanks.Add($"C{i + 1} {tankStatus.Liters.ToString("0", Es)} L día {tankStatus.Day.ToString("0.0", Es)}");
        }
        var (temperature, humidity) = _tanks[0].Climate();
        readings.Add(new Reading($"pv-t-{stamp}", _temperatureKey, temperature, "°C", observed));
        readings.Add(new Reading($"pv-h-{stamp}", _humidityKey, humidity, "%", observed));

        var batch = new
        {
            batchId = $"pv-{_settings.Node}-{stamp}",
            nodeId = _settings.Node,
            schemaVersion = "2",
            sequence = Interlocked.Increment(ref _sequence),
            sentAt = observed,
            firmwareVersion = "pi-virtual-1.0",
            readings,
        };

        try
        {
            var inserted = await _transport.SendReadingsAsync(batch, ct);
            var status = _tanks[0].Status();
            Print(ConsoleColor.Gray,
                $"→ lecturas   {string.Join(" · ", solution)} · {temperature.ToString("0.0", Es)} °C {humidity:0} %" +
                $"   {(inserted is { } n ? $"(aceptadas {n})" : "(enviadas)")}   " +
                $"· {string.Join(" · ", tanks)} · {status.LocalTime:HH:mm} ×{_tanks[0].Acceleration:0}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Print(ConsoleColor.Red, $"→ lecturas: no se pudieron mandar ({ex.Message})");
        }
    }

    // ------------------------------------------------------------------
    // Comandos
    // ------------------------------------------------------------------

    private async Task CommandsLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_settings.PollSeconds));
        while (await Tick(timer, ct))
        {
            if (Offline)
                continue;

            await ReportPendingAsync(ct);

            IReadOnlyList<ReceivedCommand> received;
            try
            {
                received = await _transport.ReceiveCommandsAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Print(ConsoleColor.Red, $"← comandos: no se pudieron recibir ({ex.Message})");
                continue;
            }

            foreach (var item in received)
            {
                var command = item.Command;
                if (!_results.ContainsKey(command.CommandId))
                    _results[command.CommandId] = await ExecuteAsync(command, ct);
                // Ya ejecutado o recién ejecutado: se confirma la recepción y se informa (otra vez, si hacía falta).
                _unreported[command.CommandId] = true;
                await AcknowledgeAsync(item, ct);
            }

            await ReportPendingAsync(ct);
        }
    }

    private async Task AcknowledgeAsync(ReceivedCommand item, CancellationToken ct)
    {
        try
        {
            await _transport.AcknowledgeAsync(item, accepted: true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Print(ConsoleColor.Red, $"← comando {item.Command.CommandId}: no se pudo confirmar ({ex.Message})");
        }
    }

    private async Task<CommandResult> ExecuteAsync(DeviceCommand command, CancellationToken ct)
    {
        var amount = (double)command.Amount;
        Print(ConsoleColor.Cyan,
            $"← comando    {command.Actuator} {Amount(amount, command.Unit)}  (vence {command.ExpiresAt.ToLocalTime():HH:mm:ss})");

        var startedAt = DateTimeOffset.UtcNow;
        if (command.ExpiresAt <= startedAt)
            return Rejected(startedAt, "vencido");
        if (Target(command.Actuator) is not { } target)
            return Rejected(startedAt, "actuador desconocido");
        var (index, kind) = target;
        var pump = Pumps[kind];
        if (!string.Equals(pump.Unit, command.Unit, StringComparison.OrdinalIgnoreCase))
            return Rejected(startedAt, $"unidad inesperada ({command.Unit})");
        if (amount <= 0 || amount > pump.LocalMax)
            return Rejected(startedAt, "supera el límite local");

        // Bomba prendida el tiempo que lleva pasar la cantidad (como mucho 6 s, para la demo).
        var seconds = Math.Clamp(amount / pump.PerSecond, 0.5, 6);
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);

        var broken = _settings.Scenario == Scenario.BrokenPhPump && index == 0 && kind is "ph-up" or "ph-down";
        if (!broken)
            _tanks[index].Dose(kind, amount);

        return new CommandResult("EXECUTED", command.Amount, startedAt, DateTimeOffset.UtcNow, null, command.Actuator, command.Unit);
    }

    /// <summary>Circuito y tipo de una bomba por su clave ("ph-up-2" es la de Subir pH del circuito que la tiene).</summary>
    private (int Index, string Kind)? Target(string actuator)
    {
        for (var i = 0; i < _circuits.Length; i++)
        {
            foreach (var (kind, key) in _circuits[i].Pumps)
            {
                if (key == actuator && Pumps.ContainsKey(kind))
                    return (i, kind);
            }
        }
        return null;
    }

    private static CommandResult Rejected(DateTimeOffset at, string reason) =>
        new("REJECTED", 0, at, at, reason, null, null);

    private async Task ReportPendingAsync(CancellationToken ct)
    {
        foreach (var commandId in _unreported.Keys)
        {
            if (!_results.TryGetValue(commandId, out var result))
                continue;

            (ReportOutcome Outcome, string? Detail) report;
            try
            {
                report = await _transport.ReportResultAsync(commandId, result, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return; // sin conexión: se reintenta en la próxima vuelta
            }

            switch (report.Outcome)
            {
                case ReportOutcome.Accepted:
                    _unreported.TryRemove(commandId, out _);
                    var detail = result.Status == "EXECUTED"
                        ? $"{result.Actuator} {Amount((double)result.ExecutedAmount, result.Unit ?? string.Empty)}"
                        : result.Error;
                    Print(result.Status == "EXECUTED" ? ConsoleColor.Green : ConsoleColor.Yellow, $"→ resultado  {result.Status} {detail}");
                    break;
                case ReportOutcome.Drop:
                    _unreported.TryRemove(commandId, out _);
                    Print(ConsoleColor.Red, $"→ resultado  {report.Detail}");
                    break;
                default:
                    return;
            }
        }
    }

    // ------------------------------------------------------------------
    // Auxiliares
    // ------------------------------------------------------------------

    private static async Task<bool> Tick(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static string Amount(double value, string unit) => $"{value.ToString("0.#", Es)} {unit}";

    private void Print(ConsoleColor color, string text)
    {
        lock (_consoleGate)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"{DateTime.Now:HH:mm:ss} ");
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ResetColor();
        }
    }
}
