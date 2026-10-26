using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Optimization;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.Optimization;
using HydroPilotWeb.Services.SensorFaults;

namespace HydroPilotWeb.Services;

/// <summary>Parámetros de rendimiento del sistema tal como se ven y editan en Ajustes &gt; Parámetros.</summary>
public sealed record YieldParameters(
    decimal? BabyLeafGrams,
    decimal? ConventionalGrams,
    decimal? BabyLeafPricePerKg,
    decimal? ConventionalPricePerKg,
    string Currency);

public sealed record ParametersUpdateResult(bool Succeeded, IReadOnlyList<string> Errors);

/// <summary>Cada cuántos días se chequea (calibra) un tipo de sensor; null = sin chequeo programado.</summary>
public sealed record SensorCheckInterval(string SensorType, int? Days);

/// <summary>
/// Parámetros globales del sistema (Ajustes &gt; Parámetros):
/// <list type="bullet">
/// <item>Peso promedio por planta de cada destino: AppSettings.</item>
/// <item>Precio por kg de cada destino: catálogo de precios a nivel global. Un precio específico de un lote,
/// si existiera, sigue teniendo prioridad.</item>
/// <item>Precio de nutrientes por litro (gastos del sistema de Simulación): AppSettings.</item>
/// <item>Días entre chequeos de cada tipo de sensor (dashboard de Invernadero): AppSettings.</item>
/// <item>Rango normal y salto máximo por tipo de sensor (errores de sensores): AppSettings.</item>
/// </list>
/// Sin valor cargado quedan "sin definir": los precios demo del catálogo no cuentan como definidos.
/// Solo Administrador y Productor los editan; el resto los ve.
/// </summary>
public class SystemParametersService
{
    public static readonly IReadOnlyList<string> EditorRoles = ["Administrador", "Productor"];

    public const string BabyLeafGramsKey = "YieldBabyLeafGramsPerPlant";
    public const string ConventionalGramsKey = "YieldConventionalGramsPerPlant";
    public const string NutrientPricePerLiterKey = "NutrientPricePerLiter";
    public const decimal MaxNutrientPricePerLiter = 10_000_000m;

    /// <summary>Días entre chequeos de un tipo de sensor: AppSettings "SensorCheckDays:{tipo}" (ej. "SensorCheckDays:pH").</summary>
    public const string SensorCheckDaysKeyPrefix = "SensorCheckDays:";
    public const int MaxSensorCheckDays = 365;

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

    /// <summary>
    /// Precio por kg de cada destino vigente para un lote a una fecha (lote &gt; cultivo &gt; global), sin
    /// los precios demo: si el usuario no cargó uno, queda sin definir.
    /// </summary>
    public static async Task<YieldPrices> ResolveLotPricesAsync(
        HydroPilotDbContext context, Lot lot, DateOnly date, CancellationToken ct = default)
    {
        var rows = (await CostPriceResolver.ResolveActiveAsync(context, lot, date, ct))
            .Where(r => r.Item == OptimizationContractCostItem.PricePerKg)
            .ToList();
        if (rows.Count == 0)
            return YieldPrices.None;

        var babyLeaf = rows.FirstOrDefault(r => r.Destination == OptimizationContractDestination.BabyLeaf);
        var conventional = rows.FirstOrDefault(r => r.Destination == OptimizationContractDestination.Conventional);
        return new YieldPrices(babyLeaf?.Value, conventional?.Value, (babyLeaf ?? conventional)!.Currency);
    }

