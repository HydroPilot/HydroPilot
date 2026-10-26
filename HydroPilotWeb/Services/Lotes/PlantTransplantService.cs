using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Forecasting;

namespace HydroPilotWeb.Services.Lotes;

/// <summary>Circuito de Adultas que puede recibir plantas del lote: el suyo (si ya trasplantó) o uno libre.</summary>
public sealed record AdultRoom(int CircuitId, string CircuitName, int FreeHoles);

/// <summary>
/// Trasplante del lote: si corresponde (entre el GDD de decisión y el cierre de la ventana Baby Leaf, con plantas en el
/// denso), la propuesta (<see cref="TransplantRules"/>), los huecos de Adultas disponibles y el costo por planta.
/// </summary>
public sealed record TransplantPlan(
    bool IsDue,
    decimal DecisionGdd,
    TransplantProposal Proposal,
    IReadOnlyList<AdultRoom> Room,
    decimal CostPerPlant,
    int Transplanted)
{
    public int Capacity => Room.Sum(r => r.FreeHoles);
}

public sealed record TransplantResult(int Transplanted, IReadOnlyList<string> Circuits, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Errors.Count == 0;
}

/// <summary>
/// Trasplante a Adultas desde la vista Lotes: las plantas elegidas pasan del circuito denso a huecos libres de circuitos
/// de Adultas (primero los que ya tiene el lote, después los libres). El lote queda en varios circuitos a la vez; el
/// denso se libera con la última planta que sale (<see cref="PlantLifecycleService.ReleaseCircuitsAsync"/>).
/// </summary>
public class PlantTransplantService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly ILogger<PlantTransplantService> _logger;

    public PlantTransplantService(IDbContextFactory<HydroPilotDbContext> dbFactory, ILogger<PlantTransplantService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>Los mismos roles que crean lotes (Soporte Técnico no).</summary>
    public static bool CanTransplant(ClaimsPrincipal user) => LotCreationService.CanCreate(user);

    public async Task<TransplantPlan?> GetPlanAsync(int lotId, decimal lotGdd, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var lot = await context.Lots.AsNoTracking().Include(l => l.Status).FirstOrDefaultAsync(l => l.Id == lotId, ct);
        if (lot is null || lot.Status?.Name is not (LotStatusNames.Activo or LotStatusNames.EnPausa))
            return null;
        var config = await context.BabyLeafConfigs.AsNoTracking()
            .Where(c => c.CropTypeId == lot.CropTypeId && c.IsActive)
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync(ct);
        if (config is null)
            return null;

        var parameters = await SystemParametersService.LoadTransplantAsync(context, ct);
        var plants = await context.Plants.AsNoTracking()
            .Where(p => p.LotId == lotId)
            .Select(p => new
            {
                p.Id,
                p.OperationalState,
                p.TransplantedOn,
                Stage = p.CommercialStage != null ? p.CommercialStage.Name : null,
                Score = p.Evaluations.OrderByDescending(e => e.EvaluatedAtUtc).Select(e => (decimal?)e.BabyLeafScore).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var babyLeafRoute = plants
            .Where(p => p.OperationalState == PlantOperationalState.Activa && p.TransplantedOn is null)
            .Select(p => new TransplantCandidate(p.Id, p.Score))
            .ToList();
        var alreadyBabyLeaf = plants.Count(p => p.OperationalState == PlantOperationalState.Cosechada && p.TransplantedOn is null
            && PlantHarvestRules.DestinationFor(p.Stage) == HarvestDestination.BabyLeaf);
        var room = await RoomAsync(context, lot, ct);

        var proposal = TransplantRules.Propose(babyLeafRoute, plants.Count, lot.BabyLeafGoalPercent, room.Sum(r => r.FreeHoles),
            config.ScoreMinCandidate, alreadyBabyLeaf);
        return new TransplantPlan(
            babyLeafRoute.Count > 0 && TransplantRules.IsDue(lotGdd, parameters.DecisionGdd, config.GddMax),
            parameters.DecisionGdd,
            proposal,
            room,
            parameters.CostPerPlant,
            plants.Count(p => p.TransplantedOn is not null));
    }

    public async Task<TransplantResult> TransplantAsync(int lotId, IReadOnlyCollection<int> plantIds, ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (!CanTransplant(user))
            return Fail("Solo Administrador, Productor y Operador pueden trasplantar.");
        if (plantIds.Count == 0)
            return Fail("Elegí las plantas que pasan a Adultas.");

        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var lot = await context.Lots.Include(l => l.Status).FirstOrDefaultAsync(l => l.Id == lotId, ct);
        if (lot is null || lot.Status?.Name is not (LotStatusNames.Activo or LotStatusNames.EnPausa))
            return Fail("El lote no está en curso.");

        var plants = await context.Plants
            .Where(p => p.LotId == lotId && plantIds.Contains(p.Id)
                        && p.OperationalState == PlantOperationalState.Activa && p.TransplantedOn == null)
            .ToListAsync(ct);
        if (plants.Count != plantIds.Count)
            return Fail("Solo se trasplantan plantas activas que siguen en el circuito denso.");

        var room = await RoomAsync(context, lot, ct);
        if (room.Sum(r => r.FreeHoles) < plants.Count)
            return Fail($"No entran: hay {room.Sum(r => r.FreeHoles)} huecos libres en Adultas.");

        // Queda "En desarrollo" en la ruta Adulta: pasa a "Adulta" cuando llega a su corte (lo decide el flujo diario).
        var developing = await context.CommercialStages.FirstOrDefaultAsync(
            s => s.CropTypeId == lot.CropTypeId && s.Name == CommercialStageNames.EnDesarrollo, ct);
        var parameters = await SystemParametersService.LoadTransplantAsync(context, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var now = DateTime.UtcNow;
        var used = new List<string>();
        var queue = new Queue<Plant>(plants.OrderBy(p => p.Row).ThenBy(p => p.Column));

        foreach (var target in room)
        {
            if (queue.Count == 0)
                break;
            var circuit = await context.Circuits.FirstAsync(c => c.Id == target.CircuitId, ct);
            var taken = (await context.Plants
                    .Where(p => p.LotId == lotId && p.CircuitId == circuit.Id)
                    .Select(p => new { p.Row, p.Column })
                    .ToListAsync(ct))
                .Select(p => (p.Row, p.Column))
                .ToHashSet();
            var moved = 0;
            foreach (var (row, column) in TransplantRules.FreeHoles(circuit.GridRows, circuit.GridColumns, taken))
            {
                if (queue.Count == 0)
                    break;
                var plant = queue.Dequeue();
                var previousStage = plant.CommercialStageId;
                (plant.DenseRow, plant.DenseColumn) = (plant.Row, plant.Column);
                plant.CircuitId = circuit.Id;
                plant.Row = row;
                plant.Column = column;
                plant.TransplantedOn = today;
                if (developing is not null)
                    plant.CommercialStageId = developing.Id;
                context.PlantStageHistories.Add(new PlantStageHistory
                {
                    PlantId = plant.Id,
                    PreviousOperationalState = plant.OperationalState.ToString(),
                    PreviousCommercialStageId = previousStage,
                    NewOperationalState = plant.OperationalState.ToString(),
                    NewCommercialStageId = plant.CommercialStageId,
                    Source = "transplant",
                    Reason = $"Trasplantada a {circuit.Name}",
                    ChangedAtUtc = now,
                });
                moved++;
            }
            if (moved == 0)
                continue;
            used.Add(circuit.Name);
            // El circuito de Adultas queda con el lote desde ahora (sus lecturas de pH y CE van al lote).
            if (!await context.CircuitAssignments.AnyAsync(a => a.CircuitId == circuit.Id && a.LotId == lotId && a.ValidUntilUtc == null, ct))
            {
                context.CircuitAssignments.Add(new CircuitAssignment
                {
                    CircuitId = circuit.Id,
                    LotId = lotId,
                    ValidFromUtc = now,
                    Source = TelemetryContract.AssignmentSourceManual,
                    CreatedAt = now,
                });
            }
        }

        lot.TransplantCostPerPlant = parameters.CostPerPlant;
        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            return Fail("Otro lote ocupó ese circuito de Adultas recién: volvé a intentar.");
        }
        await PlantLifecycleService.ReleaseCircuitsAsync(context, lot, ct);

        _logger.LogInformation("Lote {LotId}: {Count} plantas trasplantadas a {Circuits} por {User}.",
            lotId, plants.Count, string.Join(", ", used), user.Identity?.Name);
        return new TransplantResult(plants.Count, used, []);
    }

    /// <summary>
    /// Huecos de Adultas para el lote: los circuitos de Adultas activos del invernadero que no tienen otro lote (los del
    /// propio lote primero), con sus huecos sin plantas del lote.
    /// </summary>
    public static async Task<List<AdultRoom>> RoomAsync(HydroPilotDbContext context, Lot lot, CancellationToken ct)
    {
        var circuits = await context.Circuits.AsNoTracking()
            .Where(c => c.GreenhouseId == lot.GreenhouseId && c.IsActive && c.Kind == CircuitKinds.Adult)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.GridRows,
                c.GridColumns,
                Holder = c.Assignments.Where(a => a.ValidUntilUtc == null).Select(a => (int?)a.LotId).FirstOrDefault(),
                Used = context.Plants.Count(p => p.CircuitId == c.Id && p.LotId == lot.Id),
            })
            .ToListAsync(ct);
        return circuits
            .Where(c => c.Holder is null || c.Holder == lot.Id)
            .OrderBy(c => c.Holder == lot.Id ? 0 : 1)
            .ThenBy(c => c.Name)
            .Select(c => new AdultRoom(c.Id, c.Name, Math.Max(0, c.GridRows * c.GridColumns - c.Used)))
            .Where(r => r.FreeHoles > 0)
            .ToList();
    }

    /// <summary>Huecos de todos los circuitos de Adultas activos (para planificar un escenario sin lote).</summary>
    public static Task<int> TotalAdultHolesAsync(HydroPilotDbContext context, CancellationToken ct) =>
        context.Circuits.AsNoTracking()
            .Where(c => c.IsActive && c.Kind == CircuitKinds.Adult)
            .SumAsync(c => c.GridRows * c.GridColumns, ct);

    private static TransplantResult Fail(string error) => new(0, [], [error]);
}
