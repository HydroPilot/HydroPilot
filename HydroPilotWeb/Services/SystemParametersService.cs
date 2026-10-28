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
/// Trasplante a Adultas y tamaño de Baby Leaf (Ajustes &gt; Parámetros). Siempre tienen valor: los de fábrica hasta
/// que se editan.
/// <list type="bullet">
/// <item>GDD de decisión: desde ahí se proponen las que pasan a Adulta (dentro de la ventana Baby Leaf).</item>
/// <item>Rendimiento laboral (plantas por hora por operario) y costo de la hora: costo por planta trasplantada.</item>
/// <item>Días de estrés: la Adulta frena después del trasplante y su corte se corre esos días.</item>
/// <item>Tamaño máximo de Baby Leaf (× el ideal): más grande, se pasó y ya no es Baby Leaf.</item>
/// </list>
/// </summary>
public sealed record TransplantParameters(
    decimal DecisionGdd,
    int PlantsPerHour,
    decimal LaborCostPerHour,
    int StressDays,
    decimal MaxSizeFactor)
{
    /// <summary>
    /// De fábrica (los de Ajustes &gt; Parámetros del 2026-10-28): decisión al abrir la ventana Baby Leaf (250 GDD), 800
    /// plantas por hora, $8.000 la hora, 3 días de estrés y 1,3 veces el tamaño ideal (provisorio hasta definirlo).
    /// </summary>
    public static readonly TransplantParameters Defaults = new(250m, 800, 8_000m, 3, 1.3m);

    /// <summary>Costo de mano de obra por planta trasplantada: costo de la hora ÷ plantas por hora.</summary>
    public decimal CostPerPlant => PlantsPerHour > 0 ? Math.Round(LaborCostPerHour / PlantsPerHour, 2) : 0m;
}

/// <summary>Precio por litro de cada solución nutritiva: A (macro) y B (micro); null = sin definir.</summary>
public sealed record NutrientPrices(decimal? SolutionAPerLiter, decimal? SolutionBPerLiter);

/// <summary>
/// Parámetros de fábrica: los de Ajustes &gt; Parámetros del 2026-10-28. La app los carga al arrancar donde falten
/// (DbInitializer: no pisa lo que ya se cambió) y el entorno de simulación los deja al regenerar el historial (los
/// precios por kg del trimestre actual y los de nutrientes; los trimestres anteriores siguen la misma suba).
/// </summary>
public static class ParameterDefaults
{
    public const decimal BabyLeafGramsPerPlant = 30m;
    public const decimal ConventionalGramsPerPlant = 280m;
    public const decimal BabyLeafPricePerKg = 25_000m;
    public const decimal ConventionalPricePerKg = 15_000m;
    public const decimal SolutionAPricePerLiter = 25_000m;
    public const decimal SolutionBPricePerLiter = 25_000m;

    /// <summary>Días entre chequeos de cada tipo de sensor.</summary>
    public static readonly IReadOnlyDictionary<string, int> SensorCheckDays = new Dictionary<string, int>
    {
        ["pH"] = 14,
        ["CE"] = 30,
        ["Temperatura"] = 90,
        ["Humedad"] = 90,
    };

