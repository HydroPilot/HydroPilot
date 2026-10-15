using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Forecasting;

namespace HydroPilotWeb.Services.Lotes;

/// <summary>
/// Operaciones de ciclo de vida por planta (plan 09): cosecha y descarte.
/// La cosecha/descarte se decide a nivel planta y se registra en el estado
/// operativo; el estado comercial del momento queda congelado como histórico.
/// Cuando al lote ya no le quedan plantas activas, el lote se cierra solo (CloseLotIfDoneAsync).
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

        var previous = plant.OperationalState.ToString();
        plant.OperationalState = PlantOperationalState.Cosechada;
        plant.HarvestDate = harvestDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        plant.DiscardDate = null;
        plant.DiscardReason = null;

        context.PlantStageHistories.Add(new PlantStageHistory
        {
            PlantId = plant.Id,
            PreviousOperationalState = previous,
            PreviousCommercialStageId = plant.CommercialStageId,
            NewOperationalState = PlantOperationalState.Cosechada.ToString(),
            NewCommercialStageId = plant.CommercialStageId, // congela en qué estado se cosechó
            Source = "harvest",
            Reason = $"Cosechada como {plant.CommercialStage?.Name ?? "sin estado comercial"}",
            ChangedAtUtc = DateTime.UtcNow
        });

        await context.SaveChangesAsync(ct);
        await CloseLotIfDoneAsync(context, plant.LotId, ct);
        return plant;
    }

    /// <summary>
    /// Cosecha masiva del lote (vista Lotes). Baby Leaf: las activas en "Baby Leaf apta", que
    /// conservan su estado. Convencional: todas las activas que no están en riesgo, que pasan a
    /// "Cosecha convencional" (sin análisis de imagen, la aptitud convencional la da la ventana por
    /// GDD del lote; la vista solo lo ofrece desde la ventana convencional). Devuelve cuántas cosechó.
    /// </summary>
    public async Task<int> HarvestManyAsync(
        int lotId, HarvestDestination destination, DateOnly? harvestDate = null, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var active = await context.Plants
            .Include(p => p.CommercialStage)
            .Where(p => p.LotId == lotId && p.OperationalState == PlantOperationalState.Activa)
            .ToListAsync(ct);

        CommercialStage? conventional = null;
        if (destination == HarvestDestination.Convencional)
        {
            conventional = await context.CommercialStages.FirstOrDefaultAsync(s => s.Name == CommercialStageNames.CosechaConvencional, ct)
                ?? throw new InvalidOperationException($"Falta el estado comercial \"{CommercialStageNames.CosechaConvencional}\" en el catálogo.");
        }

        var selected = destination == HarvestDestination.BabyLeaf
            ? active.Where(p => p.CommercialStage?.Name == CommercialStageNames.BabyLeafApta).ToList()
            : active.Where(p => p.CommercialStage?.Name != CommercialStageNames.RiesgoFueraDeVentana).ToList();

        var date = harvestDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var now = DateTime.UtcNow;
        foreach (var plant in selected)
        {
            var previousStageId = plant.CommercialStageId;
            plant.OperationalState = PlantOperationalState.Cosechada;
            plant.HarvestDate = date;
            plant.DiscardDate = null;
            plant.DiscardReason = null;
            if (conventional is not null)
                plant.CommercialStageId = conventional.Id; // el destino queda registrado en el estado congelado

            context.PlantStageHistories.Add(new PlantStageHistory
            {
                PlantId = plant.Id,
                PreviousOperationalState = PlantOperationalState.Activa.ToString(),
                PreviousCommercialStageId = previousStageId,
                NewOperationalState = PlantOperationalState.Cosechada.ToString(),
                NewCommercialStageId = plant.CommercialStageId,
                Source = "harvest-bulk",
                Reason = destination == HarvestDestination.BabyLeaf
                    ? "Cosecha masiva de aptas Baby Leaf"
                    : "Cosecha masiva convencional",
                ChangedAtUtc = now
            });
        }

        await context.SaveChangesAsync(ct);
        if (selected.Count > 0)
            await CloseLotIfDoneAsync(context, lotId, ct);
        return selected.Count;
    }

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

        var previous = plant.OperationalState.ToString();
        plant.OperationalState = PlantOperationalState.Descartada;
        plant.DiscardDate = discardDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        plant.DiscardReason = reason;

        context.PlantStageHistories.Add(new PlantStageHistory
        {
            PlantId = plant.Id,
            PreviousOperationalState = previous,
            PreviousCommercialStageId = plant.CommercialStageId,
            NewOperationalState = PlantOperationalState.Descartada.ToString(),
            NewCommercialStageId = plant.CommercialStageId, // último estado comercial congelado
            Source = "discard",
            Reason = reason,
            ChangedAtUtc = DateTime.UtcNow
        });

        await context.SaveChangesAsync(ct);
        await CloseLotIfDoneAsync(context, plant.LotId, ct);
        return plant;
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

        var plants = context.Plants.Where(p => p.LotId == lotId);
        if (!await plants.AnyAsync(ct) || await plants.AnyAsync(p => p.OperationalState == PlantOperationalState.Activa, ct))
            return null;

        var lastHarvest = await plants.MaxAsync(p => p.HarvestDate, ct);
        var statusName = lastHarvest is null ? LotStatusNames.Descartado : LotStatusNames.Cosechado;
        var status = await context.LotStatuses.FirstOrDefaultAsync(s => s.Name == statusName, ct);
        if (status is null)
            return null;

        lot.StatusId = status.Id;
        if (lastHarvest is { } harvestDate)
        {
            lot.ActualHarvestDate = harvestDate;
            await _parameters.ApplyHarvestReferenceAsync(context, lot, harvestDate, ct);
        }

        await context.SaveChangesAsync(ct);
        return statusName;
    }
}