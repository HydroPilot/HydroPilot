namespace HydroPilot.PiSimulator;

/// <summary>
/// Un circuito de la Pi: sus claves (sondas de pH y CE, y bombas por tipo), el tanque y las plantas del lote que tiene
/// (con los días desde la siembra). Sale de la web (<see cref="DeviceConfig"/>) o, sin ella, del esquema de claves base
/// y "-n".
/// </summary>
public sealed record CircuitLayout(
    string Name,
    string Kind,
    double Liters,
    string PhKey,
    string EcKey,
    IReadOnlyDictionary<string, string> Pumps,
    int Plants,
    double Days)
{
    public static readonly string[] PumpKinds = ["ph-up", "ph-down", "nutrient", "nutrient-b", "water"];

    /// <summary>Sin la web (IoT Hub o --circuitos): n circuitos densos con las claves base y "-n".</summary>
    public static IReadOnlyList<CircuitLayout> Local(Settings settings) =>
        Enumerable.Range(0, settings.Circuits ?? 2)
            .Select(i =>
            {
                var suffix = i == 0 ? string.Empty : $"-{i + 1}";
                return new CircuitLayout($"Circuito {i + 1}", "DENSO", settings.Liters, "ph-solucion" + suffix, "ec-solucion" + suffix,
                    PumpKinds.ToDictionary(k => k, k => k + suffix), settings.Plants, settings.DaysFor(i));
            })
            .ToList();

    /// <summary>Los circuitos que dice la web (los que tienen sondas de pH y CE).</summary>
    public static IReadOnlyList<CircuitLayout> FromWeb(DeviceConfig config, Settings settings) =>
        config.Circuits
            .Where(c => c.PhKey is not null && c.EcKey is not null)
            .Select(c => new CircuitLayout(c.Name, c.Kind, (double)c.Liters, c.PhKey!, c.EcKey!, c.Pumps, c.Plants,
                c.LotDays ?? settings.DaysFor(0)))
            .ToList();
}

/// <summary>Configuración que da la web: GET api/nodos/{nodo}/circuitos.</summary>
public sealed record DeviceConfig(string NodeId, string? TemperatureKey, string? HumidityKey, List<DeviceCircuit> Circuits);

public sealed record DeviceCircuit(
    string Name,
    string Kind,
    decimal Liters,
    string? PhKey,
    string? EcKey,
    Dictionary<string, string> Pumps,
    int Plants,
    int? LotDays);
