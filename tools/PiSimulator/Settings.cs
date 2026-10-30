using System.Globalization;
using System.Text.Json;

namespace HydroPilot.PiSimulator;

public enum Scenario { Normal, PhRising, EcFalling, Heat, BrokenPhPump, NoConnection, StuckSensor }

/// <summary>Opciones de línea de comandos (todas opcionales).</summary>
public sealed class Settings
{
    /// <summary>UserSecretsId de HydroPilotWeb: de ahí se toma TelemetryApiKey si no se pasa --api-key.</summary>
    private const string WebUserSecretsId = "d65d854d-5928-41e8-a8bc-2cf96866d64c";

    public string Url { get; private set; } = "https://localhost:7059";
    public string Node { get; private set; } = "rpi-inv-01";
    public string? ApiKey { get; private set; }
    public Scenario Scenario { get; private set; } = Scenario.Normal;
    public int ReadingSeconds { get; private set; } = 15;
    public int PollSeconds { get; private set; } = 5;
    public double Liters { get; private set; } = 500;
    public double Ph { get; private set; } = 6.0;
    public double Ec { get; private set; } = 1.5;
    public bool Insecure { get; private set; }

    /// <summary>Cuántas veces más rápido corre el tiempo de las plantas y del clima (1 = tiempo real).</summary>
    public double Acceleration { get; private set; } = 1;

    /// <summary>
    /// Circuitos de la Pi (uno por mesada): cada uno con su tanque, sus sondas y sus 5 bombas. Null = los que dice la web
    /// (GET api/nodos/{nodo}/circuitos); con un número, n circuitos con las claves base y "-n" (sin preguntar).
    /// </summary>
    public int? Circuits { get; private set; }

    /// <summary>
    /// Días desde la siembra del lote de cada circuito (12 y 4: los lotes en curso de las mesadas A y B del entorno de
    /// simulación). Si hay más circuitos que valores, los demás usan el último.
    /// </summary>
    public IReadOnlyList<double> Days { get; private set; } = [12, 4];

    public double DaysFor(int circuitIndex) => Days[Math.Min(circuitIndex, Days.Count - 1)];

    /// <summary>
    /// Escenario de cada circuito: la ola de calor es del invernadero (todos); los demás, del circuito 1 (sin-conexion
    /// corta la Pi entera y lo maneja <see cref="VirtualPi"/>).
    /// </summary>
    public Scenario ScenarioFor(int circuitIndex) =>
        circuitIndex == 0 || Scenario == Scenario.Heat ? Scenario : Scenario.Normal;

    public int Plants { get; private set; } = 200;

    /// <summary>http (consulta, fase 1) o iothub (Azure IoT Hub por MQTT, fase 3).</summary>
    public string Transport { get; private set; } = "http";

    /// <summary>Cadena de conexión del dispositivo en IoT Hub (HostName=…;DeviceId=…;SharedAccessKey=…).</summary>
    public string? DeviceConnectionString { get; private set; }
    public bool ShowHelp { get; private set; }

    public const string Help = """
        Pi virtual de HydroPilot: tanque simulado que manda lecturas y ejecuta las dosis de la web.

          --url <url>            web de HydroPilot (https://localhost:7059)
          --nodo <id>            nodo (rpi-inv-01): la Pi del invernadero
          --circuitos <n>        circuitos que maneja la Pi; sin esta opción los pide a la web (claves, plantas y días de
                                 cada uno, y los actualiza cada minuto); con n, usa las claves base y -n (ph-solucion-2, ph-up-2)
          --api-key <clave>      X-Api-Key; si falta, HYDROPILOT_API_KEY o TelemetryApiKey de los user-secrets
          --escenario <nombre>   normal | calor | ph-sube | ce-baja | bomba-rota | sin-conexion | sensor-pegado
                                 (calor y sin-conexion afectan a todos; los demás, al circuito 1)
          --acelerar <n>         el tiempo de las plantas y del clima corre n veces más rápido (1; 60 = una hora por minuto)
          --dias <n/n>           con --circuitos: días desde la siembra del lote de cada uno, separados por / (12/4)
          --plantas <n>          con --circuitos: plantas de cada mesada (200)
          --lecturas <s>         cada cuántos segundos manda lecturas (15)
          --consulta <s>         cada cuántos segundos pide comandos (5)
          --litros <L>           volumen de cada tanque (500)
          --ph <valor>           pH inicial (6,0)
          --ce <valor>           CE inicial en mS/cm (1,5)
          --inseguro             no valida el certificado HTTPS (solo localhost)
          --transporte <nombre>  http (default) | iothub
          --conexion <cadena>    cadena del dispositivo en IoT Hub (o HYDROPILOT_DEVICE_CONNECTION);
                                 el nodo es su DeviceId

        Teclas: 1–9 = elegir el circuito de las teclas siguientes (1)
                p = pH +0,5 · b = pH −0,5 · c = CE −0,3 · n = CE +0,3 · r = reponer agua · a = cambiar aceleración
                x = cortar/reanudar conexión · q = salir
        """;

