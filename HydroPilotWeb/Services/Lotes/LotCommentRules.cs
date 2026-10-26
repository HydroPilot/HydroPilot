using System.Security.Claims;
using HydroPilotWeb.Models;

namespace HydroPilotWeb.Services.Lotes;

/// <summary>
/// Reglas de los comentarios de un lote: quién comenta y quién borra, cómo se limpia el texto y con
/// qué nombre queda firmado.
/// </summary>
public static class LotCommentRules
{
    /// <summary>Comentan todos los roles asignados (los mismos que entran a la vista Lotes).</summary>
    public static readonly IReadOnlyList<string> CommenterRoles = ["Administrador", "Productor", "Operador", "Soporte Técnico"];

    public static int? UserId(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirst("user_id")?.Value, out var id) ? id : null;

    public static bool CanComment(ClaimsPrincipal user) =>
        UserId(user) is not null && CommenterRoles.Any(user.IsInRole);

    /// <summary>Cada uno borra los suyos; el Administrador, cualquiera.</summary>
    public static bool CanDelete(int? authorId, ClaimsPrincipal user) =>
        user.IsInRole("Administrador") || (authorId is not null && authorId == UserId(user));

    /// <summary>Sin espacios al principio ni al final y con saltos de línea uniformes.</summary>
    public static string Normalize(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    /// <summary>Error del texto ya normalizado, o null si se puede guardar.</summary>
    public static string? Validate(string normalized) =>
        normalized.Length == 0 ? "Escribí un comentario."
        : normalized.Length > LotComment.TextMaxLength ? $"El comentario puede tener hasta {LotComment.TextMaxLength} caracteres."
        : null;

    /// <summary>Firma: nombre y apellido del usuario; si no tiene, el nombre de la sesión o el email.</summary>
    public static string AuthorName(User? dbUser, ClaimsPrincipal user)
    {
        var fullName = $"{dbUser?.GivenName} {dbUser?.Surname}".Trim();
        var name = (new[] { fullName, user.Identity?.Name, dbUser?.Email, user.FindFirst(ClaimTypes.Email)?.Value }
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "Usuario").Trim();
        return name.Length > 256 ? name[..256] : name;
    }

    /// <summary>Iniciales para el avatar ("María García" → "MG", "admin@x" → "A").</summary>
    public static string Initials(string authorName)
    {
        var words = authorName.Split([' ', '.', '_', '-', '@'], StringSplitOptions.RemoveEmptyEntries);
        var source = authorName.Contains('@') ? words.Take(1) : words.Take(2);
        var initials = string.Concat(source.Select(w => char.ToUpperInvariant(w[0])));
        return initials.Length > 0 ? initials : "?";
    }
}
