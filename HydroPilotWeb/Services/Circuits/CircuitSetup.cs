using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Control;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Services.Circuits;

/// <summary>
/// Alta de la infraestructura de un circuito: el circuito, sus sensores de pH y CE y sus 4 bombas. Las claves técnicas
/// son únicas en la Pi, que maneja todos los circuitos: el primero usa las claves base ("ph-solucion", "ph-up") y los
/// siguientes llevan sufijo ("ph-solucion-2", "ph-up-2"). También asegura los sensores de temperatura y humedad del
/// invernadero (uno de cada uno, sin circuito). La usan el arranque, el entorno de simulación y Ajustes &gt; Circuitos.
/// </summary>
public static class CircuitSetup
{
    public const string PhKey = "ph-solucion";
    public const string EcKey = "ec-solucion";
    public const string TemperatureKey = "temp-ambiente";
    public const string HumidityKey = "hum-ambiente";

    /// <summary>Sondas de cada circuito: tipo, clave base, unidad y modelo.</summary>
    private static readonly (string Type, string BaseKey, string Unit, string Model)[] Probes =
    [
        (AnomalyContract.SensorTypePh, PhKey, "pH", "PH-4502C"),
        (AnomalyContract.SensorTypeCe, EcKey, "mS/cm", "TDS-EC-Meter"),
    ];

    /// <summary>Clave técnica del n-ésimo circuito de la Pi: la base para el primero, con sufijo para los demás.</summary>
    public static string Key(string baseKey, int position) => position <= 1 ? baseKey : $"{baseKey}-{position}";

    /// <summary>La Pi del invernadero (una sola, maneja todos sus circuitos); null si no tiene.</summary>
    public static Task<IotNode?> PiOfAsync(HydroPilotDbContext context, int greenhouseId, CancellationToken ct) =>
        context.IotNodes.AsNoTracking().Where(n => n.GreenhouseId == greenhouseId).OrderBy(n => n.Id).FirstOrDefaultAsync(ct);

    /// <summary>"Circuito N" con el primer número libre del invernadero.</summary>
    public static async Task<string> NextNameAsync(HydroPilotDbContext context, int greenhouseId, CancellationToken ct)
    {
        var names = await context.Circuits.Where(c => c.GreenhouseId == greenhouseId).Select(c => c.Name).ToListAsync(ct);
        var n = 1;
        while (names.Contains($"Circuito {n}"))
            n++;
        return $"Circuito {n}";
    }

