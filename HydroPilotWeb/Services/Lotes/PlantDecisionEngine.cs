namespace HydroPilotWeb.Services.Lotes;

/// <summary>
/// Motor de decisión de la etapa comercial por planta (plan 09, árbol de decisión).
/// Se evalúa de arriba hacia abajo y el riesgo tiene prioridad sobre todo lo demás.
/// Lógica pura (sin I/O): testeable sin base de datos.
///
/// Orden documentado en el plan:
/// 1. Riesgo/bolting/mal estado visual o GDD sobre sobremadurez → Riesgo / fuera de ventana.
/// 2. Ventana Baby Leaf ([GddMin, GddMax)) → por score: apta / candidata / en desarrollo.
/// 3. Ventana convencional ([GddMax, sobremadurez]) → madurez confirmada → convencional.
/// 4. GDD bajo la ventana Baby Leaf → En desarrollo.
///
/// UMBRALES: 250, 450, 750, 60 y 80 son configurables (valores de arranque), nunca
/// constantes ocultas: llegan por PlantDecisionThresholds desde los catálogos.
/// </summary>
public static class PlantDecisionEngine
{
    /// <summary>Umbrales de arranque tomados de los catálogos (LOT-02).</summary>
    public sealed record PlantDecisionThresholds(
        decimal BabyLeafGddMin,     // ConfiguracionBabyLeaf.GddMin (arranque 250)
        decimal BabyLeafGddMax,     // ConfiguracionBabyLeaf.GddMax (arranque 450)
        decimal OvermaturityGdd,    // GddMax de la última etapa fenológica (arranque 750)
        decimal ScoreMinCandidate,  // ConfiguracionBabyLeaf.ScoreMinCandidate (arranque 60)
        decimal ScoreMinReady);     // ConfiguracionBabyLeaf.ScoreMinReady (arranque 80)

    /// <summary>
    /// Entradas de evaluación de una planta ACTIVA.
    /// Los valores null significan "no hay dato": el motor NO inventa resultados.
    /// </summary>
    public sealed record PlantEvaluationInput(
        decimal LotGdd,
        decimal? BabyLeafScore,         // null = sin evaluación disponible
        bool? MandatoryCriteriaMet,     // null = criterios obligatorios sin confirmar
        bool? HasSufficientMaturity,    // null = sin análisis de imagen (madurez no confirmada)
        bool HasRisk,
        string? RiskReason);

    public sealed record PlantDecision(string CommercialStageName, string? Reason);

    public static PlantDecision Evaluate(PlantEvaluationInput input, PlantDecisionThresholds thresholds)
    {
        // 1. Riesgo con prioridad absoluta (anomalía, bolting, mal estado visual, sobremadurez).
        if (input.HasRisk)
            return new PlantDecision(CommercialStageNames.RiesgoFueraDeVentana,
                input.RiskReason ?? "Riesgo detectado por el proveedor de anomalías");

        if (input.LotGdd > thresholds.OvermaturityGdd)
            return new PlantDecision(CommercialStageNames.RiesgoFueraDeVentana,
                $"GDD {input.LotGdd} supera el umbral de sobremadurez ({thresholds.OvermaturityGdd})");

        // 2. Ventana Baby Leaf [GddMin, GddMax).
        if (input.LotGdd >= thresholds.BabyLeafGddMin && input.LotGdd < thresholds.BabyLeafGddMax)
        {
            if (input.BabyLeafScore is null)
                return new PlantDecision(CommercialStageNames.EnDesarrollo,
                    $"Sin evaluación de score disponible (GDD {input.LotGdd} en ventana Baby Leaf)");

            if (input.BabyLeafScore >= thresholds.ScoreMinReady)
            {
                if (input.MandatoryCriteriaMet == true)
                    return new PlantDecision(CommercialStageNames.BabyLeafApta,
                        $"Score {input.BabyLeafScore} ≥ {thresholds.ScoreMinReady} y criterios obligatorios aprobados");
                // Decisión documentada: score de apta sin confirmar obligatorios → candidata
                // (el plan no cubre esta combinación; "candidata" = aún no aprobada).
                return new PlantDecision(CommercialStageNames.CandidataBabyLeaf,
                    $"Score {input.BabyLeafScore} ≥ {thresholds.ScoreMinReady} pero criterios obligatorios no confirmados");
            }

            if (input.BabyLeafScore >= thresholds.ScoreMinCandidate)
                return new PlantDecision(CommercialStageNames.CandidataBabyLeaf,
                    $"Score {input.BabyLeafScore} en banda candidata ({thresholds.ScoreMinCandidate}–{thresholds.ScoreMinReady})");

            return new PlantDecision(CommercialStageNames.EnDesarrollo,
                $"Score {input.BabyLeafScore} por debajo del umbral candidata ({thresholds.ScoreMinCandidate})");
        }

        // 3. Ventana convencional [GddMax, sobremadurez].
        if (input.LotGdd >= thresholds.BabyLeafGddMax && input.LotGdd <= thresholds.OvermaturityGdd)
        {
            if (input.HasSufficientMaturity == true)
                return new PlantDecision(CommercialStageNames.CosechaConvencional,
                    "Ventana convencional con madurez confirmada por análisis de imagen");

            return new PlantDecision(CommercialStageNames.EnDesarrollo,
                "Ventana convencional sin madurez confirmada (falta procesamiento de imagen)");
        }

        // 4. GDD por debajo de la ventana Baby Leaf.
        return new PlantDecision(CommercialStageNames.EnDesarrollo,
            $"GDD {input.LotGdd} por debajo de la ventana Baby Leaf ({thresholds.BabyLeafGddMin})");
    }
}