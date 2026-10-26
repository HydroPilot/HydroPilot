using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Metas del ciclo (meta Baby Leaf y plantas activas esperadas), validación de objetivos y
/// línea de tiempo de ventanas de cosecha de la vista Lotes. Lógica pura.
/// </summary>
public class LotOverviewRulesTests
{
    private static LotGoalsRules.Result Goals(
        int total = 22, int harvestedBl = 1, int apta = 7, int candidate = 6,
        int discarded = 2, decimal? goal = 80m, decimal? expectedActive = 90m)
        => LotGoalsRules.Evaluate(new LotGoalsRules.Input(total, harvestedBl, apta, candidate, discarded, goal, expectedActive));

    [Fact]
    public void Meta_Baby_Leaf_Cuenta_Cosechadas_Mas_Aptas_Y_Potencial_Con_Candidatas()
    {
        var result = Goals(); // Lote Demo 01: 1 cosechada + 7 aptas + 6 candidatas de 22

        Assert.Equal(8, result.AchievedCount);
        Assert.Equal(36.4m, result.AchievedPercent);
        Assert.Equal(14, result.PotentialCount);
        Assert.Equal(63.6m, result.PotentialPercent);
        Assert.Equal(18, LotGoalsRules.PlantsFor(80m, 22)); // ceil(80% de 22 = 17,6)
    }

    [Fact]
    public void Barra_De_Estados_Suma_Igual_Que_El_Logrado_Y_El_Potencial()
    {
        // Lote Demo 01: cosechada BL, aptas, candidatas, en desarrollo, cosechada conv., convencional, riesgo, descartadas.
        int[] counts = [1, 7, 6, 6, 0, 0, 0, 2];
        var goals = Goals();

        var shares = LotGoalsRules.CumulativeShares(counts, 22);

        Assert.Equal([4.5m, 31.9m, 27.2m, 27.3m, 0m, 0m, 0m, 9.1m], shares);
        Assert.Equal(goals.AchievedPercent, shares[0] + shares[1]);             // 36,4 (no 4,5 + 31,8 = 36,3)
        Assert.Equal(goals.PotentialPercent, shares[0] + shares[1] + shares[2]); // 63,6
        Assert.Equal(100m - goals.ActivePercent, shares[^1]);                    // 9,1 descartadas = 100 − 90,9 activas
        Assert.Equal(100m, shares.Sum());
    }

    [Theory]
    [InlineData(ForecastSourceKind.Mock, 16, 0, 1)]      // temperaturas sintéticas
    [InlineData(ForecastSourceKind.Observed, 0, 10, 1)]  // sin lecturas de temperatura
    [InlineData(ForecastSourceKind.Mock, 12, 4, 2)]      // sintéticas y con días faltantes
    [InlineData(ForecastSourceKind.Forecast, 16, 0, 0)]  // datos reales completos: sin alerta
    [InlineData(ForecastSourceKind.Observed, 0, 0, 0)]   // recién sembrado: nada que medir todavía
    public void Alertas_De_Confiabilidad_Solo_Por_Problemas_De_Datos(
        ForecastSourceKind kind, int coverageDays, int missingDays, int expectedAlerts)
    {
        Assert.Equal(expectedAlerts, LotDataQualityRules.Alerts(kind, coverageDays, missingDays).Count);
    }

    [Fact]
    public void Avisos_Sin_Los_De_La_Fecha_De_Cosecha_Del_Forecast()
    {
        // Lotes no usa esa fecha (umbral de aptas, GDD objetivo, criterio de tamaño): sus cortes van por GDD.
        var warnings = LotDataQualityRules.RemainingWarnings(
        [
            "Se detectaron lecturas sintéticas (marker mock/demo) en el histórico: no representan precisión real.",
            "Porcentaje de Baby Leafs (36,8%) por debajo del objetivo (70,00%).",
            "Porcentaje objetivo de Baby Leafs no configurado: fecha de cosecha híbrida pendiente de decisión.",
            "La fecha por GDD (2026-10-28) queda más allá del horizonte de 7 días: es una extrapolación con el último GDD diario, no un pronóstico climático.",
            "Cosecha adulta: sin criterio de tamaño/madurez disponible (análisis de imagen pendiente): fecha híbrida sin decidir.",
        ], []);

        Assert.Equal(["Se detectaron lecturas sintéticas (marker mock/demo) en el histórico: no representan precisión real."], warnings);
    }