    public static Settings Parse(string[] args)
    {
        var settings = new Settings();
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i].TrimStart('-').ToLowerInvariant();
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Falta el valor de --{key}.");

            switch (key)
            {
                case "url": settings.Url = Next().TrimEnd('/'); break;
                case "nodo": settings.Node = Next(); break;
                case "api-key": settings.ApiKey = Next(); break;
                case "escenario": settings.Scenario = ParseScenario(Next()); break;
                case "lecturas": settings.ReadingSeconds = Math.Max(1, int.Parse(Next(), CultureInfo.InvariantCulture)); break;
                case "consulta": settings.PollSeconds = Math.Max(1, int.Parse(Next(), CultureInfo.InvariantCulture)); break;
                case "litros": settings.Liters = Math.Max(1, ParseNumber(Next())); break;
                case "ph": settings.Ph = ParseNumber(Next()); break;
                case "ce": settings.Ec = ParseNumber(Next()); break;
                case "acelerar": settings.Acceleration = Math.Clamp(ParseNumber(Next()), 1, 1_000); break;
                case "circuitos": settings.Circuits = Math.Clamp(int.Parse(Next(), CultureInfo.InvariantCulture), 1, 9); break;
                case "dias":
                    settings.Days = Next().Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(d => Math.Clamp(ParseNumber(d), 0, 80)).ToList();
                    if (settings.Days.Count == 0)
                        throw new ArgumentException("Falta el valor de --dias.");
                    break;
                case "plantas": settings.Plants = Math.Clamp(int.Parse(Next(), CultureInfo.InvariantCulture), 0, 1_000); break;
                case "inseguro": settings.Insecure = true; break;
                case "transporte": settings.Transport = Next().ToLowerInvariant(); break;
                case "conexion": settings.DeviceConnectionString = Next(); break;
                case "help" or "h" or "ayuda": settings.ShowHelp = true; break;
                default: throw new ArgumentException($"Opción desconocida: {args[i]}");
            }
        }

        if (settings.Transport is not ("http" or "iothub"))
            throw new ArgumentException($"Transporte desconocido: {settings.Transport}");

        if (settings.Transport == "iothub")
        {
            settings.DeviceConnectionString ??= Environment.GetEnvironmentVariable("HYDROPILOT_DEVICE_CONNECTION");
            if (string.IsNullOrWhiteSpace(settings.DeviceConnectionString))
                throw new ArgumentException("Con --transporte iothub hace falta --conexion (o HYDROPILOT_DEVICE_CONNECTION).");
            settings.Node = IotHubPiTransport.DeviceIdOf(settings.DeviceConnectionString)
                ?? throw new ArgumentException("La cadena de conexión no tiene DeviceId.");
        }
        else
        {
            settings.ApiKey ??= Environment.GetEnvironmentVariable("HYDROPILOT_API_KEY") ?? ApiKeyFromUserSecrets();
        }
        return settings;
    }

    private static Scenario ParseScenario(string value) => value.ToLowerInvariant() switch
    {
        "normal" => Scenario.Normal,
        "ph-sube" => Scenario.PhRising,
        "ce-baja" => Scenario.EcFalling,
        "calor" => Scenario.Heat,
        "bomba-rota" => Scenario.BrokenPhPump,
        "sin-conexion" => Scenario.NoConnection,
        "sensor-pegado" => Scenario.StuckSensor,
        _ => throw new ArgumentException($"Escenario desconocido: {value}"),
    };

    /// <summary>Acepta coma o punto decimal ("6,2" o "6.2").</summary>
    private static double ParseNumber(string value) =>
        double.Parse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Lee solo TelemetryApiKey del secrets.json de HydroPilotWeb (nunca lo muestra).</summary>
    private static string? ApiKeyFromUserSecrets()
    {
        var root = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "UserSecrets")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".microsoft", "usersecrets");
        var file = Path.Combine(root, WebUserSecretsId, "secrets.json");
        if (!File.Exists(file))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.TryGetProperty("TelemetryApiKey", out var key) ? key.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