    /// <summary>
    /// Guarda en el lote los pesos por planta vigentes y los precios a la fecha de cosecha: el ciclo
    /// cerrado se valoriza con ellos aunque los parámetros cambien después. No llama a SaveChanges.
    /// </summary>
    public async Task ApplyHarvestReferenceAsync(
        HydroPilotDbContext context, Lot lot, DateOnly harvestDate, CancellationToken ct = default)
    {
        var reference = await GetYieldReferenceAsync(ct);
        var prices = await ResolveLotPricesAsync(context, lot, harvestDate, ct);

        lot.HarvestBabyLeafGramsPerPlant = reference.BabyLeafGramsPerPlant;
        lot.HarvestConventionalGramsPerPlant = reference.ConventionalGramsPerPlant;
        lot.HarvestBabyLeafPricePerKg = prices.BabyLeafPerKg;
        lot.HarvestConventionalPricePerKg = prices.ConventionalPerKg;
        lot.HarvestPriceCurrency = prices.BabyLeafPerKg is null && prices.ConventionalPerKg is null ? null : prices.Currency;
    }

    /// <summary>Pesos y precios guardados en un lote cerrado (null los que no se guardaron).</summary>
    public static (YieldReference Reference, YieldPrices Prices) HarvestReference(Lot lot) => (
        new YieldReference(lot.HarvestBabyLeafGramsPerPlant, lot.HarvestConventionalGramsPerPlant),
        new YieldPrices(lot.HarvestBabyLeafPricePerKg, lot.HarvestConventionalPricePerKg,
            lot.HarvestPriceCurrency ?? YieldPrices.None.Currency));

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
            "Parámetros de rendimiento actualizados por {User}: Baby Leaf {BabyLeaf} g y {BabyLeafPrice}/kg, adulta {Conventional} g y {ConventionalPrice}/kg",
            user.Identity?.Name, normalized.BabyLeafGrams, normalized.BabyLeafPricePerKg,
            normalized.ConventionalGrams, normalized.ConventionalPricePerKg);

        return new ParametersUpdateResult(true, []);
    }

    /// <summary>Precio de los nutrientes por litro (en la moneda de los precios); null = sin definir.</summary>
    public Task<decimal?> GetNutrientPricePerLiterAsync(CancellationToken ct = default) =>
        _settings.GetDecimalAsync(NutrientPricePerLiterKey, ct);

    /// <summary>Guarda el precio de nutrientes por litro (vacío = sin definir). Solo Administrador y Productor.</summary>
    public async Task<ParametersUpdateResult> UpdateNutrientPricePerLiterAsync(
        decimal? pricePerLiter,
        ClaimsPrincipal user,
        CancellationToken ct = default)
    {
        if (!CanEdit(user))
            return new ParametersUpdateResult(false, ["Solo Administrador y Productor pueden editar los parámetros del sistema."]);
        if (pricePerLiter is { } price && (price <= 0m || price > MaxNutrientPricePerLiter))
            return new ParametersUpdateResult(false, ["El precio de nutrientes por litro tiene que ser mayor a 0."]);

        await _settings.SetDecimalAsync(NutrientPricePerLiterKey, pricePerLiter, ct);
        _logger.LogInformation("Precio de nutrientes actualizado por {User}: {Price}/L", user.Identity?.Name, pricePerLiter);
        return new ParametersUpdateResult(true, []);
    }

    /// <summary>Días entre chequeos de cada tipo de sensor (catálogo SensorTypes); null = sin chequeo programado.</summary>
    public async Task<IReadOnlyList<SensorCheckInterval>> GetSensorCheckIntervalsAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var types = await context.SensorTypes.AsNoTracking().OrderBy(t => t.Id).Select(t => t.Name).ToListAsync(ct);
        var keys = types.Select(SensorCheckDaysKey).ToList();
        var values = await context.AppSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        return types
            .Select(type => new SensorCheckInterval(type,
                values.TryGetValue(SensorCheckDaysKey(type), out var value)
                && int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var days)
                    ? days
                    : null))
            .ToList();
    }

    /// <summary>Guarda los días entre chequeos por tipo de sensor (vacío = sin chequeo). Solo Administrador y Productor.</summary>
    public async Task<ParametersUpdateResult> UpdateSensorCheckIntervalsAsync(
        IReadOnlyList<SensorCheckInterval> intervals,
        ClaimsPrincipal user,
        CancellationToken ct = default)
    {
        if (!CanEdit(user))
            return new ParametersUpdateResult(false, ["Solo Administrador y Productor pueden editar los parámetros del sistema."]);

        var errors = intervals
            .Where(i => i.Days is { } days && (days < 1 || days > MaxSensorCheckDays))
            .Select(i => $"Los días entre chequeos de {i.SensorType} tienen que estar entre 1 y {MaxSensorCheckDays}.")
            .ToList();
        if (errors.Count > 0)
            return new ParametersUpdateResult(false, errors);

        foreach (var interval in intervals)
            await _settings.SetDecimalAsync(SensorCheckDaysKey(interval.SensorType), interval.Days, ct);

        _logger.LogInformation("Chequeo de sensores actualizado por {User}: {Intervals}", user.Identity?.Name,
            string.Join(", ", intervals.Select(i => $"{i.SensorType} {i.Days?.ToString() ?? "sin definir"}")));
        return new ParametersUpdateResult(true, []);
    }

    public static string SensorCheckDaysKey(string sensorType) => SensorCheckDaysKeyPrefix + sensorType;

    /// <summary>Rango normal y salto máximo de pH, CE y temperatura (los cargados o los de fábrica).</summary>
    public async Task<IReadOnlyList<SensorFaultLimits>> GetSensorFaultLimitsAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var limits = await SensorFaultRules.LoadLimitsAsync(context, ct);
        return SensorFaultRules.Defaults.Select(d => limits[d.SensorType]).ToList();
    }

    /// <summary>Guarda el rango normal y el salto máximo por tipo de sensor. Solo Administrador y Productor.</summary>
    public async Task<ParametersUpdateResult> UpdateSensorFaultLimitsAsync(
        IReadOnlyList<SensorFaultLimits> limits,
        ClaimsPrincipal user,
        CancellationToken ct = default)
    {
        if (!CanEdit(user))
            return new ParametersUpdateResult(false, ["Solo Administrador y Productor pueden editar los parámetros del sistema."]);

        var errors = new List<string>();
        foreach (var limit in limits)
        {
            if (SensorFaultRules.Defaults.All(d => d.SensorType != limit.SensorType))
            {
                errors.Add($"{limit.SensorType} no tiene límites de errores de sensor.");
                continue;
            }
            if (limit.Min >= limit.Max)
                errors.Add($"En {limit.SensorType}, el mínimo normal tiene que ser menor que el máximo.");
            if (limit.Jump <= 0)
                errors.Add($"En {limit.SensorType}, el salto máximo tiene que ser mayor que 0.");
            if (TelemetryValidationService.FindBand(limit.SensorType) is { } band
                && (limit.Min < band.PhysicalMin || limit.Max > band.PhysicalMax))
                errors.Add($"En {limit.SensorType}, el rango normal tiene que estar entre {SensorFaultRules.Num(band.PhysicalMin)} y {SensorFaultRules.Num(band.PhysicalMax)}.");
        }
        if (errors.Count > 0)
            return new ParametersUpdateResult(false, errors);

        foreach (var limit in limits)
        {
            await _settings.SetDecimalAsync(SensorFaultRules.MinKey(limit.SensorType), limit.Min, ct);
            await _settings.SetDecimalAsync(SensorFaultRules.MaxKey(limit.SensorType), limit.Max, ct);
            await _settings.SetDecimalAsync(SensorFaultRules.JumpKey(limit.SensorType), limit.Jump, ct);
        }

        _logger.LogInformation("Límites de errores de sensores actualizados por {User}: {Limits}", user.Identity?.Name,
            string.Join(", ", limits.Select(l => $"{l.SensorType} {l.Min}–{l.Max} salto {l.Jump}")));
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
    /// de cultivo) para que valga en todo el sistema; el histórico queda.
    /// Una corrección del mismo día se hace en el lugar. Vacío = sin definir: se cierran los
    /// cargados a mano, pero los demo se dejan.
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
