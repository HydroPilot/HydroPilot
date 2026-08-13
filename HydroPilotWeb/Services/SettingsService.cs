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
}