    [Fact]
    public void Avisos_Que_Ya_Explica_Una_Alerta_No_Se_Repiten()
    {
        // Lote Demo 02: sin lecturas de temperatura. La alerta "Sin lecturas de temperatura" cubre
        // los avisos técnicos de cobertura; quedan los que dicen otra cosa.
        var alerts = LotDataQualityRules.Alerts(ForecastSourceKind.Observed, coverageDays: 0, missingDays: 7);
        var warnings = LotDataQualityRules.RemainingWarnings(
        [
            "Se descartaron 931 lecturas asociadas a otro lote por asignación nodo→lote.",
            "Faltan 7 día(s) del período sin lectura usable (cobertura 0/7).",
            "Sin lecturas de temperatura operativamente usables en el período: el GDD observado queda sin dato (no se asume 0 como medición).",
            "Sin promedio de días observados para fallback: los días sin pronóstico quedan sin valor de GDD (no se asume 0).",
            "La fecha por GDD (2026-10-28) queda más allá del horizonte de 7 días: es una extrapolación con el último GDD diario, no un pronóstico climático.",
        ], alerts);

        Assert.Equal(["Se descartaron 931 lecturas asociadas a otro lote por asignación nodo→lote."], warnings);

        // Lote Demo 01: el aviso de lecturas sintéticas lo explica la alerta.
        var synthetic = LotDataQualityRules.Alerts(ForecastSourceKind.Mock, coverageDays: 16, missingDays: 0);
        Assert.Empty(LotDataQualityRules.RemainingWarnings(
            ["Se detectaron lecturas sintéticas (marker mock/demo) en el histórico: no representan precisión real."], synthetic));
    }

    [Fact]
    public void Sin_Metas_No_Se_Inventa_Un_Estado()
    {
        var result = Goals(goal: null, expectedActive: null);
        Assert.Equal(ActiveGoalStatus.SinMeta, result.ActiveStatus);
    }

    [Fact]
    public void Lote_Sin_Plantas_No_Divide_Por_Cero()
    {
        var result = Goals(total: 0, harvestedBl: 0, apta: 0, candidate: 0, discarded: 0);
        Assert.Equal(0m, result.AchievedPercent);
        Assert.Equal(0m, result.ActivePercent);
        Assert.Equal(ActiveGoalStatus.SinPlantas, result.ActiveStatus);
    }

    [Theory]
    [InlineData(2, 90, ActiveGoalStatus.Dentro)]      // 20 de 22 = 90,9% ≥ 90%
    [InlineData(3, 90, ActiveGoalStatus.PorDebajo)]   // 19 de 22 = 86,4% < 90%
    [InlineData(2, 91, ActiveGoalStatus.PorDebajo)]   // 90,9% < 91%
    public void Plantas_Activas_Contra_Las_Esperadas(int discarded, int expectedActive, ActiveGoalStatus expected)
    {
        var result = Goals(discarded: discarded, expectedActive: expectedActive);
        Assert.Equal(expected, result.ActiveStatus);
    }

    [Fact]
    public void Las_Cosechadas_Cuentan_Como_Activas()
    {
        // 10 plantas: 3 cosechadas como Baby Leaf y 1 descartada → 9 no se perdieron.
        var result = Goals(total: 10, harvestedBl: 3, apta: 0, candidate: 0, discarded: 1, expectedActive: 90m);
        Assert.Equal(9, result.ActiveCount);
        Assert.Equal(90m, result.ActivePercent);
        Assert.Equal(ActiveGoalStatus.Dentro, result.ActiveStatus);
        Assert.Equal(9, LotGoalsRules.PlantsFor(90m, 10));
        Assert.Equal(55, LotGoalsRules.PlantsFor(90.5m, 60)); // ceil(54,3): lo mínimo para llegar
    }

