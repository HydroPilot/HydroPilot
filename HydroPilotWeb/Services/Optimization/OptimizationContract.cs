namespace HydroPilotWeb.Models.Optimization;

/// <summary>Estados de una recomendación (contrato compartido plan 02 / plan 16 OPT-04).</summary>
public static class OptimizationContractStatus
{
    public const string Pending = "PENDING";
    public const string Accepted = "ACCEPTED";
    public const string Discarded = "DISCARDED";
    public const string Expired = "EXPIRED";
    public const string Blocked = "BLOCKED";
}

/// <summary>Dirección sugerida por una recomendación (regla OPT-02).</summary>
public static class OptimizationContractDirection
{
    public const string Raise = "RAISE";
    public const string Lower = "LOWER";
    public const string Maintain = "MAINTAIN";
    public const string Verify = "VERIFY";

    /// <summary>
    /// Adoptar un destino alternativo (comparativa económica que supera el
    /// umbral: OPT-03/OPT-08). Es una recomendación informativa, nunca un
    /// cambio físico automático.
    /// </summary>
    public const string Switch = "SWITCH";
}

/// <summary>Prioridades de recomendación.</summary>
public static class OptimizationContractPriority
{
    public const string High = "HIGH";
    public const string Medium = "MEDIUM";
    public const string Low = "LOW";
}

/// <summary>Tipos de recomendación del módulo de optimización.</summary>
public static class OptimizationContractType
{
    /// <summary>Manejo de la solución nutritiva del lote (pH/CE vs objetivos).</summary>
    public const string ChemicalSolution = "CHEMICAL_SOLUTION";

    /// <summary>Comparativa económica Baby Leaf vs convencional (OPT-03/OPT-08).</summary>
    public const string EconomicAlternative = "ECONOMIC_ALTERNATIVE";

    /// <summary>Recomendación comercial agregada del lote (OPT-08) con riesgo visible (OPT-09).</summary>
    public const string CommercialGrowout = "COMMERCIAL_GROWOUT";
}

/// <summary>Acciones manuales registrables sobre una recomendación (OPT-04).</summary>
public static class OptimizationContractAction
{
    public const string Accept = "ACCEPT";
    public const string Discard = "DISCARD";
}

/// <summary>Ítems del catálogo de costos/precios (OPT-03).</summary>
public static class OptimizationContractCostItem
{
    public const string PricePerKg = "price_per_kg";
    public const string SeedCostM2 = "seed_cost_m2";
    public const string NutrientCostM2 = "nutrient_cost_m2";
    public const string EnergyCostM2 = "energy_cost_m2";
    public const string TransplantCostM2 = "transplant_cost_m2";
}

/// <summary>Destinos comerciales de la comparativa económica.</summary>
public static class OptimizationContractDestination
{
    public const string BabyLeaf = "baby_leaf";
    public const string Conventional = "conventional";
}

/// <summary>
/// Contrato del módulo de optimización: constantes compartidas, versión de
/// reglas y textos canónicos. Fuente única para no divergir entre UI/API/tests.
/// </summary>
public static class OptimizationContract
{
    public const string RuleVersion = "optimization-v1";

    /// <summary>Umbral por defecto de diferencia de rentabilidad (configurable vía OptimizationOptions).</summary>
    public const decimal DefaultProfitabilityThresholdPercent = 10m;

    /// <summary>Cantidad de lecturas consecutivas consistentes para confianza alta.</summary>
    public const int DefaultConsistentReadingCount = 2;

    /// <summary>Ventana de frescura por defecto para lecturas de pH/CE (minutos).</summary>
    public const int DefaultMaxReadingAgeMinutes = 60;

    /// <summary>Horas de vigencia de una recomendación PENDING antes de EXPIRED.</summary>
    public const int DefaultPendingExpirationHours = 24;

    public const string InformativeBanner = "Las recomendaciones son informativas y no controlan actuadores.";

    public const string NoDoseBanner =
        "No se calculan dosis exactas: requiere volumen, concentración, receta validada y curva de respuesta.";

    /// <summary>Motivo canónico cuando la comparación económica no puede calcularse.</summary>
    public const string NotCalculableReason = "Comparación no calculable";
}