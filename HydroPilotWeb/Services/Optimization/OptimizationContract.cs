namespace HydroPilotWeb.Models.Optimization;

/// <summary>Estados de una recomendación.</summary>
public static class OptimizationContractStatus
{
    public const string Pending = "PENDING";

    /// <summary>Aceptada: el usuario se comprometió a hacerlo; el sistema verifica solo si se cumplió.</summary>
    public const string Accepted = "ACCEPTED";

    /// <summary>Cumplida: aceptada y el problema se resolvió.</summary>
    public const string Done = "DONE";

    /// <summary>No cumplida: aceptada y se pasó la fecha (o se cerró la ventana) sin resolverse.</summary>
    public const string Missed = "MISSED";

    public const string Discarded = "DISCARDED";

    /// <summary>Ya no aplica: el problema desapareció sin decisión.</summary>
    public const string Expired = "EXPIRED";

    /// <summary>Solo lógica anterior (historial).</summary>
    public const string Blocked = "BLOCKED";
}

/// <summary>Dirección sugerida por una recomendación.</summary>
public static class OptimizationContractDirection
{
    public const string Raise = "RAISE";
    public const string Lower = "LOWER";
    public const string Maintain = "MAINTAIN";
    public const string Verify = "VERIFY";

    /// <summary>Cambiar la estrategia de destino del lote.</summary>
    public const string Switch = "SWITCH";

    /// <summary>Cortar (cosechar).</summary>
    public const string Cut = "CUT";
}

/// <summary>Prioridades de recomendación.</summary>
public static class OptimizationContractPriority
{
    public const string High = "HIGH";
    public const string Medium = "MEDIUM";
    public const string Low = "LOW";
}

/// <summary>Tipos de recomendación (R1–R6) y, para el historial, los de la lógica anterior.</summary>
public static class OptimizationContractType
{
    /// <summary>R1: cortar Baby Leaf (óptimo alcanzado o en pocos días).</summary>
    public const string BabyLeafCut = "BABY_LEAF_CUT";

    /// <summary>R2: cortar convencional (óptimo alcanzado, en pocos días o pasado de cosecha).</summary>
    public const string ConventionalCut = "CONVENTIONAL_CUT";

    /// <summary>R3: otra estrategia de destino deja más margen que el plan del lote.</summary>
    public const string HarvestStrategy = "HARVEST_STRATEGY";

    /// <summary>R4: CE por encima del rango: se gasta de más en nutrientes.</summary>
    public const string NutrientOverspend = "NUTRIENT_OVERSPEND";

    /// <summary>R5: pH/CE fuera de rango o anomalía abierta: plantas en riesgo.</summary>
    public const string SolutionRisk = "SOLUTION_RISK";

    /// <summary>R6: meta Baby Leaf o máximo de pérdidas en riesgo.</summary>
    public const string CycleGoals = "CYCLE_GOALS";

    // Lógica anterior (solo historial).
    public const string ChemicalSolution = "CHEMICAL_SOLUTION";
    public const string EconomicAlternative = "ECONOMIC_ALTERNATIVE";
    public const string CommercialGrowout = "COMMERCIAL_GROWOUT";
}

/// <summary>Qué representa el impacto en $ de una recomendación.</summary>
public static class OptimizationImpactKind
{
    /// <summary>Plata a capturar (cortes, estrategia).</summary>
    public const string Capture = "capture";

    /// <summary>Ahorro por semana (nutrientes).</summary>
    public const string Saving = "saving";

    /// <summary>Valor en riesgo (solución, metas).</summary>
    public const string Risk = "risk";
}

/// <summary>Acciones manuales registrables sobre una recomendación.</summary>
public static class OptimizationContractAction
{
    public const string Accept = "ACCEPT";
    public const string Discard = "DISCARD";
}

/// <summary>Ítems del catálogo de costos/precios (hoy solo se usa el precio por kg).</summary>
public static class OptimizationContractCostItem
{
    public const string PricePerKg = "price_per_kg";
    public const string SeedCostM2 = "seed_cost_m2";
    public const string NutrientCostM2 = "nutrient_cost_m2";
    public const string EnergyCostM2 = "energy_cost_m2";
    public const string TransplantCostM2 = "transplant_cost_m2";
}

/// <summary>Destinos comerciales del catálogo de precios.</summary>
public static class OptimizationContractDestination
{
    public const string BabyLeaf = "baby_leaf";
    public const string Conventional = "conventional";
}

/// <summary>Constantes compartidas del módulo de optimización.</summary>
public static class OptimizationContract
{
    public const string RuleVersion = "optimization-v2";

    /// <summary>Mínimos por defecto para recomendar cambiar de estrategia (Ajustes &gt; Parámetros).</summary>
    public const decimal DefaultStrategyMinGainAmount = 10_000m;
    public const decimal DefaultStrategyMinGainPercent = 10m;

    /// <summary>Motivos de descarte (lista corta; "Otro" pide el texto).</summary>
    public static readonly IReadOnlyList<string> DiscardReasons =
        ["Precio distinto", "Sin mano de obra", "Dato erróneo", "Otro"];
}