    [Fact]
    public void El_Color_Sale_De_Las_Plantas_De_La_Meta_No_Del_Porcentaje_Redondeado()
    {
        // Meta 91,7% de 60 = ceil(55,02) = 56 plantas. Con 55 activas el % redondeado también da 91,7%,
        // pero faltan plantas: tiene que quedar por debajo, como dice el título.
        var result = Goals(total: 60, harvestedBl: 0, apta: 0, candidate: 0, discarded: 5, expectedActive: 91.7m);
        Assert.Equal(91.7m, result.ActivePercent);
        Assert.Equal(56, LotGoalsRules.PlantsFor(91.7m, 60));
        Assert.Equal(ActiveGoalStatus.PorDebajo, result.ActiveStatus);
    }

    [Fact]
    public void Validacion_Acepta_Valores_Vacios_Y_En_Rango()
    {
        Assert.Empty(LotObjectivesRules.Validate(new LotObjectives(null, null, null)));
        Assert.Empty(LotObjectivesRules.Validate(new LotObjectives(100m, 0m, 0m)));
    }

    [Theory]
    [InlineData(0, 50, 10)]     // umbral 0%: el lote estaría siempre listo
    [InlineData(101, 50, 10)]
    [InlineData(70, -1, 10)]
    [InlineData(70, 101, 10)]
    [InlineData(70, 50, -1)]
    [InlineData(70, 50, 101)]
    public void Validacion_Rechaza_Valores_Fuera_De_Rango(int threshold, int goal, int maxLoss)
    {
        var errors = LotObjectivesRules.Validate(new LotObjectives(threshold, goal, maxLoss));
        Assert.Single(errors);
    }

    [Fact]
    public void Normalizacion_Redondea_A_Dos_Decimales()
    {
        var normalized = LotObjectivesRules.Normalize(new LotObjectives(70.126m, 80.004m, null));
        Assert.Equal(70.13m, normalized.HarvestThresholdPercent);
        Assert.Equal(80.00m, normalized.BabyLeafGoalPercent);
        Assert.Null(normalized.ExpectedActivePercent);
    }

    [Theory]
    [InlineData(249.9, CyclePhase.Desarrollo)]
    [InlineData(250, CyclePhase.VentanaBabyLeaf)]
    [InlineData(449.9, CyclePhase.VentanaBabyLeaf)]
    [InlineData(450, CyclePhase.VentanaConvencional)]
    [InlineData(750, CyclePhase.VentanaConvencional)]
    [InlineData(750.1, CyclePhase.Sobremadurez)]
    public void Fase_Del_Lote_Usa_Los_Limites_Del_Motor_De_Decision(double gdd, CyclePhase expected)
    {
        Assert.Equal(expected, CycleTimelineLogic.PhaseFor((decimal)gdd, 250m, 450m, 750m));
    }

    private static List<DailyGddPoint> Points(DateOnly from, params decimal[] gdd) =>
        gdd.Select((g, i) => new DailyGddPoint(from.AddDays(i), g, GddPointSource.Observed)).ToList();

    [Fact]
    public void Hito_Ya_Alcanzado_Toma_La_Fecha_Observada_Del_Historial()
    {
        var history = Points(new DateOnly(2026, 9, 21), 100m, 100m, 100m);

        var milestone = CycleTimelineLogic.Milestone(250m, 300m, history, []);

        Assert.Equal(new DateOnly(2026, 9, 23), milestone.Date);
        Assert.False(milestone.IsEstimate);
        Assert.False(milestone.IsExtrapolated);
    }

    [Fact]
    public void Hito_Futuro_Dentro_Del_Pronostico()
    {
        var projection = Points(new DateOnly(2026, 10, 1), 50m, 50m, 50m);

        var milestone = CycleTimelineLogic.Milestone(400m, 300m, [], projection);

        Assert.Equal(new DateOnly(2026, 10, 2), milestone.Date);
        Assert.True(milestone.IsEstimate);
        Assert.False(milestone.IsExtrapolated);
    }