    /// <summary>Crea el circuito con sus sensores de pH y CE y sus 4 bombas (guarda los cambios).</summary>
    public static async Task<Circuit> CreateAsync(
        HydroPilotDbContext context, int greenhouseId, int nodeId, string name, decimal reservoirLiters, CancellationToken ct)
    {
        // Posición en la Pi para las claves: la siguiente cuyas claves estén libres.
        var position = await context.Circuits.CountAsync(c => c.NodeId == nodeId, ct) + 1;
        while (await KeysTakenAsync(context, nodeId, position, ct))
            position++;

        var circuit = new Circuit
        {
            GreenhouseId = greenhouseId,
            NodeId = nodeId,
            Name = name,
            ControlMode = ControlContract.ModeManual,
            ReservoirLiters = reservoirLiters,
            SourceWaterEc = ControlContract.DefaultSourceWaterEc,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        context.Circuits.Add(circuit);
        await context.SaveChangesAsync(ct);

        var types = await context.SensorTypes.ToDictionaryAsync(t => t.Name, ct);
        var units = await context.MeasurementUnits.ToListAsync(ct);
        foreach (var probe in Probes)
            await AddProbeAsync(context, nodeId, circuit.Id, probe, Key(probe.BaseKey, position), types, units, ct);

        foreach (var pump in ControlContract.DefaultActuators)
        {
            context.Actuators.Add(new Actuator
            {
                NodeId = nodeId,
                CircuitId = circuit.Id,
                Kind = pump.Kind,
                TechnicalKey = Key(pump.Kind, position),
                Name = pump.Name,
                Unit = pump.Unit,
                MaxPerCommand = pump.MaxPerCommand,
                MaxPerDay = pump.MaxPerDay,
                EffectPerUnit = pump.Effect,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await context.SaveChangesAsync(ct);
        return circuit;
    }

    /// <summary>
    /// Asegura un sensor activo de temperatura y uno de humedad del invernadero (sin circuito), leídos por la Pi
    /// indicada. Si ya hay, no hace nada.
    /// </summary>
    public static async Task EnsureAmbientSensorsAsync(HydroPilotDbContext context, int greenhouseId, int nodeId, CancellationToken ct)
    {
        var types = await context.SensorTypes.ToDictionaryAsync(t => t.Name, ct);
        var units = await context.MeasurementUnits.ToListAsync(ct);
        foreach (var (type, key, unit) in new[]
                 {
                     (AnomalyContract.SensorTypeTemperatura, TemperatureKey, "°C"),
                     (AnomalyContract.SensorTypeHumedad, HumidityKey, "%"),
                 })
        {
            if (await context.Sensors.AnyAsync(s => s.CircuitId == null && s.IsActive && s.SensorType!.Name == type
                                                    && s.Node!.GreenhouseId == greenhouseId, ct))
                continue;

            var existing = await context.Sensors.FirstOrDefaultAsync(s => s.NodeId == nodeId && s.TechnicalKey == key, ct);
            if (existing is not null)
            {
                existing.IsActive = true;
                existing.CircuitId = null;
                continue;
            }
            context.Sensors.Add(new Sensor
            {
                NodeId = nodeId,
                SensorTypeId = types[type].Id,
                MeasurementUnitId = units.FirstOrDefault(u => u.Symbol == unit)?.Id,
                Name = await UniqueSensorNameAsync(context, key, ct),
                TechnicalKey = key,
                Model = "DHT22",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Sondas de pH y CE para los circuitos que no tienen ningún sensor (el que trajo UnifyGreenhousePi de una Pi que
    /// nunca tuvo sensores). Llevan el sufijo de las claves de sus bombas ("ph-up-2" → "ph-solucion-2"). Un circuito
    /// que ya tiene sensores, activos o no, no se toca.
    /// </summary>
    public static async Task EnsureProbesAsync(HydroPilotDbContext context, CancellationToken ct)
    {
        var bare = await context.Circuits
            .Where(c => !c.Sensors.Any())
            .Select(c => new { c.Id, c.NodeId, Pumps = c.Actuators.Select(a => new { a.Kind, a.TechnicalKey }).ToList() })
            .ToListAsync(ct);
        if (bare.Count == 0)
            return;

        var types = await context.SensorTypes.ToDictionaryAsync(t => t.Name, ct);
        var units = await context.MeasurementUnits.ToListAsync(ct);
        foreach (var circuit in bare)
        {
            var suffix = circuit.Pumps
                .Where(p => p.TechnicalKey.StartsWith(p.Kind, StringComparison.Ordinal))
                .Select(p => p.TechnicalKey[p.Kind.Length..])
                .FirstOrDefault() ?? $"-c{circuit.Id}";
            foreach (var probe in Probes)
            {
                var key = probe.BaseKey + suffix;
                if (await context.Sensors.AnyAsync(s => s.NodeId == circuit.NodeId && s.TechnicalKey == key, ct))
                    key = $"{probe.BaseKey}-c{circuit.Id}";
                await AddProbeAsync(context, circuit.NodeId, circuit.Id, probe, key, types, units, ct);
            }
        }
        await context.SaveChangesAsync(ct);
    }

    private static async Task AddProbeAsync(
        HydroPilotDbContext context, int nodeId, int circuitId, (string Type, string BaseKey, string Unit, string Model) probe,
        string key, Dictionary<string, SensorType> types, List<MeasurementUnit> units, CancellationToken ct)
    {
        context.Sensors.Add(new Sensor
        {
            NodeId = nodeId,
            CircuitId = circuitId,
            SensorTypeId = types[probe.Type].Id,
            MeasurementUnitId = units.FirstOrDefault(u => u.Symbol == probe.Unit)?.Id,
            Name = await UniqueSensorNameAsync(context, key, ct),
            TechnicalKey = key,
            Model = probe.Model,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        });
    }

    private static async Task<bool> KeysTakenAsync(HydroPilotDbContext context, int nodeId, int position, CancellationToken ct)
    {
        var sensorKeys = new[] { Key(PhKey, position), Key(EcKey, position) };
        var pumpKeys = ControlContract.DefaultActuators.Select(a => Key(a.Kind, position)).ToList();
        return await context.Sensors.AnyAsync(s => s.NodeId == nodeId && sensorKeys.Contains(s.TechnicalKey), ct)
               || await context.Actuators.AnyAsync(a => a.NodeId == nodeId && pumpKeys.Contains(a.TechnicalKey), ct);
    }

    /// <summary>El nombre del sensor es único en todo el sistema: si la clave ya se usa como nombre, se le agrega un número.</summary>
    private static async Task<string> UniqueSensorNameAsync(HydroPilotDbContext context, string baseName, CancellationToken ct)
    {
        var name = baseName;
        var i = 2;
        while (await context.Sensors.AnyAsync(s => s.Name == name, ct) || context.Sensors.Local.Any(s => s.Name == name))
            name = $"{baseName}-{i++}";
        return name;
    }
}
