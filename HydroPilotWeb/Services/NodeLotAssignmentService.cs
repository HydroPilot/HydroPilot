using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Services;

public sealed record NodeLotAssignmentDto(
    int Id,
    int NodeId,
    string NodeIdentifier,
    int LotId,
    string LotLabel,
    DateTime ValidFromUtc,
    DateTime? ValidUntilUtc,
    string Source);

/// <summary>
/// CRUD de la asignación temporal nodo→lote. Valida solapamientos (un nodo no puede
/// tener dos asignaciones vigentes a la vez) y lotes/nodos existentes.
/// </summary>
public sealed class NodeLotAssignmentService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;

    public NodeLotAssignmentService(IDbContextFactory<HydroPilotDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public sealed record CreateResult(NodeLotAssignmentDto? Assignment, string? Error);

    /// <summary>Valida que el rango no solape ninguna asignación existente del nodo.</summary>
    public async Task<CreateResult> CreateAsync(
        int nodeId,
        int lotId,
        DateTime validFromUtc,
        DateTime? validUntilUtc,
        string source,
        CancellationToken ct)
    {
        if (validUntilUtc.HasValue && validUntilUtc.Value <= validFromUtc)
            return new(null, "'validUntilUtc' debe ser posterior a 'validFromUtc'.");

        if (!string.Equals(source, TelemetryContract.AssignmentSourceManual, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(source, TelemetryContract.AssignmentSourceAuto, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(source, TelemetryContract.AssignmentSourceFixture, StringComparison.OrdinalIgnoreCase))
            return new(null, "Origen de asignación desconocido (manual | auto | fixture).");

        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var nodeExists = await context.IotNodes.AnyAsync(n => n.Id == nodeId, ct);
        if (!nodeExists) return new(null, $"Nodo {nodeId} no registrado.");

        var lot = await context.Lots.Include(l => l.CropType).FirstOrDefaultAsync(l => l.Id == lotId, ct);
        if (lot is null) return new(null, $"Lote {lotId} no registrado.");

        // Solapamiento: [from, until) contra las asignaciones del nodo.
        var overlapping = await context.NodeLotAssignments
            .Where(a => a.NodeId == nodeId &&
                        a.ValidFromUtc < (validUntilUtc ?? DateTime.MaxValue) &&
                        (a.ValidUntilUtc == null || a.ValidUntilUtc > validFromUtc))
            .AnyAsync(ct);
        if (overlapping)
            return new(null, "La asignación se solapa con una asignación existente del nodo.");

        var normalizedFrom = DateTime.SpecifyKind(validFromUtc, DateTimeKind.Utc);
        DateTime? normalizedUntil = validUntilUtc.HasValue
            ? DateTime.SpecifyKind(validUntilUtc.Value, DateTimeKind.Utc)
            : null;

        var assignment = new NodeLotAssignment
        {
            NodeId = nodeId,
            LotId = lotId,
            ValidFromUtc = normalizedFrom,
            ValidUntilUtc = normalizedUntil,
            Source = source.ToLowerInvariant(),
        };
        context.NodeLotAssignments.Add(assignment);
        await context.SaveChangesAsync(ct);

        return new(ToDto(assignment, nodeId, null, lotId, lot.CropType!.Name), null);
    }

    public async Task<IReadOnlyList<NodeLotAssignmentDto>> ListAsync(int? nodeId, CancellationToken ct)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var query = context.NodeLotAssignments.AsQueryable();

        if (nodeId.HasValue)
            query = query.Where(a => a.NodeId == nodeId.Value);

        var rows = await query
            .OrderByDescending(a => a.ValidFromUtc)
            .Select(a => new NodeLotAssignmentDto(
                a.Id,
                a.NodeId,
                a.Node!.Identifier,
                a.LotId,
                a.Lot!.CropType!.Name,
                a.ValidFromUtc,
                a.ValidUntilUtc,
                a.Source))
            .ToListAsync(ct);
        return rows;
    }

    private static NodeLotAssignmentDto ToDto(
        NodeLotAssignment a, int nodeId, string? nodeIdentifier, int lotId, string lotLabel) =>
        new(a.Id, nodeId, nodeIdentifier ?? $"nodo-{nodeId}", lotId, lotLabel, a.ValidFromUtc, a.ValidUntilUtc, a.Source);
}