    [Fact]
    public void Hito_Futuro_Mas_Alla_Del_Pronostico_Queda_Marcado_Como_Extrapolado()
    {
        var projection = Points(new DateOnly(2026, 10, 1), 50m, 50m);

        var milestone = CycleTimelineLogic.Milestone(500m, 300m, [], projection);

        Assert.Equal(new DateOnly(2026, 10, 4), milestone.Date); // faltan 100 GDD a 50/día
        Assert.True(milestone.IsExtrapolated);
    }

    [Fact]
    public void Linea_De_Tiempo_Calcula_Dia_Del_Ciclo_Y_Ventanas()
    {
        var sowing = new DateOnly(2026, 9, 20);
        var history = Points(sowing.AddDays(1), Enumerable.Repeat(16.7m, 15).ToArray()); // 250,5 GDD
        var projection = Points(new DateOnly(2026, 10, 6), Enumerable.Repeat(13m, 7).ToArray());

        var timeline = CycleTimelineLogic.Build(sowing, new DateOnly(2026, 10, 5), 250.5m,
            history, projection, 250m, 450m, 750m);

        Assert.Equal(15, timeline.CycleDay);
        Assert.Equal(CyclePhase.VentanaBabyLeaf, timeline.Phase);
        Assert.Equal(new DateOnly(2026, 10, 5), timeline.BabyLeafOpens.Date);
        Assert.True(timeline.BabyLeafCloses.IsEstimate);
        Assert.True(timeline.BabyLeafCloses.Date > timeline.AsOfDate);
        Assert.True(timeline.Overmaturity.Date > timeline.BabyLeafCloses.Date);
        Assert.Equal(300m, timeline.BabyLeafOptimum.Gdd);
        Assert.Equal(650m, timeline.ConventionalOptimum.Gdd);
        Assert.True(timeline.BabyLeafOptimum.Date > timeline.BabyLeafOpens.Date);
        Assert.True(timeline.ConventionalOptimum.Date > timeline.BabyLeafCloses.Date);
    }

    private static CycleTimeline TimelineAt(decimal accumulated)
    {
        var asOf = new DateOnly(2026, 10, 13);
        var projection = Points(asOf.AddDays(1), Enumerable.Repeat(15m, 7).ToArray());
        return CycleTimelineLogic.Build(asOf.AddDays(-20), asOf, accumulated, [], projection, 250m, 450m, 750m);
    }

    [Theory]
    [InlineData(200, HarvestDestination.BabyLeaf, false)]     // desarrollo: espera el óptimo Baby Leaf
    [InlineData(280, HarvestDestination.BabyLeaf, false)]     // ventana abierta, antes del óptimo
    [InlineData(300, HarvestDestination.BabyLeaf, true)]      // óptimo alcanzado: cortar ya, hasta el cierre
    [InlineData(449, HarvestDestination.BabyLeaf, true)]
    [InlineData(450, HarvestDestination.Convencional, false)] // lo que no se cortó sigue para convencional
    [InlineData(650, HarvestDestination.Convencional, true)]
    [InlineData(750, HarvestDestination.Convencional, true)]
    public void Proximo_Corte_Segun_Fase_Por_Gdd(double gdd, HarvestDestination destination, bool cutNow)
    {
        var cut = TimelineAt((decimal)gdd).NextCut;

        Assert.NotNull(cut);
        Assert.Equal(destination, cut.Destination);
        Assert.Equal(cutNow, cut.CutNow);
    }

    [Fact]
    public void Proximo_Corte_Baby_Leaf_Tiene_Fecha_Del_Optimo_Y_Limite_En_El_Cierre()
    {
        var cut = TimelineAt(270m).NextCut!; // faltan 30 GDD al óptimo y 180 al cierre, a 15 por día

        Assert.Equal(new DateOnly(2026, 10, 15), cut.Optimum.Date);
        Assert.Equal(new DateOnly(2026, 10, 25), cut.Limit.Date);
        Assert.Equal(450m, cut.Limit.Gdd);
        Assert.True(cut.Limit.IsExtrapolated); // más allá de los 7 días de proyección
    }

