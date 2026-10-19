using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;

namespace HydroPilotWeb.Services;

/// <summary>
/// Acceso a settings genéricos (key-value) persistidos en la base.
/// </summary>
public class SettingsService
{
    public const string WeatherFetchEnabledKey = "WeatherFetchEnabled";
    public const string WeatherDailyLimitDisabledKey = "WeatherDailyLimitDisabled";

    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;

    public SettingsService(IDbContextFactory<HydroPilotDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<bool> GetBoolAsync(string key, bool defaultValue = false, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var setting = await context.AppSettings.FindAsync([key], ct);
        return setting is not null && bool.TryParse(setting.Value, out var value) ? value : defaultValue;
    }

    public async Task SetBoolAsync(string key, bool value, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var setting = await context.AppSettings.FindAsync([key], ct);

        if (setting is null)
        {
            context.AppSettings.Add(new AppSetting { Key = key, Value = value.ToString() });
        }
        else
        {
            setting.Value = value.ToString();
        }

        await context.SaveChangesAsync(ct);
    }

    public async Task<int> GetIntAsync(string key, int defaultValue, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var setting = await context.AppSettings.FindAsync([key], ct);
        return setting is not null && int.TryParse(setting.Value, out var value) ? value : defaultValue;
    }

    public async Task<string?> GetStringAsync(string key, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        return (await context.AppSettings.FindAsync([key], ct))?.Value;
    }

    public async Task SetStringAsync(string key, string value, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var setting = await context.AppSettings.FindAsync([key], ct);
        if (setting is null)
            context.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else
            setting.Value = value;
        await context.SaveChangesAsync(ct);
    }

    /// <summary>Decimal guardado con cultura invariante; null si no está definido.</summary>
    public async Task<decimal?> GetDecimalAsync(string key, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var setting = await context.AppSettings.FindAsync([key], ct);
        return setting is not null
               && decimal.TryParse(setting.Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>Guarda el decimal con cultura invariante; null borra el setting (queda sin definir).</summary>
    public async Task SetDecimalAsync(string key, decimal? value, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var setting = await context.AppSettings.FindAsync([key], ct);

        if (value is not { } number)
        {
            if (setting is not null)
                context.AppSettings.Remove(setting);
        }
        else if (setting is null)
        {
            context.AppSettings.Add(new AppSetting { Key = key, Value = number.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
        else
        {
            setting.Value = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        await context.SaveChangesAsync(ct);
    }
}
