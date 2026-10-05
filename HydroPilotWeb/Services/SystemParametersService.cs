using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models.Optimization;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.Optimization;

namespace HydroPilotWeb.Services;

/// <summary>Parámetros de rendimiento del sistema tal como se ven y editan en Ajustes &gt; Parámetros.</summary>
public sealed record YieldParameters(
    decimal? BabyLeafGrams,
    decimal? ConventionalGrams,
    decimal? BabyLeafPricePerKg,
    decimal? ConventionalPricePerKg,
    string Currency);

public sealed record ParametersUpdateResult(bool Succeeded, IReadOnlyList<string> Errors);

/// <summary>
/// Parámetros globales del sistema (Ajustes &gt; Parámetros). Por ahora, los de rendimiento:
/// <list type="bullet">
/// <item>Peso promedio por planta de cada destino: AppSettings.</item>
/// <item>Precio por kg de cada destino: catálogo de precios a nivel global, el mismo que usa
/// Optimización. Un precio específico de un lote, si existiera, sigue teniendo prioridad.</item>
/// </list>
/// Sin valor cargado quedan "sin definir": los precios demo del catálogo no cuentan como definidos.
/// Solo Administrador y Productor los editan; el resto los ve.
/// </summary>
public class SystemParametersService
{
    public static readonly IReadOnlyList<string> EditorRoles = ["Administrador", "Productor"];

    public const string BabyLeafGramsKey = "YieldBabyLeafGramsPerPlant";
    public const string ConventionalGramsKey = "YieldConventionalGramsPerPlant";

    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly SettingsService _settings;
    private readonly ILogger<SystemParametersService> _logger;

    public SystemParametersService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        SettingsService settings,
        ILogger<SystemParametersService> logger)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _logger = logger;
    }

    public static bool CanEdit(ClaimsPrincipal user) => EditorRoles.Any(user.IsInRole);

    /// <summary>Peso promedio por planta de cada destino; null = sin definir.</summary>
    public async Task<YieldReference> GetYieldReferenceAsync(CancellationToken ct = default) => new(
        await _settings.GetDecimalAsync(BabyLeafGramsKey, ct),
        await _settings.GetDecimalAsync(ConventionalGramsKey, ct));

    public async Task<YieldParameters> GetYieldParametersAsync(CancellationToken ct = default)
    {
        var reference = await GetYieldReferenceAsync(ct);

        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await ActivePriceRows(context, Today)
            .AsNoTracking()
            .Where(c => c.Source != CostPriceResolver.DemoSource)
            .ToListAsync(ct);

        // El global manda; un precio de cultivo cargado antes de esta pantalla se muestra si no hay global.
        decimal? Price(string destination) => rows
            .Where(r => r.Destination == destination)
            .OrderBy(r => r.CropTypeId == null ? 0 : 1)
            .ThenByDescending(r => r.ValidFrom)
            .ThenByDescending(r => r.Id)
            .FirstOrDefault()?.Value;

        return new YieldParameters(
            reference.BabyLeafGramsPerPlant,
            reference.ConventionalGramsPerPlant,
            Price(OptimizationContractDestination.BabyLeaf),
            Price(OptimizationContractDestination.Conventional),
            rows.FirstOrDefault()?.Currency ?? "ARS");
    }

    public async Task<ParametersUpdateResult> UpdateYieldParametersAsync(
        YieldReferenceInput input,
        ClaimsPrincipal user,
        CancellationToken ct = default)
    {
        if (!CanEdit(user))
            return new ParametersUpdateResult(false, ["Solo Administrador y Productor pueden editar los parámetros del sistema."]);

        var errors = YieldReferenceRules.Validate(input);
        if (errors.Count > 0)
            return new ParametersUpdateResult(false, errors);

        var normalized = YieldReferenceRules.Normalize(input);

        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        await SetPricePerKgAsync(context, OptimizationContractDestination.BabyLeaf, normalized.BabyLeafPricePerKg, ct);
        await SetPricePerKgAsync(context, OptimizationContractDestination.Conventional, normalized.ConventionalPricePerKg, ct);
        await context.SaveChangesAsync(ct);

        await _settings.SetDecimalAsync(BabyLeafGramsKey, normalized.BabyLeafGrams, ct);
        await _settings.SetDecimalAsync(ConventionalGramsKey, normalized.ConventionalGrams, ct);

        _logger.LogInformation(
            "Parámetros de rendimiento actualizados por {User}: Baby Leaf {BabyLeaf} g y {BabyLeafPrice}/kg, convencional {Conventional} g y {ConventionalPrice}/kg",
            user.Identity?.Name, normalized.BabyLeafGrams, normalized.BabyLeafPricePerKg,
            normalized.ConventionalGrams, normalized.ConventionalPricePerKg);

        return new ParametersUpdateResult(true, []);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>Precios por kg vigentes hoy que no son de un lote (globales o de cultivo, demo incluidos).</summary>
    private static IQueryable<CostPriceCatalog> ActivePriceRows(HydroPilotDbContext context, DateOnly today) =>
        context.CostPriceCatalogs.Where(c => c.LotId == null
                                             && c.Item == OptimizationContractCostItem.PricePerKg
                                             && c.ValidFrom <= today
                                             && (c.ValidUntil == null || c.ValidUntil >= today));

    /// <summary>
    /// Deja vigente desde hoy el precio global del destino y cierra ayer los otros vigentes (demo o
    /// de cultivo) para que valga en todo el sistema, también en Optimización; el histórico queda.
    /// Una corrección del mismo día se hace en el lugar. Vacío = sin definir: se cierran los
    /// cargados a mano, pero los demo se dejan (Optimización los sigue usando, etiquetados).
    /// </summary>
    private static async Task SetPricePerKgAsync(
        HydroPilotDbContext context, string destination, decimal? value, CancellationToken ct)
    {
        var today = Today;
        var active = await ActivePriceRows(context, today)
            .Where(c => c.Destination == destination)
            .ToListAsync(ct);

        var manualGlobal = active.Where(c => c.CropTypeId == null && c.Source == CostPriceResolver.ManualSource).ToList();
        if (value is { } unchanged && active.Count == 1 && manualGlobal.Count == 1 && manualGlobal[0].Value == unchanged)
            return;

        var editedToday = value is null ? null : manualGlobal.FirstOrDefault(c => c.ValidFrom == today);
        var toClose = value is null ? active.Where(c => c.Source != CostPriceResolver.DemoSource) : active;
        foreach (var row in toClose.Where(r => r != editedToday))
            row.ValidUntil = today.AddDays(-1);

        if (value is not { } price)
            return;

        if (editedToday is not null)
        {
            editedToday.Value = price;
            editedToday.ValidUntil = null;
            return;
        }

        context.CostPriceCatalogs.Add(new CostPriceCatalog
        {
            Destination = destination,
            Item = OptimizationContractCostItem.PricePerKg,
            Value = price,
            Currency = active.FirstOrDefault()?.Currency ?? "ARS",
            ValidFrom = today,
            Source = CostPriceResolver.ManualSource,
        });
    }
}