    [Fact]
    public void En_Sobremadurez_No_Hay_Proximo_Corte()
    {
        Assert.Null(TimelineAt(760m).NextCut);
    }

    [Fact]
    public void Optimos_Quedan_Dentro_De_Su_Ventana()
    {
        // Ventana Baby Leaf 200–280 y convencional 280–600: 300 y 650 caen afuera.
        var timeline = CycleTimelineLogic.Build(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 13), 100m,
            [], [], 200m, 280m, 600m);

        Assert.Equal(280m, timeline.BabyLeafOptimum.Gdd);
        Assert.Equal(600m, timeline.ConventionalOptimum.Gdd);
    }

    [Fact]
    public void Curva_De_Gdd_Acumula_Lo_Medido_Marca_Dias_Sin_Lectura_Y_Sigue_Con_La_Proyeccion()
    {
        var sowing = new DateOnly(2026, 10, 1);
        var history = new List<DailyGddPoint>
        {
            new(sowing, 10m, GddPointSource.Observed),
            new(sowing.AddDays(2), 12m, GddPointSource.Observed), // el día 2 no tiene lectura
        };
        var projection = Points(sowing.AddDays(3), 15m, 15m);

        var curve = GddCurveLogic.Build(sowing, sowing.AddDays(2), history, projection);

        Assert.Equal([10m, 10m, 22m], curve.Measured.Select(d => d.Cumulative));
        Assert.Null(curve.Measured[1].Source); // día sin lectura: no suma
        Assert.Equal([37m, 52m], curve.Projected.Select(d => d.Cumulative));
        Assert.Equal(sowing.AddDays(4), curve.EndDate);
        Assert.Equal(52m, curve.MaxCumulative);
    }

    [Fact]
    public void Curva_De_Gdd_Con_Siembra_Futura_Solo_Proyecta_Desde_La_Siembra()
    {
        // Hoy 9/10, siembra 12/10: nada medido; los días antes de sembrar no suman.
        var sowing = new DateOnly(2026, 10, 12);
        var projection = Points(new DateOnly(2026, 10, 10), 15m, 15m, 15m, 15m);

        var curve = GddCurveLogic.Build(sowing, new DateOnly(2026, 10, 9), [], projection);

        Assert.Empty(curve.Measured);
        Assert.Equal([sowing, sowing.AddDays(1)], curve.Projected.Select(d => d.Date));
        Assert.Equal([15m, 30m], curve.Projected.Select(d => d.Cumulative));
    }

    [Theory]
    [InlineData("Lote 03", 6, 10, 0, 0)]
    [InlineData("", 6, 10, 0, 1)]       // sin nombre
    [InlineData("Lote 03", 0, 10, 0, 1)] // grilla vacía
    [InlineData("Lote 03", 6, 31, 0, 1)] // grilla demasiado grande
    [InlineData("Lote 03", 6, 10, 1, 1)] // siembra futura
    public void Alta_De_Lote_Valida_Nombre_Grilla_Y_Fecha(string name, int rows, int columns, int daysFromToday, int expectedErrors)
    {
        var today = new DateOnly(2026, 10, 14);
        var input = new NewLotInput(name, CropTypeId: 1, GreenhouseId: 1, today.AddDays(daysFromToday), rows, columns, CircuitId: 1);

        Assert.Equal(expectedErrors, NewLotRules.Validate(input, today).Count);
    }

    [Fact]
    public void Alta_De_Lote_Valida_Y_Redondea_Las_Metas_Opcionales()
    {
        var today = new DateOnly(2026, 10, 14);
        var input = new NewLotInput("Lote 03", 1, 1, today, 6, 10, BabyLeafGoalPercent: 80.126m, ExpectedActivePercent: null, CircuitId: 1);

        Assert.Empty(NewLotRules.Validate(input, today));
        Assert.Equal(80.13m, NewLotRules.Goals(input).BabyLeafGoalPercent);
        Assert.Null(NewLotRules.Goals(input).ExpectedActivePercent); // vacía: sin definir
        Assert.Single(NewLotRules.Validate(input with { ExpectedActivePercent = 150m }, today));
    }

    [Fact]
    public void Rendimiento_Real_Del_Ciclo_Usa_La_Referencia_De_La_Cosecha()
    {
        var harvest = LotYieldRules.Harvested(3, 2, new YieldReference(30m, 200m), new YieldPrices(3000m, 2000m, "ARS"));

        Assert.Equal(new DestinationYield(3, 0.09m, 270m), harvest.Real.BabyLeaf);
        Assert.Equal(new DestinationYield(2, 0.4m, 800m), harvest.Real.Conventional);
        Assert.Equal(1070m, harvest.TotalRevenue);

        // Sin precio convencional y con plantas convencionales cosechadas: no hay total.
        var partial = LotYieldRules.Harvested(3, 2, new YieldReference(30m, 200m), new YieldPrices(3000m, null, "ARS"));
        Assert.Null(partial.TotalRevenue);

        // Sin plantas convencionales, ese precio no hace falta para el total.
        var babyLeafOnly = LotYieldRules.Harvested(3, 0, new YieldReference(30m, 200m), new YieldPrices(3000m, null, "ARS"));
        Assert.Equal(270m, babyLeafOnly.TotalRevenue);
    }

    [Fact]
    public void Rendimiento_Separa_Baby_Leaf_Y_Convencional_Actual_Y_Esperado_Sin_Historico()
    {
        // Lote Demo 01: 7 aptas, 6 candidatas, 6 en desarrollo, 0 convencionales.
        var estimate = LotYieldRules.Estimate(
            new LotYieldRules.Input(Apta: 7, Candidate: 6, Developing: 6, Conventional: 0),
            new YieldReference(30m, 200m),
            new YieldPrices(2600m, 1800m, "ARS"));

        Assert.Equal(new DestinationYield(7, 0.21m, 546m), estimate.Current.BabyLeaf);       // 7 × 30 g × $2600/kg
        Assert.Equal(new DestinationYield(0, 0m, 0m), estimate.Current.Conventional);
        Assert.Equal(new DestinationYield(13, 0.39m, 1014m), estimate.Expected.BabyLeaf);   // aptas + candidatas
        Assert.Equal(new DestinationYield(6, 1.2m, 2160m), estimate.Expected.Conventional); // en desarrollo + convencionales
        Assert.Equal(new YieldBasis(1m, null), estimate.Basis);                            // sin histórico: solo el lote
    }

    private static readonly YieldReference Weights = new(120m, 280m);
    private static readonly YieldPrices Prices = new(14650m, 2100m, "ARS");

    // Lotes cerrados de la misma época: 53,8 % Baby Leaf, 37,2 % convencional (como el B18 de la simulación).
    private static readonly YieldHistory History = new(7, true, 220m / 409m, 152m / 409m);

    [Fact]
    public void Esperado_En_Desarrollo_Sale_Del_Historico()
    {
        // 60 plantas en desarrollo: el lote todavía no dice nada, se reparten con el histórico.
        var estimate = LotYieldRules.Estimate(
            new LotYieldRules.Input(Apta: 0, Candidate: 0, Developing: 60, Conventional: 0), Weights, Prices, History, lotWeight: 0m);

        Assert.Equal(new DestinationYield(32, 3.84m, 56256m), estimate.Expected.BabyLeaf);       // 60 × 53,8 % = 32,3
        Assert.Equal(new DestinationYield(22, 6.16m, 12936m), estimate.Expected.Conventional);   // 60 × 37,2 % = 22,3
        Assert.Equal(new DestinationYield(0, 0m, 0m), estimate.Current.BabyLeaf);
    }

    [Fact]
    public void Esperado_En_La_Ventana_Baby_Leaf_Mezcla_Historico_Y_Lote()
    {
        // A mitad de la ventana: 20 aptas + 10 candidatas + 30 en desarrollo, histórico 50 % / 40 %.
        var history = new YieldHistory(5, true, 0.5m, 0.4m);
        var estimate = LotYieldRules.Estimate(
            new LotYieldRules.Input(Apta: 20, Candidate: 10, Developing: 30, Conventional: 0), Weights, Prices, history, lotWeight: 0.5m);

        // Histórico: 30 BL y 24 conv. Lote: 30 BL y 30 conv. Mitad y mitad: 30 y 27.
        Assert.Equal(30, estimate.Expected.BabyLeaf.Plants);
        Assert.Equal(27, estimate.Expected.Conventional.Plants);
        Assert.Equal(0.5m, estimate.Basis.LotWeight);
    }

    [Fact]
    public void Esperado_Despues_De_La_Ventana_Es_El_Lote_Con_Lo_Ya_Cosechado()
    {
        // Ventana convencional: 35 cosechadas como Baby Leaf, 20 convencionales activas, 2 en riesgo.
        var estimate = LotYieldRules.Estimate(
            new LotYieldRules.Input(Apta: 0, Candidate: 0, Developing: 0, Conventional: 20, Risk: 2, HarvestedBabyLeaf: 35),
            Weights, Prices, History, lotWeight: 1m);

        Assert.Equal(35, estimate.Expected.BabyLeaf.Plants);      // lo cosechado no se pierde del esperado
        Assert.Equal(20, estimate.Expected.Conventional.Plants);  // las en riesgo cuentan como pérdida
        Assert.Equal(new DestinationYield(20, 5.6m, 11760m), estimate.Current.Conventional);
    }

    [Fact]
    public void Sin_Historico_El_Esperado_Es_Solo_El_Lote_Aunque_Este_En_Desarrollo()
    {
        var estimate = LotYieldRules.Estimate(
            new LotYieldRules.Input(Apta: 0, Candidate: 0, Developing: 60, Conventional: 0), Weights, Prices, history: null, lotWeight: 0m);

        Assert.Equal(1m, estimate.Basis.LotWeight);
        Assert.Equal(0, estimate.Expected.BabyLeaf.Plants);
        Assert.Equal(60, estimate.Expected.Conventional.Plants);
    }

    private static CycleTimeline Timeline(CyclePhase phase, decimal gdd)
    {
        static CycleMilestone At(decimal value) => new(value, null, true, false);
        return new CycleTimeline(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 20), 19, phase, gdd,
            At(250m), At(350m), At(450m), At(600m), At(750m));
    }

    [Theory]
    [InlineData(CyclePhase.Desarrollo, 120, 0)]
    [InlineData(CyclePhase.VentanaBabyLeaf, 250, 0)]     // recién abre: todavía todo histórico
    [InlineData(CyclePhase.VentanaBabyLeaf, 300, 0.25)]
    [InlineData(CyclePhase.VentanaBabyLeaf, 450, 1)]
    [InlineData(CyclePhase.VentanaConvencional, 520, 1)]
    [InlineData(CyclePhase.Sobremadurez, 800, 1)]
    public void El_Peso_Del_Lote_Crece_Con_La_Fase(CyclePhase phase, int gdd, double expected)
    {
        Assert.Equal((decimal)expected, LotYieldRules.LotWeight(Timeline(phase, gdd)));
    }

    [Fact]
    public void Sin_Linea_De_Tiempo_Pesa_Solo_El_Lote()
    {
        Assert.Equal(1m, LotYieldRules.LotWeight(null));
    }

    private static LotYieldHistoryRules.ClosedLot Closed(string sowing, int total, int babyLeaf, int conventional) =>
        new(DateOnly.Parse(sowing), total, babyLeaf, conventional);

    [Fact]
    public void Historico_Usa_Los_Lotes_De_La_Misma_Epoca()
    {
        var lots = new[]
        {
            Closed("2025-10-10", 60, 42, 13),
            Closed("2024-11-05", 60, 24, 27),
            Closed("2025-09-20", 40, 30, 8),
            Closed("2026-01-15", 60, 0, 49),   // verano: fuera de la época (± 45 días)
        };

        var history = LotYieldHistoryRules.Build(new DateOnly(2026, 10, 18), lots)!;

        Assert.True(history.SameSeason);
        Assert.Equal(3, history.Lots);
        Assert.Equal(96m / 160m, history.BabyLeafShare);       // por plantas: un lote grande pesa más
        Assert.Equal(48m / 160m, history.ConventionalShare);
    }

    [Fact]
    public void Historico_Sin_Suficientes_De_La_Epoca_Usa_Todos_Los_Cerrados()
    {
        var lots = new[] { Closed("2025-10-10", 60, 42, 13), Closed("2026-02-01", 60, 0, 49), Closed("2026-06-01", 0, 0, 0) };

        var history = LotYieldHistoryRules.Build(new DateOnly(2026, 10, 18), lots)!;

        Assert.False(history.SameSeason);
        Assert.Equal(2, history.Lots);                          // el lote sin plantas no cuenta
        Assert.Equal(42m / 120m, history.BabyLeafShare);
    }

    [Fact]
    public void Sin_Lotes_Cerrados_No_Hay_Historico()
    {
        Assert.Null(LotYieldHistoryRules.Build(new DateOnly(2026, 10, 18), []));
    }

    [Theory]
    [InlineData("2026-12-15", "2027-01-10", 26)]   // cruza el fin de año
    [InlineData("2025-10-18", "2026-10-18", 0)]
    [InlineData("2026-03-01", "2026-09-01", 181)]
    public void Distancia_En_El_Anio(string a, string b, int days)
    {
        Assert.Equal(days, LotYieldHistoryRules.DaysApartInYear(DateOnly.Parse(a), DateOnly.Parse(b)));
    }

    [Fact]
    public void Rendimiento_Sin_Precio_No_Inventa_Pesos()
    {
        var estimate = LotYieldRules.Estimate(
            new LotYieldRules.Input(Apta: 2, Candidate: 0, Developing: 0, Conventional: 3),
            new YieldReference(25m, 180m),
            YieldPrices.None);

        Assert.Equal(new DestinationYield(2, 0.05m, null), estimate.Current.BabyLeaf);
        Assert.Equal(new DestinationYield(3, 0.54m, null), estimate.Current.Conventional);
    }

    [Fact]
    public void Rendimiento_Sin_Peso_Definido_No_Inventa_Kg()
    {
        var estimate = LotYieldRules.Estimate(
            new LotYieldRules.Input(Apta: 2, Candidate: 1, Developing: 0, Conventional: 3),
            new YieldReference(null, 180m),
            new YieldPrices(2600m, 1800m, "ARS"));

        Assert.Equal(new DestinationYield(2, null, null), estimate.Current.BabyLeaf);       // sin peso: ni kg ni $
        Assert.Equal(new DestinationYield(3, 0.54m, 972m), estimate.Current.Conventional);
        Assert.False(estimate.Reference.IsComplete);
    }

    [Theory]
    [InlineData(null, 200, 2600, 1800, 0)]   // vacío = sin definir, como las metas
    [InlineData(null, null, null, null, 0)]
    [InlineData(0, 200, 2600, 1800, 1)]
    [InlineData(30, 6000, 2600, 1800, 1)]
    [InlineData(30, 200, 2600, 0, 1)]
    [InlineData(-5, 200, -1, 1800, 2)]
    [InlineData(30, 200, 2600, 1800, 0)]
    public void Pesos_Y_Precios_De_Referencia_Se_Validan(
        int? babyLeafGrams, int? conventionalGrams, int? babyLeafPrice, int? conventionalPrice, int expectedErrors)
    {
        var errors = YieldReferenceRules.Validate(new YieldReferenceInput(
            babyLeafGrams, conventionalGrams, babyLeafPrice, conventionalPrice));
        Assert.Equal(expectedErrors, errors.Count);
    }
}
