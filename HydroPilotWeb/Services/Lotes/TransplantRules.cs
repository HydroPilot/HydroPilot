namespace HydroPilotWeb.Services.Lotes;

/// <summary>Planta del circuito denso (ruta Baby Leaf) con el score de su última evaluación.</summary>
public sealed record TransplantCandidate(int PlantId, decimal? Score);

/// <summary>
/// Propuesta de trasplante: las que pasan a Adulta (las de score bajo primero, después el excedente sobre la meta Baby
/// Leaf) hasta llenar los huecos de Adultas. NoRoom = las que deberían pasar y no entran: quedan en el denso y, si no
/// llegan a Baby Leaf, se descartan al cerrar la ventana.
/// </summary>
public sealed record TransplantProposal(IReadOnlyList<int> PlantIds, int LowScore, int OverGoal, int NoRoom)
{
    public static readonly TransplantProposal None = new([], 0, 0, 0);

    public int Wanted => PlantIds.Count + NoRoom;
}

/// <summary>
/// Trasplante a Adultas (lógica pura; la usan Lotes, Simulación y el entorno de simulación):
/// <list type="bullet">
/// <item>Desde el GDD de decisión (dentro de la ventana Baby Leaf, cuando el sistema ya clasifica por score) y hasta que
/// la ventana cierra.</item>
/// <item>Van a Adulta las que no llegan a Baby Leaf (score bajo el mínimo de candidata) y el excedente sobre la meta
/// Baby Leaf del ciclo (las de menor score), si entran en los huecos libres de Adultas.</item>
/// </list>
/// </summary>
public static class TransplantRules
{
    public static bool IsDue(decimal lotGdd, decimal decisionGdd, decimal windowCloseGdd) =>
        lotGdd >= decisionGdd && lotGdd < windowCloseGdd;

    /// <param name="babyLeafRoute">Plantas activas que siguen en el circuito denso.</param>
    /// <param name="totalPlants">Plantas del lote (la meta Baby Leaf es un % de todas).</param>
    /// <param name="alreadyBabyLeaf">Ya cosechadas como Baby Leaf (cuentan para la meta).</param>
    /// <param name="capacity">Huecos libres de Adultas para este lote.</param>
    public static TransplantProposal Propose(
        IReadOnlyList<TransplantCandidate> babyLeafRoute,
        int totalPlants,
        decimal? goalPercent,
        int capacity,
        decimal scoreMinCandidate,
        int alreadyBabyLeaf = 0)
    {
        // Sin score todavía no se sabe: se queda en el denso.
        var low = babyLeafRoute.Where(p => p.Score is { } s && s < scoreMinCandidate).OrderBy(p => p.Score).ToList();
        var good = babyLeafRoute.Where(p => p.Score is null || p.Score >= scoreMinCandidate)
            .OrderByDescending(p => p.Score ?? decimal.MaxValue)
            .ToList();
        var target = goalPercent is { } goal
            ? Math.Max(0, (int)Math.Round(goal / 100m * totalPlants, MidpointRounding.AwayFromZero) - alreadyBabyLeaf)
            : good.Count;
        var overGoal = good.Skip(target).OrderBy(p => p.Score).ToList();

        var wanted = low.Concat(overGoal).ToList();
        var chosen = wanted.Take(Math.Max(0, capacity)).Select(p => p.PlantId).ToList();
        return new TransplantProposal(chosen, low.Count, overGoal.Count, wanted.Count - chosen.Count);
    }

    /// <summary>Huecos libres de un circuito, fila por fila.</summary>
    public static IEnumerable<(int Row, int Column)> FreeHoles(int rows, int columns, IReadOnlySet<(int Row, int Column)> taken)
    {
        for (var row = 1; row <= rows; row++)
        {
            for (var column = 1; column <= columns; column++)
            {
                if (!taken.Contains((row, column)))
                    yield return (row, column);
            }
        }
    }
}
