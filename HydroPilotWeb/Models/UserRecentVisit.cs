namespace HydroPilotWeb.Models;

/// <summary>
/// Última vez que un usuario abrió un lote en una vista (Lotes u Optimización), para "Últimos accedidos"
/// del dashboard. Una fila por usuario + vista + lote (o circuito, en Optimización sin lote); se actualiza la fecha.
/// </summary>
public class UserRecentVisit
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Vista: RecentVisitKinds.Lotes u Optimization.</summary>
    public string Kind { get; set; } = string.Empty;

    public int? LotId { get; set; }
    public Lot? Lot { get; set; }

    /// <summary>Circuito abierto en Optimización (sin FK: si el circuito ya no existe, la visita se ignora).</summary>
    public int? CircuitId { get; set; }

    public DateTime VisitedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class RecentVisitKinds
{
    public const string Lotes = "lotes";
    public const string Optimization = "optimization";
}
