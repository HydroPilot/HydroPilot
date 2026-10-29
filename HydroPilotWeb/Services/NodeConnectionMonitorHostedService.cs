using System.Globalization;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Notifications;
using HydroPilotWeb.Services.Notifications;
using HydroPilotWeb.Services.SimulationEnvironment;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Services;

/// <summary>
/// Monitor periódico del estado de conexión de los nodos (IOT-06).
/// Recalcula y persiste ConnectionState para que el dashboard distinga
/// NEVER_CONNECTED / ONLINE / DEGRADED / OFFLINE sin duplicar la regla.
/// Un nodo OFFLINE genera una alerta "Nodo X desconectado" (Alta, una por corte) que se resuelve sola
/// cuando vuelven a llegar lecturas. Un nodo que nunca se conectó no genera alerta.
/// </summary>
public sealed class NodeConnectionMonitorHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<TelemetryOptions> _options;
    private readonly ILogger<NodeConnectionMonitorHostedService> _logger;

    public NodeConnectionMonitorHostedService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<TelemetryOptions> options,
        ILogger<NodeConnectionMonitorHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Monitor de conexión de nodos iniciado.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var lease = RegenerationPause.TryEnter();
                if (lease is not null)
                    await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallo en el ciclo del monitor de conexión de nodos.");
            }

            var interval = TimeSpan.FromSeconds(Math.Max(_options.CurrentValue.MonitorIntervalSeconds, 10));
            await Task.Delay(interval, stoppingToken);
        }
    }

    /// <summary>Recalcula y persiste el estado de conexión de todos los nodos. Expuesto para pruebas.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<HydroPilotDbContext>();
        var options = _options.CurrentValue;
        var nowUtc = DateTime.UtcNow;

        var nodes = await context.IotNodes.Include(n => n.Greenhouse).ToListAsync(ct);
        var previous = new Dictionary<int, string>();
        foreach (var node in nodes)
        {
            var computed = NodeConnectionEvaluator.Evaluate(
                node.LastAcceptedAt, nowUtc, node.ExpectedIntervalSeconds, options.DegradedIntervalFactor);

            previous[node.Id] = node.ConnectionState;
            if (!string.Equals(node.ConnectionState, computed, StringComparison.Ordinal))
            {
                node.ConnectionState = computed;
                _logger.LogInformation("Nodo {Identifier}: estado de conexión → {State}", node.Identifier, computed);
            }
        }

        if (nodes.Count > 0)
            await context.SaveChangesAsync(ct);

        // Sin servicio de notificaciones (pruebas que solo miran el estado) no se generan alertas.
        if (scope.ServiceProvider.GetService<NotificationService>() is not { } notifications)
            return;
        foreach (var node in nodes)
            await SyncOfflineAlertAsync(context, notifications, node, previous[node.Id], nowUtc, ct);
    }

    /// <summary>
    /// OFFLINE: crea la alerta del corte (una por corte: la huella lleva la última lectura aceptada, así
    /// no se repite en cada vuelta del monitor). Si estaba OFFLINE y volvió: resuelve las alertas del nodo.
    /// </summary>
    private async Task SyncOfflineAlertAsync(
        HydroPilotDbContext context, NotificationService notifications, IotNode node, string previousState, DateTime nowUtc, CancellationToken ct)
    {
        try
        {
            if (node.ConnectionState == TelemetryContract.ConnectionOffline && node.LastAcceptedAt is { } lastAccepted)
            {
                var fingerprint = NodeConnectionAlerts.Fingerprint(node.Id, lastAccepted);
                if (await context.NotificationAlerts.AnyAsync(a => a.Fingerprint == fingerprint, ct))
                    return;

                // La Pi maneja todos los circuitos del invernadero: el corte no es de un lote en particular.
                int? lotId = null;
                var zone = GddService.ResolveGreenhouseTimeZone(node.Greenhouse?.TimeZoneId, []);
                var since = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(lastAccepted, DateTimeKind.Utc), zone);

                await notifications.CreateAlertAsync(new CreateAlertDto(
                    NotificationTypes.NodoDesconectado,
                    NotificationSeverities.Alta,
                    $"Nodo {node.Identifier} desconectado",
                    $"Sin lecturas desde el {since.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture)}. Revisá que la Pi tenga energía y red.",
                    fingerprint,
                    lotId), ct);
            }
            else if (previousState == TelemetryContract.ConnectionOffline)
            {
                await notifications.ResolveByFingerprintPrefixAsync(NodeConnectionAlerts.Prefix(node.Id), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "No se pudo sincronizar la alerta de conexión del nodo {Identifier}", node.Identifier);
        }
    }
}

/// <summary>Huella de las alertas de nodo desconectado: "node-offline-{nodoId}-{última lectura aceptada}".</summary>
public static class NodeConnectionAlerts
{
    public static string Prefix(int nodeId) => $"node-offline-{nodeId}-";

    public static string Fingerprint(int nodeId, DateTime lastAcceptedUtc) =>
        Prefix(nodeId) + lastAcceptedUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    /// <summary>Nodo de una huella de nodo desconectado; null si la huella es de otra cosa.</summary>
    public static int? NodeId(string? fingerprint)
    {
        const string start = "node-offline-";
        if (fingerprint is null || !fingerprint.StartsWith(start, StringComparison.Ordinal))
            return null;
        var rest = fingerprint[start.Length..];
        var dash = rest.IndexOf('-');
        return dash > 0 && int.TryParse(rest[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
    }
}