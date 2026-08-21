using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;

namespace HydroPilotWeb.Services.Lotes;

/// <summary>
/// Operaciones de ciclo de vida por planta (plan 09): cosecha y descarte.
/// La cosecha/descarte se decide a nivel planta y se registra en el estado
/// operativo; el estado comercial del momento queda congelado como histórico.
/// </summary>
public class PlantLifecycleService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;

    public PlantLifecycleService(IDbContextFactory<HydroPilotDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
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
        return plant;
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
        return plant;
    }
}