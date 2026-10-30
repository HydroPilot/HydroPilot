using System.Text;
using HydroPilot.PiSimulator;

Console.OutputEncoding = Encoding.UTF8;

Settings settings;
try
{
    settings = Settings.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(Settings.Help);
    return 2;
}

if (settings.ShowHelp)
{
    Console.WriteLine(Settings.Help);
    return 0;
}

HttpClient? http = null;
IPiTransport transport;
if (settings.Transport == "iothub")
{
    transport = new IotHubPiTransport(settings.DeviceConnectionString!);
}
else
{
    if (string.IsNullOrWhiteSpace(settings.ApiKey))
    {
        Console.Error.WriteLine("Falta la API key: pasá --api-key, definí HYDROPILOT_API_KEY o cargá TelemetryApiKey en los user-secrets de HydroPilotWeb.");
        return 2;
    }

    var handler = new HttpClientHandler();
    if (settings.Insecure)
        handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
    http = new HttpClient(handler)
    {
        BaseAddress = new Uri(settings.Url + "/"),
        Timeout = TimeSpan.FromSeconds(15),
    };
    http.DefaultRequestHeaders.Add("X-Api-Key", settings.ApiKey);
    transport = new HttpPiTransport(http, settings.Node);
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var target = settings.Transport == "iothub" ? "Azure IoT Hub" : settings.Url;

// Los circuitos: los que dice la web (claves, plantas y días de cada uno) o, con --circuitos (o sin la web), los locales.
DeviceConfig? config = null;
if (settings.Circuits is null)
{
    try
    {
        config = await transport.GetConfigAsync(cts.Token);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
    {
        Console.Error.WriteLine($"No se pudieron pedir los circuitos a la web ({ex.Message}): se usan 2 con las claves base.");
    }
}
var layouts = config is { Circuits.Count: > 0 } ? CircuitLayout.FromWeb(config, settings) : CircuitLayout.Local(settings);

var scenario = layouts.Count > 1 && settings.Scenario is not (Scenario.Normal or Scenario.Heat or Scenario.NoConnection)
    ? $"{settings.Scenario} ({layouts[0].Name})"
    : settings.Scenario.ToString();
Console.WriteLine($"Pi virtual {settings.Node} → {target} ({transport.Name}) · {layouts.Count} circuito{(layouts.Count > 1 ? "s" : "")}" +
                  $"{(config is null ? " (locales)" : " (de la web)")} · escenario {scenario} · tiempo ×{settings.Acceleration:0}");
for (var i = 0; i < layouts.Count; i++)
    Console.WriteLine($"  C{i + 1} {layouts[i].Name}: {layouts[i].Plants} plantas, día {layouts[i].Days:0} · {layouts[i].Liters:0} L · {layouts[i].PhKey}");
Console.WriteLine((layouts.Count > 1 ? $"Teclas: 1–{Math.Min(9, layouts.Count)} = circuito · " : "Teclas: ") +
                  "p = pH +0,5 · b = pH −0,5 · c = CE −0,3 · n = CE +0,3 · r = reponer agua · a = aceleración · x = conexión · q = salir");

// Un tanque (mesada) por circuito; el clima es el mismo para todos.
var tanks = layouts
    .Select((l, i) => new Tank(l.Liters, settings.Ph, settings.Ec, settings.ScenarioFor(i), l.Plants, l.Days, settings.Acceleration))
    .ToList();
try
{
    await new VirtualPi(transport, settings, layouts, tanks, config?.TemperatureKey, config?.HumidityKey, refresh: config is not null)
        .RunAsync(cts.Token);
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
}
finally
{
    await transport.DisposeAsync();
    http?.Dispose();
}
return 0;
