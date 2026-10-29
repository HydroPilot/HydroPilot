using HydroPilotWeb.Data;
using HydroPilotWeb.Models.Notifications;
using HydroPilotWeb.Services.Dashboard;
using HydroPilotWeb.Services.SensorFaults;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Services.Notifications;

/// <summary>
/// Filtros del historial de alertas: tipo y severidad (NotificationTypes / NotificationSeverities), abiertas o resueltas
/// (null = las dos), lote, fechas (del invernadero, "hasta" incluido), texto en el título o el mensaje, y la página.
/// </summary>
public sealed record AlertFilter(
    string? Type = null,
    string? Severity = null,
    bool? Open = null,
    int? LotId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Text = null,
    int Page = 1);

/// <summary>Una alerta como la muestra la vista Alertas; Link: a dónde lleva (como en el dashboard).</summary>
public sealed record AlertHistoryRow(
    int Id,
    string Type,
    string Severity,
    string Title,
    string Message,
    string? LotName,
    DateTime CreatedAtLocal,
    DateTime? ResolvedAtLocal,
    bool IsResolved,
    string Link);

/// <summary>Una página del historial: sus filas, cuántas hay con esos filtros y cuántas de esas siguen abiertas.</summary>
public sealed record AlertHistoryPage(IReadOnlyList<AlertHistoryRow> Rows, int Total, int OpenCount, int Page, int PageSize)
{
    public int Pages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

public sealed record AlertLotOption(int Id, string Name);

/// <summary>Nombre de cada tipo y severidad de alerta en la vista.</summary>
public static class AlertLabels
{
    /// <summary>Los tipos que se listan, en el orden del filtro (sin la prueba de email de Admin).</summary>
    public static readonly IReadOnlyList<string> Types =
    [
        NotificationTypes.DesbalanceQuimico,
        NotificationTypes.ErrorSensor,
        NotificationTypes.FallaDosificacion,
        NotificationTypes.NodoDesconectado,
        NotificationTypes.CosechaInminente,
        NotificationTypes.Info,
    ];

    public static readonly IReadOnlyList<string> Severities =
    [
        NotificationSeverities.Critica,
        NotificationSeverities.Alta,
        NotificationSeverities.Media,
        NotificationSeverities.Info,
    ];

    public static string Type(string type) => type switch
    {
        NotificationTypes.DesbalanceQuimico => "Solución fuera de banda",
        NotificationTypes.ErrorSensor => "Error de sensor",
        NotificationTypes.FallaDosificacion => "Control pausado",
        NotificationTypes.NodoDesconectado => "Pi desconectada",
        NotificationTypes.CosechaInminente => "Cosecha inminente",
        NotificationTypes.Info => "Información",
        _ => type,
    };

    public static string Severity(string severity) => severity switch
    {
        NotificationSeverities.Critica => "Crítica",
        NotificationSeverities.Alta => "Alta",
        NotificationSeverities.Media => "Media",
        _ => "Informativa",
    };
}

/// <summary>
/// Historial de todas las alertas del invernadero (vista Alertas): las de cada lote, de la Pi, de los sensores y del
/// control, abiertas y resueltas, más nuevas primero. Solo lectura; la bandeja de cada usuario es aparte.
/// </summary>
public sealed class AlertHistoryService
{
    public const int PageSize = 50;

    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;

    public AlertHistoryService(IDbContextFactory<HydroPilotDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>Lotes que tienen alguna alerta, para el filtro (en orden natural del nombre).</summary>
    public async Task<IReadOnlyList<AlertLotOption>> LotsAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var lots = await context.NotificationAlerts.AsNoTracking()
            .Where(a => a.LotId != null && a.Type != NotificationTypes.PruebaSistema)
            .Select(a => new { Id = a.LotId!.Value, a.Lot!.Name })
            .Distinct()
            .ToListAsync(ct);
        return lots
            .Select(l => new AlertLotOption(l.Id, string.IsNullOrWhiteSpace(l.Name) ? $"Lote {l.Id}" : l.Name))
            .OrderBy(l => l.Name, NaturalStringComparer.Instance)
            .ToList();
    }

    public async Task<AlertHistoryPage> SearchAsync(AlertFilter filter, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        // Fechas y horas del invernadero, como el dashboard.
        var timeZoneId = await context.Greenhouses.AsNoTracking().OrderBy(g => g.Id).Select(g => g.TimeZoneId).FirstOrDefaultAsync(ct);
        var zone = GddService.ResolveGreenhouseTimeZone(timeZoneId, []);
        DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        DateTime Utc(DateOnly day) => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone);

        var query = context.NotificationAlerts.AsNoTracking().Where(a => a.Type != NotificationTypes.PruebaSistema);
        if (!string.IsNullOrWhiteSpace(filter.Type))
            query = query.Where(a => a.Type == filter.Type);
        if (!string.IsNullOrWhiteSpace(filter.Severity))
            query = query.Where(a => a.Severity == filter.Severity);
        if (filter.Open is { } open)
            query = query.Where(a => a.IsResolved != open);
        if (filter.LotId is { } lotId)
            query = query.Where(a => a.LotId == lotId);
        if (filter.From is { } from)
        {
            var fromUtc = Utc(from);
            query = query.Where(a => a.CreatedAtUtc >= fromUtc);
        }
        if (filter.To is { } to)
        {
            var toUtc = Utc(to.AddDays(1));
            query = query.Where(a => a.CreatedAtUtc < toUtc);
        }
        if (filter.Text?.Trim() is { Length: > 0 } text)
            query = query.Where(a => a.Title.Contains(text) || a.Message.Contains(text));

        var total = await query.CountAsync(ct);
        var openCount = await query.CountAsync(a => !a.IsResolved, ct);
        var page = Math.Clamp(filter.Page, 1, Math.Max(1, (int)Math.Ceiling(total / (double)PageSize)));
        var rows = await query
            .OrderByDescending(a => a.CreatedAtUtc)
            .ThenByDescending(a => a.Id)
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .Select(a => new
            {
                a.Id, a.Type, a.Severity, a.Title, a.Message, a.LotId, LotName = a.Lot != null ? a.Lot.Name : null,
                a.Fingerprint, a.CreatedAtUtc, a.ResolvedAtUtc, a.IsResolved,
            })
            .ToListAsync(ct);

        return new AlertHistoryPage(
            rows.Select(a => new AlertHistoryRow(
                    a.Id, a.Type, a.Severity, a.Title, a.Message, a.LotName,
                    Local(a.CreatedAtUtc), a.ResolvedAtUtc is { } resolved ? Local(resolved) : null, a.IsResolved,
                    GreenhouseDashboardRules.AlertLink(a.Type, a.LotId, SensorFaultAlerts.CircuitId(a.Fingerprint))))
                .ToList(),
            total, openCount, page, PageSize);
    }
}
