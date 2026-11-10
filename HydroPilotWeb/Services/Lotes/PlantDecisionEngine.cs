namespace HydroPilotWeb.Services.Lotes;

/// <summary>
/// Motor de decisión de la etapa comercial por planta (plan 09, árbol de decisión).
/// Se evalúa de arriba hacia abajo y el riesgo tiene prioridad sobre todo lo demás.
/// Lógica pura (sin I/O): testeable sin base de datos.
///
/// Orden (desde 2026-10-26, con el trasplante a Adultas):
/// 1. Riesgo (anomalía) o GDD sobre sobremadurez → Riesgo / fuera de ventana.
/// 2. Trasplantada (ruta Adulta): Activa hasta la etapa adulta (cuando cierra la ventana Baby Leaf); ahí, como las
///    candidatas y las aptas de Baby Leaf, Adulta la que llegó al tamaño de cosecha adulta y Activa (posible adulta) la que
///    no. Sin medición de tamaño, Adulta cuando el lote llega al corte de adultas. Desde 2026-11-09 (antes era Adulta
///    apenas se trasplantaba).
/// 3. Ruta Baby Leaf (sigue en el circuito denso):
///    - ventana cerrada sin cortarse → se descarta (no hay lugar para seguir como adulta);
///    - en la ventana: se pasó del tamaño máximo → se descarta; si no, por score: Baby Leaf / candidata / en desarrollo;
///    - antes de la ventana → Activa.
///
/// UMBRALES: 250, 450, 750, 60, 80 y el tamaño máximo son configurables (valores de arranque), nunca
/// constantes ocultas: llegan por PlantDecisionThresholds desde los catálogos y los parámetros.
/// </summary>
public static class PlantDecisionEngine
{
    /// <summary>Umbrales de arranque tomados de los catálogos (LOT-02) y de Ajustes &gt; Parámetros.</summary>
    public sealed record PlantDecisionThresholds(
        decimal BabyLeafGddMin,     // ConfiguracionBabyLeaf.GddMin (arranque 250)
        decimal BabyLeafGddMax,     // ConfiguracionBabyLeaf.GddMax (arranque 450)
        decimal OvermaturityGdd,    // GddMax de la última etapa fenológica (arranque 750)
        decimal ScoreMinCandidate,  // ConfiguracionBabyLeaf.ScoreMinCandidate (arranque 60)
        decimal ScoreMinReady,      // ConfiguracionBabyLeaf.ScoreMinReady (arranque 80)
        decimal? MaxSizeFactor = null,  // tamaño máximo Baby Leaf × el ideal (arranque 1,3); null = sin control
        decimal? AdultReadySize = null, // tamaño de cosecha adulta × el ideal Baby Leaf (arranque 2); null = por GDD
        decimal AdultCutGdd = 650m);    // corte de adultas: sin medición de tamaño, Adulta desde acá

    /// <summary>
    /// Entradas de evaluación de una planta ACTIVA.
    /// Los valores null significan "no hay dato": el motor NO inventa resultados.
    /// </summary>
    public sealed record PlantEvaluationInput(
        decimal LotGdd,
        decimal? BabyLeafScore,         // null = sin evaluación disponible
        bool? MandatoryCriteriaMet,     // null = criterios obligatorios sin confirmar
        bool HasRisk,
        string? RiskReason,
        bool IsAdultRoute = false,      // trasplantada a un circuito de Adultas
        decimal? SizeRatio = null);     // tamaño respecto del ideal (última evaluación); null = sin medición

    /// <summary>Discard = la planta ya no puede ser Baby Leaf ni tiene lugar como adulta: el flujo diario la descarta.</summary>
    public sealed record PlantDecision(string CommercialStageName, string? Reason, bool Discard = false);

    public static PlantDecision Evaluate(PlantEvaluationInput input, PlantDecisionThresholds thresholds)
    {
        // 1. Riesgo con prioridad absoluta (anomalía, bolting, mal estado visual, sobremadurez).
        if (input.HasRisk)
            return new PlantDecision(CommercialStageNames.RiesgoFueraDeVentana,
                input.RiskReason ?? "Riesgo detectado por el proveedor de anomalías");

        if (input.LotGdd > thresholds.OvermaturityGdd)
            return new PlantDecision(CommercialStageNames.RiesgoFueraDeVentana,
                $"GDD {input.LotGdd} supera el umbral de sobremadurez ({thresholds.OvermaturityGdd})");

        // 2. Ruta Adulta: Activa hasta que llega a adulta en la etapa adulta; Adulta = apta para cortar.
        if (input.IsAdultRoute)
        {
            if (input.LotGdd < thresholds.BabyLeafGddMax)
                return new PlantDecision(CommercialStageNames.Activa, "Trasplantada: crece en Adultas hasta la etapa adulta");
            var ready = thresholds.AdultReadySize is { } readySize && input.SizeRatio is { } size
                ? size >= readySize
                : input.LotGdd >= thresholds.AdultCutGdd;
            return ready
                ? new PlantDecision(CommercialStageNames.CosechaConvencional, "Llegó al tamaño de cosecha adulta: apta para cortar")
                : new PlantDecision(CommercialStageNames.Activa, "Posible adulta: todavía no llega al tamaño de cosecha adulta");
        }

        // 3. Ruta Baby Leaf: la ventana cerró sin cortarla → descarte.
        if (input.LotGdd >= thresholds.BabyLeafGddMax)
            return new PlantDecision(CommercialStageNames.RiesgoFueraDeVentana,
                $"Pasó la ventana Baby Leaf ({thresholds.BabyLeafGddMax} GDD) sin cortarse", Discard: true);

        // Ventana Baby Leaf [GddMin, GddMax).
        if (input.LotGdd >= thresholds.BabyLeafGddMin)
        {
            if (thresholds.MaxSizeFactor is { } maxSize && input.SizeRatio is { } size && size > maxSize)
                return new PlantDecision(CommercialStageNames.RiesgoFueraDeVentana,
                    $"Se pasó del tamaño Baby Leaf ({size:0.##} veces el ideal; máximo {maxSize:0.##})", Discard: true);

            if (input.BabyLeafScore is null)
                return new PlantDecision(CommercialStageNames.Activa,
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

            return new PlantDecision(CommercialStageNames.Activa,
                $"Score {input.BabyLeafScore} por debajo del umbral candidata ({thresholds.ScoreMinCandidate})");
        }

        // Antes de la ventana Baby Leaf.
        return new PlantDecision(CommercialStageNames.Activa,
            $"GDD {input.LotGdd} por debajo de la ventana Baby Leaf ({thresholds.BabyLeafGddMin})");
    }
}
