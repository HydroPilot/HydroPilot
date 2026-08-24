using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

/// <summary>
/// Registro de reglas de anomalía por cultivo (plan 15 / ANO-01). Es la tabla de
/// reglas del módulo ("si hace falta" en el contrato de integración): registra la
/// política estable de cada regla (qué sensor, cuántas consecutivas abren episodio,
/// cooldown, activa o pendiente) dejando el valor agronómico visible y configurable.
///
/// Las bandas operativas de pH y CE NO se duplican acá: se resuelven en evaluación
/// desde el dominio (CropType.OptimalPh* y PhenologicalStage.Ec*) para no quedar
/// desincronizadas con los catálogos de lotes (Source = "crop-config"/"stage-config").
/// Temperatura y humedad no tienen umbral aprobado: su fila queda IsActive=false con
/// Source="pendiente" y la UI la muestra como pendiente; nunca disparan un episodio.
/// </summary>
public class AnomalyRuleCatalog
{
    public int Id { get; set; }

    public int CropTypeId { get; set; }

    /// <summary>Código estable de regla (AnomalyContract.Type*).</summary>
    [Required]
    [MaxLength(60)]
    public string Code { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Tipo de sensor al que aplica (coincide con SensorType.Name del catálogo IoT).</summary>
    [Required]
    [MaxLength(50)]
    public string SensorTypeName { get; set; } = string.Empty;

    /// <summary>Lecturas consecutivas fuera de banda que abren un episodio crítico (arranque 2).</summary>
    public int ConsecutiveToOpen { get; set; } = 2;

    /// <summary>Cooldown en minutos tras resolver un episodio antes de abrir uno nuevo de la misma regla.</summary>
    public int CooldownMinutes { get; set; } = 30;

    public bool IsActive { get; set; } = true;

    /// <summary>Origen de la configuración: crop-config | stage-config | baseline | pendiente.</summary>
    [Required]
    [MaxLength(30)]
    public string Source { get; set; } = "crop-config";

    [MaxLength(300)]
    public string? Notes { get; set; }

    /// <summary>Rango físico de referencia (solo divulgativo: la validez física ya la resuelve IoT).</summary>
    public decimal? PhysicalMin { get; set; }
    public decimal? PhysicalMax { get; set; }

    /// <summary>
    /// Banda operativa explícita (baseline). null para pH/CE: se resuelve en evaluación
    /// desde CropType/etapa fenológica; para temperatura/humedad queda null porque el
    /// umbral aprobado es pendiente.
    /// </summary>
    public decimal? OperationalMin { get; set; }
    public decimal? OperationalMax { get; set; }
    public decimal? TargetValue { get; set; }

    public CropType? CropType { get; set; }
}