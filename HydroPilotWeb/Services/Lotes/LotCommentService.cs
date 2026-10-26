using System.Security.Claims;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Services.Lotes;

/// <summary>Comentario para el modal, con la hora en la zona del invernadero.</summary>
public sealed record LotCommentDto(int Id, string AuthorName, string Text, DateTime CreatedAtLocal, bool CanDelete);

/// <summary>Comentarios del lote (más nuevos arriba) y la hora actual en la misma zona, para "hace 5 min".</summary>
public sealed record LotCommentList(IReadOnlyList<LotCommentDto> Items, DateTime NowLocal);

public sealed record LotCommentResult(bool Succeeded, string? Error = null);

/// <summary>Comentarios libres a nivel lote: listar, contar, agregar y borrar.</summary>
public class LotCommentService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly ILogger<LotCommentService> _logger;

    public LotCommentService(IDbContextFactory<HydroPilotDbContext> dbFactory, ILogger<LotCommentService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<int> CountAsync(int lotId, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        return await context.LotComments.CountAsync(c => c.LotId == lotId, ct);
    }

    public async Task<LotCommentList> GetAsync(int lotId, ClaimsPrincipal user, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var timeZoneId = await context.Lots.AsNoTracking()
            .Where(l => l.Id == lotId)
            .Select(l => l.Greenhouse!.TimeZoneId)
            .FirstOrDefaultAsync(ct);
        var zone = GddService.ResolveGreenhouseTimeZone(timeZoneId, []);

        var rows = await context.LotComments.AsNoTracking()
            .Where(c => c.LotId == lotId)
            .OrderByDescending(c => c.CreatedAtUtc).ThenByDescending(c => c.Id)
            .Select(c => new { c.Id, c.AuthorName, c.Text, c.CreatedAtUtc, c.UserId })
            .ToListAsync(ct);

        var items = rows
            .Select(c => new LotCommentDto(
                c.Id, c.AuthorName, c.Text, Local(c.CreatedAtUtc, zone), LotCommentRules.CanDelete(c.UserId, user)))
            .ToList();
        return new LotCommentList(items, Local(DateTime.UtcNow, zone));
    }

    public async Task<LotCommentResult> AddAsync(int lotId, string? text, ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (!LotCommentRules.CanComment(user) || LotCommentRules.UserId(user) is not { } userId)
            return new LotCommentResult(false, "Tu usuario no puede comentar.");

        var normalized = LotCommentRules.Normalize(text);
        if (LotCommentRules.Validate(normalized) is { } error)
            return new LotCommentResult(false, error);

        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        if (!await context.Lots.AnyAsync(l => l.Id == lotId, ct))
            return new LotCommentResult(false, "El lote no existe.");

        var author = await context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        context.LotComments.Add(new LotComment
        {
            LotId = lotId,
            UserId = author?.Id,
            AuthorName = LotCommentRules.AuthorName(author, user),
            Text = normalized,
            CreatedAtUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync(ct);

        _logger.LogInformation("Comentario agregado al lote {LotId} por el usuario {UserId}", lotId, userId);
        return new LotCommentResult(true);
    }

    public async Task<LotCommentResult> DeleteAsync(int commentId, ClaimsPrincipal user, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var comment = await context.LotComments.FirstOrDefaultAsync(c => c.Id == commentId, ct);
        if (comment is null)
            return new LotCommentResult(true); // ya no estaba
        if (!LotCommentRules.CanDelete(comment.UserId, user))
            return new LotCommentResult(false, "Solo el autor o un Administrador pueden borrar el comentario.");

        context.LotComments.Remove(comment);
        await context.SaveChangesAsync(ct);

        _logger.LogInformation("Comentario {CommentId} del lote {LotId} borrado por el usuario {UserId}",
            commentId, comment.LotId, LotCommentRules.UserId(user));
        return new LotCommentResult(true);
    }

    private static DateTime Local(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
}