    /// <summary>Lo que va en AppSettings (clave → valor), para cargarlo donde falte.</summary>
    public static IEnumerable<(string Key, decimal Value)> Settings()
    {
        yield return (SystemParametersService.BabyLeafGramsKey, BabyLeafGramsPerPlant);
        yield return (SystemParametersService.ConventionalGramsKey, ConventionalGramsPerPlant);
        yield return (SystemParametersService.SolutionAPricePerLiterKey, SolutionAPricePerLiter);
        yield return (SystemParametersService.SolutionBPricePerLiterKey, SolutionBPricePerLiter);
        foreach (var (type, days) in SensorCheckDays)
            yield return (SystemParametersService.SensorCheckDaysKeyPrefix + type, days);
    }
}

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
    public const string SolutionAPricePerLiterKey = "NutrientSolutionAPricePerLiter";
    public const string SolutionBPricePerLiterKey = "NutrientSolutionBPricePerLiter";
    public const decimal MaxNutrientPricePerLiter = 10_000_000m;

    /// <summary>Días entre chequeos de un tipo de sensor: AppSettings "SensorCheckDays:{tipo}" (ej. "SensorCheckDays:pH").</summary>
    public const string SensorCheckDaysKeyPrefix = "SensorCheckDays:";
    public const int MaxSensorCheckDays = 365;

    public const string DecisionGddKey = "Transplant:DecisionGdd";
    public const string PlantsPerHourKey = "Transplant:PlantsPerHour";
    public const string LaborCostPerHourKey = "Transplant:LaborCostPerHour";
    public const string StressDaysKey = "Transplant:StressDays";
    public const string MaxSizeFactorKey = "BabyLeaf:MaxSizeFactor";

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

    /// <summary>Precio por litro de la Solución A (macro) y de la B (micro), en la moneda de los precios; null = sin definir.</summary>
    public async Task<NutrientPrices> GetNutrientPricesAsync(CancellationToken ct = default) => new(
        await _settings.GetDecimalAsync(SolutionAPricePerLiterKey, ct),
        await _settings.GetDecimalAsync(SolutionBPricePerLiterKey, ct));

    /// <summary>Guarda el precio por litro de cada solución (vacío = sin definir). Solo Administrador y Productor.</summary>
    public async Task<ParametersUpdateResult> UpdateNutrientPricesAsync(
        NutrientPrices prices,
        ClaimsPrincipal user,
        CancellationToken ct = default)
    {
        if (!CanEdit(user))
            return new ParametersUpdateResult(false, ["Solo Administrador y Productor pueden editar los parámetros del sistema."]);
        var errors = new List<string>();
        if (prices.SolutionAPerLiter is { } a && (a <= 0m || a > MaxNutrientPricePerLiter))
            errors.Add("El precio por litro de la Solución A tiene que ser mayor a 0.");
        if (prices.SolutionBPerLiter is { } b && (b <= 0m || b > MaxNutrientPricePerLiter))
            errors.Add("El precio por litro de la Solución B tiene que ser mayor a 0.");
        if (errors.Count > 0)
            return new ParametersUpdateResult(false, errors);

        await _settings.SetDecimalAsync(SolutionAPricePerLiterKey, prices.SolutionAPerLiter, ct);
        await _settings.SetDecimalAsync(SolutionBPricePerLiterKey, prices.SolutionBPerLiter, ct);
        _logger.LogInformation("Precios de nutrientes actualizados por {User}: A {A}/L, B {B}/L",
            user.Identity?.Name, prices.SolutionAPerLiter, prices.SolutionBPerLiter);
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

    /// <summary>Trasplante y tamaño de Baby Leaf: los cargados o los de fábrica.</summary>
    public async Task<TransplantParameters> GetTransplantParametersAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        return await LoadTransplantAsync(context, ct);
    }

    /// <summary>Lo mismo con un contexto propio (flujo diario, trasplante, entorno de simulación).</summary>
    public static async Task<TransplantParameters> LoadTransplantAsync(HydroPilotDbContext context, CancellationToken ct = default)
    {
        string[] keys = [DecisionGddKey, PlantsPerHourKey, LaborCostPerHourKey, StressDaysKey, MaxSizeFactorKey];
        var values = await context.AppSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        decimal Value(string key, decimal fallback) =>
            values.TryGetValue(key, out var text)
            && decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;

        var d = TransplantParameters.Defaults;
        return new TransplantParameters(
            Value(DecisionGddKey, d.DecisionGdd),
            (int)Value(PlantsPerHourKey, d.PlantsPerHour),
            Value(LaborCostPerHourKey, d.LaborCostPerHour),
            (int)Value(StressDaysKey, d.StressDays),
            Value(MaxSizeFactorKey, d.MaxSizeFactor));
    }

    public static IReadOnlyList<string> ValidateTransplant(TransplantParameters p)
    {
        var errors = new List<string>();
        if (p.DecisionGdd is <= 0m or > 1_000m)
            errors.Add("El GDD de decisión tiene que estar entre 0 y 1.000.");
        if (p.PlantsPerHour is < 1 or > 100_000)
            errors.Add("Las plantas por hora tienen que estar entre 1 y 100.000.");
        if (p.LaborCostPerHour is < 0m or > 10_000_000m)
            errors.Add("El costo de la hora de trabajo no puede ser negativo.");
        if (p.StressDays is < 0 or > 30)
            errors.Add("Los días de estrés tienen que estar entre 0 y 30.");
        if (p.MaxSizeFactor is < 1m or > 3m)
            errors.Add("El tamaño máximo de Baby Leaf tiene que estar entre 1 y 3 veces el ideal.");
        return errors;
    }

    /// <summary>Guarda el trasplante y el tamaño de Baby Leaf. Solo Administrador y Productor.</summary>
    public async Task<ParametersUpdateResult> UpdateTransplantParametersAsync(
        TransplantParameters input, ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (!CanEdit(user))
            return new ParametersUpdateResult(false, ["Solo Administrador y Productor pueden editar los parámetros del sistema."]);
        var errors = ValidateTransplant(input);
        if (errors.Count > 0)
            return new ParametersUpdateResult(false, errors);

        await _settings.SetDecimalAsync(DecisionGddKey, input.DecisionGdd, ct);
        await _settings.SetDecimalAsync(PlantsPerHourKey, input.PlantsPerHour, ct);
        await _settings.SetDecimalAsync(LaborCostPerHourKey, input.LaborCostPerHour, ct);
        await _settings.SetDecimalAsync(StressDaysKey, input.StressDays, ct);
        await _settings.SetDecimalAsync(MaxSizeFactorKey, input.MaxSizeFactor, ct);
        _logger.LogInformation("Trasplante actualizado por {User}: {Parameters}", user.Identity?.Name, input);
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
