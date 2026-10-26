using System.Security.Claims;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Lotes;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Services.Dashboard;

/// <summary>Visita guardada, con el nombre del lote o del nodo para mostrarla.</summary>
public sealed record RecentVisit(string Kind, int? LotId, string? LotName, int? CircuitId, string? CircuitName, DateTime VisitedAtUtc);

/// <summary>
/// "Últimos accedidos": qué lote abrió cada usuario en Lotes y Optimización. Una fila por usuario +
/// vista + lote (o nodo sin lote); cada visita actualiza la fecha. Se guardan las últimas MaxPerUser.
/// </summary>
public class RecentVisitService
{
    public const int MaxPerUser = 20;

    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly ILogger<RecentVisitService> _logger;

    public RecentVisitService(IDbContextFactory<HydroPilotDbContext> dbFactory, ILogger<RecentVisitService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>Anota la visita. Nunca falla hacia la vista: si no se puede guardar, solo se loguea.</summary>
    public async Task RecordAsync(ClaimsPrincipal user, string kind, int? lotId, int? circuitId = null, CancellationToken ct = default)
    {
        if (LotCommentRules.UserId(user) is not { } userId || (lotId is null && circuitId is null))
            return;

        try
        {
            await using var context = await _dbFactory.CreateDbContextAsync(ct);
            if (!await context.Users.AnyAsync(u => u.Id == userId, ct))
                return;

            var visit = await context.UserRecentVisits.FirstOrDefaultAsync(v =>
                v.UserId == userId && v.Kind == kind && v.LotId == lotId && (lotId != null || v.CircuitId == circuitId), ct);
            if (visit is null)
                context.UserRecentVisits.Add(visit = new UserRecentVisit { UserId = userId, Kind = kind, LotId = lotId });
            visit.CircuitId = circuitId;
            visit.VisitedAtUtc = DateTime.UtcNow;
            await context.SaveChangesAsync(ct);

            var stale = await context.UserRecentVisits
                .Where(v => v.UserId == userId)
                .OrderByDescending(v => v.VisitedAtUtc)
                .Skip(MaxPerUser)
                .ToListAsync(ct);
            if (stale.Count > 0)
            {
                context.UserRecentVisits.RemoveRange(stale);
                await context.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "No se pudo guardar la visita de {UserId} a {Kind} (lote {LotId}, circuito {CircuitId})", userId, kind, lotId, circuitId);
        }
    }

    /// <summary>Últimas visitas del usuario, más recientes primero (sin las de lotes o circuitos que ya no existen).</summary>
    public async Task<IReadOnlyList<RecentVisit>> GetAsync(ClaimsPrincipal user, int take = 10, CancellationToken ct = default)
    {
        if (LotCommentRules.UserId(user) is not { } userId)
            return [];

        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await context.UserRecentVisits
            .AsNoTracking()
            .Where(v => v.UserId == userId)
            .OrderByDescending(v => v.VisitedAtUtc)
            .Take(take)
            .Select(v => new
            {
                v.Kind,
                v.LotId,
                LotName = v.Lot != null ? v.Lot.Name : null,
                v.CircuitId,
                CircuitName = context.Circuits.Where(c => c.Id == v.CircuitId).Select(c => c.Name).FirstOrDefault(),
                v.VisitedAtUtc
            })
            .ToListAsync(ct);

        return rows
            .Where(v => v.LotId is not null || v.CircuitName is not null)
            .Select(v => new RecentVisit(v.Kind, v.LotId, v.LotName, v.CircuitId, v.CircuitName, v.VisitedAtUtc))
            .ToList();
    }
}
