using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Services;

/// <summary>
/// Monitor periódico del estado de conexión de los nodos (IOT-06).
/// Recalcula y persiste ConnectionState para que el dashboard distinga
/// NEVER_CONNECTED / ONLINE / DEGRADED / OFFLINE sin duplicar la regla.
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

        var nodes = await context.IotNodes.ToListAsync(ct);
        foreach (var node in nodes)
        {
            var computed = NodeConnectionEvaluator.Evaluate(
                node.LastAcceptedAt, nowUtc, node.ExpectedIntervalSeconds, options.DegradedIntervalFactor);

            if (!string.Equals(node.ConnectionState, computed, StringComparison.Ordinal))
            {
                node.ConnectionState = computed;
                _logger.LogInformation("Nodo {Identifier}: estado de conexión → {State}", node.Identifier, computed);
            }
        }

        if (nodes.Count > 0)
            await context.SaveChangesAsync(ct);
    }
}