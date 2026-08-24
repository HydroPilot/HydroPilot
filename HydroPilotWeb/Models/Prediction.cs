using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

public class Prediction
{
    public int Id { get; set; }

    public int LotId { get; set; }

    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    public DateOnly? EstimatedHarvestDate { get; set; }

    public decimal AccumulatedGdd { get; set; }

    public decimal? EstimatedYield { get; set; }

    [MaxLength(50)]
    public string ModelVersion { get; set; } = "gdd-v1";

    /// <summary>
    /// Fecha de cálculo simulada/efectiva (F-03). Null en predicciones legadas.
    /// La precisión (F-05) elige la última predicción con AsOfDate anterior a la
    /// cosecha, sin usar datos posteriores a esa fecha.
    /// </summary>
    public DateOnly? AsOfDate { get; set; }

    /// <summary>Resumen de la fuente de datos del cálculo (sensor/pronóstico/fallback/mock).</summary>
    [MaxLength(60)]
    public string? DataSource { get; set; }

    /// <summary>Cobertura de días observados sobre el período siembra→AsOfDate (0-100).</summary>
    public decimal? CoveragePercent { get; set; }

    public Lot? Lot { get; set; }
}
