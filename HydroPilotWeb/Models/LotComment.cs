namespace HydroPilotWeb.Models;

/// <summary>Comentario libre de un usuario sobre un lote (vista Lotes → 💬).</summary>
public class LotComment
{
    public const int TextMaxLength = 1000;

    public int Id { get; set; }

    public int LotId { get; set; }
    public Lot? Lot { get; set; }

    /// <summary>Autor. Null si después se borró el usuario: el comentario queda con el nombre guardado.</summary>
    public int? UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Nombre del autor al comentar (nombre y apellido, o el email si no tiene).</summary>
    public string AuthorName { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
