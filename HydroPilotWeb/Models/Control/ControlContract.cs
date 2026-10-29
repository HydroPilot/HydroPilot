namespace HydroPilotWeb.Models.Control;

/// <summary>
/// Vocabulario del control de pH y CE: modos del nodo, tipos de actuador, estados de un comando y su origen.
/// Los valores viajan tal cual en el contrato con la Raspberry Pi (notas/control/plan-control-ph-ce.md).
/// </summary>
public static class ControlContract
{
    // --- Modo del nodo ---
    public const string ModeOff = "OFF";
    public const string ModeManual = "MANUAL";
    public const string ModeAuto = "AUTO";

    // --- Tipo de actuador (qué hace sobre el tanque) ---
    public const string KindPhUp = "ph-up";
    public const string KindPhDown = "ph-down";
    /// <summary>Solución A (macro). Conserva el valor "nutrient" de cuando había una sola bomba de nutrientes.</summary>
    public const string KindNutrientA = "nutrient";
    /// <summary>Solución B (micro): bomba aparte, A y B no se mezclan concentradas.</summary>
    public const string KindNutrientB = "nutrient-b";
    public const string KindWater = "water";

    public const string UnitMl = "ml";
    public const string UnitLiters = "L";

    // --- Magnitud que corrige cada actuador ---
    public const string ParameterPh = AnomalyContract.SensorTypePh;
    public const string ParameterEc = AnomalyContract.SensorTypeCe;

    // --- Estado de un comando ---
    /// <summary>Creado; la Pi todavía no lo pidió.</summary>
    public const string StatusPending = "PENDING";
    /// <summary>La Pi lo pidió; falta su resultado.</summary>
    public const string StatusDelivered = "DELIVERED";
    public const string StatusExecuted = "EXECUTED";
    /// <summary>La Pi no lo ejecutó (vencido, actuador apagado o límite propio).</summary>
    public const string StatusRejected = "REJECTED";
    /// <summary>La Pi lo intentó y falló la bomba (puede haber aplicado una parte).</summary>
    public const string StatusFailed = "FAILED";
    /// <summary>Nadie lo pidió antes de vencer: nunca se ejecuta.</summary>
    public const string StatusExpired = "EXPIRED";
    /// <summary>Se entregó y no llegó resultado a tiempo: pudo haberse aplicado.</summary>
    public const string StatusUnconfirmed = "UNCONFIRMED";
    public const string StatusCancelled = "CANCELLED";

    // --- Origen ---
    public const string SourceManual = "MANUAL";
    public const string SourceAuto = "AUTO";
    public const string SourceEdge = "EDGE";

    /// <summary>Versión de la lógica que creó el comando (las dosis manuales no tienen motor).</summary>
    public const string ControllerManual = "manual";
    public const string ControllerAuto = "auto-v1";

    // --- Origen del nivel buscado de un lote ---
    public const string SetpointSourceStage = "STAGE";
    public const string SetpointSourceRecipe = "RECIPE";

    public const decimal DefaultReservoirLiters = 500m;
    public const decimal MaxReservoirLiters = 100_000m;

    /// <summary>CE del agua de red con que se diluye (mS/cm).</summary>
    public const decimal DefaultSourceWaterEc = 0.2m;

    /// <summary>Fuera de este pH la corrección de pH va antes que la de CE (rango crítico).</summary>
    public const decimal CriticalPhMin = 5.0m;
    public const decimal CriticalPhMax = 7.0m;

    /// <summary>
    /// Actuadores con que arranca cada nodo; límites pensados para un tanque de 500 L. Effect: cuánto mueve
    /// 1 ml de producto por litro de tanque (pH para pH Up/Down, mS/cm para las soluciones A y B); el agua diluye y no
    /// lleva efecto propio. Son estimaciones iniciales: se autocalibran con cada dosis. La B lleva ~40 % de lo de la A
    /// (proporción de la tabla del cultivo).
    /// </summary>
    public static readonly IReadOnlyList<ActuatorDefault> DefaultActuators =
    [
        new(KindPhUp, "Subir pH", UnitMl, 50m, 250m, 2.5m),
        new(KindPhDown, "Bajar pH", UnitMl, 50m, 250m, 2.5m),
        new(KindNutrientA, "Solución A", UnitMl, 500m, 2_000m, 0.3m),
        new(KindNutrientB, "Solución B", UnitMl, 200m, 800m, 0.3m),
        new(KindWater, "Agua", UnitLiters, 50m, 150m, null),
    ];

    public static ActuatorDefault? DefaultFor(string kind) => DefaultActuators.FirstOrDefault(a => a.Kind == kind);

    public sealed record ActuatorDefault(string Kind, string Name, string Unit, decimal MaxPerCommand, decimal MaxPerDay, decimal? Effect);
}
