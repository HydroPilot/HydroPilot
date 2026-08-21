namespace HydroPilotWeb.Components.Lotes;

/// <summary>
/// Paleta visual única de estados (plan 09): la tonalidad de "Candidata Baby Leaf"
/// es la misma en todas las vistas (amarillo oscuro / naranja claro). El texto y
/// el patrón no dependen solo del color (cada celda lleva etiqueta y tooltip).
/// </summary>
public static class CommercialStagePalette
{
    public sealed record CellStyle(string Background, string Color, string Label, string Title);

    public static CellStyle ForStage(string? stageName) => stageName switch
    {
        "En desarrollo" => new CellStyle("#2e9e4f", "#ffffff", "ED", "En desarrollo"),
        "Candidata Baby Leaf" => new CellStyle("#e8a33d", "#3d2800", "CAN", "Candidata Baby Leaf"),
        "Baby Leaf apta" => new CellStyle("#2d7dd2", "#ffffff", "BL", "Baby Leaf apta"),
        "Cosecha convencional" => new CellStyle("#8a4fd3", "#ffffff", "CC", "Cosecha convencional"),
        "Riesgo / fuera de ventana" => new CellStyle("#d64545", "#ffffff", "R", "Riesgo / fuera de ventana"),
        _ => new CellStyle("#b9c2cc", "#2b3440", "?", stageName ?? "Sin estado")
    };

    /// <summary>Posición configurada sin planta: gris claro. NO es una planta descartada.</summary>
    public static CellStyle Empty => new("#e4e7eb", "#8a94a0", "·", "Posición vacía");

    /// <summary>Planta descartada: gris oscuro atenuado con patrón, distinto de la vacía.</summary>
    public static CellStyle Discarded => new(
        "repeating-linear-gradient(45deg,#8a94a0,#8a94a0 4px,#9aa3ad 4px,#9aa3ad 8px)",
        "#2b3440", "✕", "Planta descartada");
}