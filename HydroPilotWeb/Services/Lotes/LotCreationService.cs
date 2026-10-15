using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Forecasting;

namespace HydroPilotWeb.Services.Lotes;

/// <summary>
/// Alta de un lote desde la vista Lotes. Sin m²: en hidroponía cada planta tiene su hueco, así que
/// el lote se define por su grilla (filas × columnas) y se crea una planta por posición. Las metas
/// del ciclo son opcionales: null = sin definir.
/// </summary>
public sealed record NewLotInput(
    string? Name,
    int CropTypeId,
    int GreenhouseId,
    DateOnly SowingDate,
    int Rows,
    int Columns,
    decimal? BabyLeafGoalPercent = null,
    decimal? MaxLossPercent = null);

public sealed record NewLotChoice(int Id, string Name);

/// <summary>Opciones del formulario de alta: cultivos, invernaderos y un nombre sugerido.</summary>
public sealed record NewLotOptions(IReadOnlyList<NewLotChoice> Crops, IReadOnlyList<NewLotChoice> Greenhouses, string SuggestedName);

public sealed record NewLotResult(int? LotId, IReadOnlyList<string> Errors)
{
    public bool Succeeded => LotId is not null;
}

/// <summary>Validación del alta de un lote. Lógica pura.</summary>
public static class NewLotRules
{
    public const int MaxGridSide = 30;
    public const int MaxNameLength = 150;

    public static IReadOnlyList<string> Validate(NewLotInput input, DateOnly today)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(input.Name))
            errors.Add("Poné un nombre para el lote.");
        else if (input.Name.Trim().Length > MaxNameLength)
            errors.Add($"El nombre puede tener hasta {MaxNameLength} caracteres.");

        if (input.CropTypeId <= 0)
            errors.Add("Elegí el cultivo.");
        if (input.GreenhouseId <= 0)
            errors.Add("Elegí el invernadero.");
        if (input.SowingDate > today)
            errors.Add("La fecha de siembra no puede ser futura.");
        if (input.Rows is < 1 or > MaxGridSide || input.Columns is < 1 or > MaxGridSide)
            errors.Add($"Filas y columnas tienen que ir de 1 a {MaxGridSide}.");

        // Mismas reglas que el editor de metas del ciclo.
        errors.AddRange(LotObjectivesRules.Validate(Goals(input)));
        return errors;
    }

    /// <summary>Metas del ciclo del alta; el umbral de cosecha no se pide (Lotes no lo usa).</summary>
    public static LotObjectives Goals(NewLotInput input) =>
        LotObjectivesRules.Normalize(new LotObjectives(null, input.BabyLeafGoalPercent, input.MaxLossPercent));
}

public class LotCreationService
{
    /// <summary>Roles que pueden dar de alta un lote (Soporte Técnico no).</summary>
    public static readonly IReadOnlyList<string> CreatorRoles = ["Administrador", "Productor", "Operador"];

    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly ILogger<LotCreationService> _logger;

    public LotCreationService(IDbContextFactory<HydroPilotDbContext> dbFactory, ILogger<LotCreationService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public static bool CanCreate(ClaimsPrincipal user) => CreatorRoles.Any(user.IsInRole);

    public async Task<NewLotOptions> GetOptionsAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var crops = await context.CropTypes
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new NewLotChoice(c.Id, c.Name))
            .ToListAsync(ct);
        var greenhouses = await context.Greenhouses
            .AsNoTracking()
            .OrderBy(g => g.Name)
            .Select(g => new NewLotChoice(g.Id, g.Name))
            .ToListAsync(ct);

        // Nombre sugerido: "Lote NN" con el próximo número libre.
        var names = (await context.Lots.AsNoTracking().Select(l => l.Name).ToListAsync(ct))
            .Where(n => n is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var number = names.Count + 1;
        while (names.Contains($"Lote {number:00}"))
            number++;

        return new NewLotOptions(crops, greenhouses, $"Lote {number:00}");
    }

    public async Task<NewLotResult> CreateAsync(NewLotInput input, ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (!CanCreate(user))
            return new NewLotResult(null, ["Solo Administrador, Productor y Operador pueden crear lotes."]);

        var errors = NewLotRules.Validate(input, DateOnly.FromDateTime(DateTime.UtcNow));
        if (errors.Count > 0)
            return new NewLotResult(null, errors);

        var name = input.Name!.Trim();
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        if (await context.Lots.AnyAsync(l => l.Name == name, ct))
            return new NewLotResult(null, [$"Ya hay un lote llamado \"{name}\"."]);
        if (!await context.CropTypes.AnyAsync(c => c.Id == input.CropTypeId, ct))
            return new NewLotResult(null, ["El cultivo elegido no existe."]);
        if (!await context.Greenhouses.AnyAsync(g => g.Id == input.GreenhouseId, ct))
            return new NewLotResult(null, ["El invernadero elegido no existe."]);

        var activeStatus = await context.LotStatuses.FirstOrDefaultAsync(s => s.Name == LotStatusNames.Activo, ct);
        if (activeStatus is null)
            return new NewLotResult(null, ["Falta el estado de lote ACTIVO en el catálogo."]);

        // Plantas recién sembradas: etapa de GDD 0 y "En desarrollo"; el flujo diario las va actualizando.
        var stage = await PlantEvaluationService.ResolvePhenologicalStageAsync(context, input.CropTypeId, 0m, ct);
        var developing = await context.CommercialStages.FirstOrDefaultAsync(s => s.Name == CommercialStageNames.EnDesarrollo, ct);

        var now = DateTime.UtcNow;
        var goals = NewLotRules.Goals(input);
        var lot = new Lot
        {
            GreenhouseId = input.GreenhouseId,
            CropTypeId = input.CropTypeId,
            StatusId = activeStatus.Id,
            Name = name,
            SowingDate = input.SowingDate,
            PlantedAreaM2 = 0m, // no se usa: el lote se mide por plantas
            GridRows = input.Rows,
            GridColumns = input.Columns,
            BabyLeafGoalPercent = goals.BabyLeafGoalPercent,
            MaxLossPercent = goals.MaxLossPercent,
            CreatedAt = now
        };
        for (var row = 1; row <= input.Rows; row++)
        {
            for (var column = 1; column <= input.Columns; column++)
            {
                lot.Plants.Add(new Plant
                {
                    Row = row,
                    Column = column,
                    PhenologicalStageId = stage?.Id,
                    CommercialStageId = developing?.Id,
                    OperationalState = PlantOperationalState.Activa,
                    CreatedAtUtc = now
                });
            }
        }

        // Un solo SaveChanges: el lote y sus plantas se crean juntos o no se crea nada.
        context.Lots.Add(lot);
        await context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Lote {LotId} \"{Name}\" creado por {User}: {Rows}×{Columns} plantas, siembra {SowingDate}",
            lot.Id, name, user.Identity?.Name, input.Rows, input.Columns, input.SowingDate);

        return new NewLotResult(lot.Id, []);
    }
}
