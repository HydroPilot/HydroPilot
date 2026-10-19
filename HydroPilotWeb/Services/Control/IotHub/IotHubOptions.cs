namespace HydroPilotWeb.Services.Control.IotHub;

/// <summary>
/// Azure IoT Hub como transporte con la Pi (sección "IotHub"; las cadenas van en user-secrets o en la
/// configuración de la Web App, nunca en appsettings.json). Apagado: todo va por HTTP como en la fase 1.
/// Guía de alta en notas/control/azure-iot-hub.md.
/// </summary>
public sealed class IotHubOptions
{
    public const string SectionName = "IotHub";

    public bool Enabled { get; set; }

    /// <summary>Cadena de la política "service" del hub: mandar comandos (C2D) y leer sus acuses.</summary>
    public string? ServiceConnectionString { get; set; }

    /// <summary>Punto de conexión compatible con Event Hub del hub (Built-in endpoints), con EntityPath.</summary>
    public string? EventHubConnectionString { get; set; }

    /// <summary>Grupo de consumidores propio de la web (crearlo en el hub; no compartir $Default).</summary>
    public string ConsumerGroup { get; set; } = "hydropilot-web";

    /// <summary>Sin checkpoint guardado, desde cuántos minutos atrás se leen los mensajes al arrancar.</summary>
    public int StartBackMinutes { get; set; } = 60;
}
