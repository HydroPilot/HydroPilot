using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Forecasting;

namespace HydroPilotWeb.Services.Lotes;

/// <summary>Resultado de cosechar o descartar varias plantas: cuántas de cada destino y si el lote se cerró.</summary>
public sealed record PlantBatchResult(int BabyLeaf, int Adult, int Discarded, string? LotClosedAs)
{
    public int Harvested => BabyLeaf + Adult;
}

/// <summary>Destino de cosecha según la ruta y el estado comercial de la planta (lógica pura).</summary>
public static class PlantHarvestRules
{
    /// <summary>Trasplantada → adulta; en el denso, Baby Leaf o candidata → Baby Leaf; el resto → adulta.</summary>
    public static HarvestDestination DestinationFor(string? commercialStageName, bool isAdultRoute = false) =>
        !isAdultRoute && commercialStageName is CommercialStageNames.BabyLeafApta or CommercialStageNames.CandidataBabyLeaf
            ? HarvestDestination.BabyLeaf
            : HarvestDestination.Convencional;
}

/// <summary>
/// Operaciones de ciclo de vida por planta (plan 09): cosecha y descarte, de a una (detalle de la planta)
/// o de varias elegidas en el mapa.
/// La cosecha/descarte se decide a nivel planta y se registra en el estado
/// operativo; el estado comercial del momento queda congelado como histórico.
/// Cuando a un circuito del lote ya no le quedan plantas activas del lote, el circuito se libera (el denso después del
/// último corte de Baby Leaf; el de Adultas con la última adulta); cuando no le queda ninguna, el lote se cierra solo
/// (CloseLotIfDoneAsync).
/// </summary>
public class PlantLifecycleService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly SystemParametersService _parameters;

    public PlantLifecycleService(IDbContextFactory<HydroPilotDbContext> dbFactory, SystemParametersService parameters)
    {
        _dbFactory = dbFactory;
        _parameters = parameters;
    }

    /// <summary>Cosecha una planta (detalle de la planta): mismo destino por estado que la selección.</summary>
    public async Task<Plant?> HarvestPlantAsync(int plantId, DateOnly? harvestDate = null, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var plant = await context.Plants
            .Include(p => p.CommercialStage)
            .FirstOrDefaultAsync(p => p.Id == plantId, ct);

        if (plant is null)
            return null;
        if (plant.OperationalState != PlantOperationalState.Activa)
            return plant; // No se re-cosecha ni se cambia un estado histórico.

        var adult = await AdultStageAsync(context, ct);
        Harvest(context, plant, adult, harvestDate ?? Today, DateTime.UtcNow, "harvest");

        await context.SaveChangesAsync(ct);
        await CloseLotIfDoneAsync(context, plant.LotId, ct);
        return plant;
    }

    /// <summary>
    /// Cosecha las plantas elegidas en el mapa (solo las activas del lote). El destino sale del estado de
    /// cada una (<see cref="PlantHarvestRules.DestinationFor"/>): Baby Leaf o candidata se cosecha como Baby
    /// Leaf y conserva su estado; el resto se cosecha como adulta y su estado pasa a "Adulta".
    /// </summary>
    public async Task<PlantBatchResult> HarvestPlantsAsync(
        int lotId, IReadOnlyCollection<int> plantIds, DateOnly? harvestDate = null, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var plants = await ActivePlantsAsync(context, lotId, plantIds, ct);
        var adult = await AdultStageAsync(context, ct);

        var date = harvestDate ?? Today;
        var now = DateTime.UtcNow;
        var babyLeaf = 0;
        foreach (var plant in plants)
        {
            if (Harvest(context, plant, adult, date, now, "harvest-bulk") == HarvestDestination.BabyLeaf)
                babyLeaf++;
        }

        await context.SaveChangesAsync(ct);
        var closedAs = plants.Count > 0 ? await CloseLotIfDoneAsync(context, lotId, ct) : null;
        return new PlantBatchResult(babyLeaf, plants.Count - babyLeaf, 0, closedAs);
    }

    /// <summary>Descarta una planta (detalle de la planta).</summary>
    public async Task<Plant?> DiscardPlantAsync(int plantId, string? reason = null, DateOnly? discardDate = null, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var plant = await context.Plants
            .Include(p => p.CommercialStage)
            .FirstOrDefaultAsync(p => p.Id == plantId, ct);

        if (plant is null)
            return null;
        if (plant.OperationalState != PlantOperationalState.Activa)
            return plant; // Un estado histórico no se re-descarta.

        Discard(context, plant, reason, discardDate ?? Today, DateTime.UtcNow);

        await context.SaveChangesAsync(ct);
        await CloseLotIfDoneAsync(context, plant.LotId, ct);
        return plant;
    }

    /// <summary>Descarta las plantas elegidas en el mapa (solo las activas del lote), con el mismo motivo.</summary>
    public async Task<PlantBatchResult> DiscardPlantsAsync(
        int lotId, IReadOnlyCollection<int> plantIds, string? reason, DateOnly? discardDate = null, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var plants = await ActivePlantsAsync(context, lotId, plantIds, ct);

        var date = discardDate ?? Today;
        var now = DateTime.UtcNow;
        foreach (var plant in plants)
            Discard(context, plant, reason, date, now);

        await context.SaveChangesAsync(ct);
        var closedAs = plants.Count > 0 ? await CloseLotIfDoneAsync(context, lotId, ct) : null;
        return new PlantBatchResult(0, 0, plants.Count, closedAs);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static Task<List<Plant>> ActivePlantsAsync(
        HydroPilotDbContext context, int lotId, IReadOnlyCollection<int> plantIds, CancellationToken ct) =>
        context.Plants
            .Include(p => p.CommercialStage)
            .Where(p => p.LotId == lotId && plantIds.Contains(p.Id) && p.OperationalState == PlantOperationalState.Activa)
            .ToListAsync(ct);

    /// <summary>Estado "Adulta" del catálogo; null si falta (la cosecha se registra igual, sin cambiar el estado).</summary>
    private static Task<CommercialStage?> AdultStageAsync(HydroPilotDbContext context, CancellationToken ct) =>
        context.CommercialStages.FirstOrDefaultAsync(s => s.Name == CommercialStageNames.CosechaConvencional, ct);

    /// <summary>Cosecha una planta activa con el destino de su estado; el estado comercial queda congelado.</summary>
    private static HarvestDestination Harvest(
        HydroPilotDbContext context, Plant plant, CommercialStage? adult, DateOnly date, DateTime now, string source)
    {
        var destination = PlantHarvestRules.DestinationFor(plant.CommercialStage?.Name, plant.IsAdultRoute);
        var previousStageId = plant.CommercialStageId;
        plant.OperationalState = PlantOperationalState.Cosechada;
        plant.HarvestDate = date;
        plant.DiscardDate = null;
        plant.DiscardReason = null;
        if (destination == HarvestDestination.Convencional && adult is not null)
            plant.CommercialStageId = adult.Id; // el destino queda registrado en el estado congelado

        context.PlantStageHistories.Add(new PlantStageHistory
        {
            PlantId = plant.Id,
            PreviousOperationalState = PlantOperationalState.Activa.ToString(),
            PreviousCommercialStageId = previousStageId,
            NewOperationalState = PlantOperationalState.Cosechada.ToString(),
            NewCommercialStageId = plant.CommercialStageId,
            Source = source,
            Reason = destination == HarvestDestination.BabyLeaf ? "Cosechada como Baby Leaf" : "Cosechada como adulta",
            ChangedAtUtc = now
        });
        return destination;
    }

    private static void Discard(HydroPilotDbContext context, Plant plant, string? reason, DateOnly date, DateTime now)
    {
        plant.OperationalState = PlantOperationalState.Descartada;
        plant.DiscardDate = date;
        plant.DiscardReason = reason;

        context.PlantStageHistories.Add(new PlantStageHistory
        {
            PlantId = plant.Id,
            PreviousOperationalState = PlantOperationalState.Activa.ToString(),
            PreviousCommercialStageId = plant.CommercialStageId,
            NewOperationalState = PlantOperationalState.Descartada.ToString(),
            NewCommercialStageId = plant.CommercialStageId, // último estado comercial congelado
            Source = "discard",
            Reason = reason,
            ChangedAtUtc = now
        });
    }

    /// <summary>
    /// Cierra el lote cuando ya no le quedan plantas activas: Cosechado si se cosechó alguna (fecha de
    /// cosecha = la última, con los pesos y precios de ese día guardados en el lote) o Descartado si se
    /// descartaron todas. Solo lotes en curso (Activo / En pausa) que tienen plantas. Devuelve el
    /// estado nuevo, o null si el lote sigue igual.
    /// </summary>
    private async Task<string?> CloseLotIfDoneAsync(HydroPilotDbContext context, int lotId, CancellationToken ct)
    {
        var lot = await context.Lots.Include(l => l.Status).FirstOrDefaultAsync(l => l.Id == lotId, ct);
        if (lot?.Status?.Name is not (LotStatusNames.Activo or LotStatusNames.EnPausa))
            return null;

        await ReleaseCircuitsAsync(context, lot, ct);
        var plants = context.Plants.Where(p => p.LotId == lotId);
        if (!await plants.AnyAsync(ct) || await plants.AnyAsync(p => p.OperationalState == PlantOperationalState.Activa, ct))
            return null;

        var lastHarvest = await plants.MaxAsync(p => p.HarvestDate, ct);
        var statusName = lastHarvest is null ? LotStatusNames.Descartado : LotStatusNames.Cosechado;
        var status = await context.LotStatuses.FirstOrDefaultAsync(s => s.Name == statusName, ct);
        if (status is null)
            return null;

        lot.StatusId = status.Id;
        // El circuito queda libre para el próximo lote.
        var now = DateTime.UtcNow;
        foreach (var assignment in await context.CircuitAssignments.Where(a => a.LotId == lotId && a.ValidUntilUtc == null).ToListAsync(ct))
            assignment.ValidUntilUtc = now;
        if (lastHarvest is { } harvestDate)
        {
            lot.ActualHarvestDate = harvestDate;
            await _parameters.ApplyHarvestReferenceAsync(context, lot, harvestDate, ct);
        }
        // El pico de aptas del ciclo: el óptimo que aprenden los lotes siguientes.
        lot.BabyLeafPeakGdd ??= await BabyLeafOptimumService.PeakGddAsync(context, lot.Id, ct);

        await context.SaveChangesAsync(ct);
        return statusName;
    }

    /// <summary>
    /// Libera los circuitos del lote que ya no tienen plantas activas del lote: el denso cuando se cortó, descartó o
    /// trasplantó la última; el de Adultas con la última adulta. Guarda los cambios.
    /// </summary>
    public static async Task ReleaseCircuitsAsync(HydroPilotDbContext context, Lot lot, CancellationToken ct)
    {
        var open = await context.CircuitAssignments.Where(a => a.LotId == lot.Id && a.ValidUntilUtc == null).ToListAsync(ct);
        if (open.Count == 0)
            return;
        var busy = await context.Plants
            .Where(p => p.LotId == lot.Id && p.OperationalState == PlantOperationalState.Activa)
            .Select(p => p.CircuitId ?? lot.CircuitId)
            .Distinct()
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var assignment in open.Where(a => !busy.Contains(a.CircuitId)))
            assignment.ValidUntilUtc = now;
        await context.SaveChangesAsync(ct);
    }
}