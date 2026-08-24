using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Forecasting;

namespace HydroPilotWeb.Tests.Unit;

/// <summary>
/// Fórmula de GDD (plan 10): cap de Tmax en 30 °C, sin GDD negativo, base configurable.
/// </summary>
public class GddFormulaTests
{
    [Theory]
    [InlineData(28, 20, 4.5, 19.5)]    // (28+20)/2 - 4.5
    [InlineData(35, 22, 4.5, 21.5)]    // cap Tmax: (30+22)/2 - 4.5
    [InlineData(10, 5, 4.5, 3.0)]      // (10+5)/2 - 4.5
    [InlineData(10, 2, 4.5, 1.5)]      // (10+2)/2 - 4.5
    [InlineData(5, -5, 4.5, 0.0)]      // frío extremo → 0
    [InlineData(30, 30, 4.5, 25.5)]    // (30+30)/2 - 4.5
    public void DailyGdd_Formula_Y_Cap(decimal tmax, decimal tmin, decimal tbase, decimal expected)
    {
        Assert.Equal(expected, GddService.DailyGdd(tmax, tmin, tbase));
    }

    [Fact]
    public void DailyGdd_Nunca_Negativo()
    {
        for (var tmax = -20m; tmax <= 40m; tmax += 5m)
        {
            for (var tmin = -20m; tmin <= tmax; tmin += 5m)
            {
                Assert.True(GddService.DailyGdd(tmax, tmin, 4.5m) >= 0m);
            }
        }
    }

    [Fact]
    public void DailyGdd_Cap_De_Tmax_Aplica()
    {
        // Con Tmax > 30 el resultado no sube más.
        Assert.Equal(GddService.DailyGdd(30m, 20m, 4.5m), GddService.DailyGdd(40m, 20m, 4.5m));
    }
}

/// <summary>Lógica pura de fechas de proyección (F-03/F-10): cruce de umbral, extrapolación, días restantes.</summary>
public class GddDateLogicTests
{
    private static readonly IReadOnlyList<DailyGddPoint> FixedProjection =
    [
        new(new DateOnly(2026, 1, 2), 10m, GddPointSource.Forecast),
        new(new DateOnly(2026, 1, 3), 10m, GddPointSource.Forecast),
        new(new DateOnly(2026, 1, 4), 10m, GddPointSource.Forecast),
        new(new DateOnly(2026, 1, 5), 10m, GddPointSource.Forecast),
        new(new DateOnly(2026, 1, 6), 10m, GddPointSource.Forecast),
        new(new DateOnly(2026, 1, 7), 10m, GddPointSource.Forecast),
        new(new DateOnly(2026, 1, 8), 10m, GddPointSource.Forecast)
    ];

    [Fact]
    public void Cruce_Dentro_Del_Horizonte()
    {
        // 220 acumulado, target 250 → resta 30 → cruza el 3° día (10+10+10).
        var date = GddDateLogic.EstimateCrossDate(220m, FixedProjection, 250m);
        Assert.Equal(new DateOnly(2026, 1, 4), date);
    }

    [Fact]
    public void Ya_Sobre_El_Umbral_Cruza_Hoy()
    {
        // 320 acumulado ≥ 250 → ya está dentro: fecha = día previo al inicio de la proyección.
        var date = GddDateLogic.EstimateCrossDate(320m, FixedProjection, 250m);
        Assert.Equal(new DateOnly(2026, 1, 1), date);
    }

    [Fact]
    public void No_Alcanza_En_Horizonte_Extrapola_Con_Ultimo_Gdd()
    {
        // 100 acumulado, target 650 → restan 550; proyección suma 70 → resta 480
        // → extrapola 480/10 = 48 días después del último punto.
        var date = GddDateLogic.EstimateCrossDate(100m, FixedProjection, 650m);
        Assert.Equal(new DateOnly(2026, 1, 8).AddDays(48), date);
    }

    [Fact]
    public void Sin_Proyeccion_Y_Sin_Cruce_Devuelve_Null()
    {
        Assert.Null(GddDateLogic.EstimateCrossDate(100m, [], 650m));
    }

    [Fact]
    public void Dias_Restantes_Respetan_AsOfDate()
    {
        Assert.Equal(7, GddDateLogic.DaysRemaining(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 8)));
        Assert.Equal(0, GddDateLogic.DaysRemaining(new DateOnly(2026, 1, 8), new DateOnly(2026, 1, 8)));
        Assert.Equal(-2, GddDateLogic.DaysRemaining(new DateOnly(2026, 1, 8), new DateOnly(2026, 1, 6)));
        Assert.Null(GddDateLogic.DaysRemaining(new DateOnly(2026, 1, 8), null));
    }
}

/// <summary>Rendimiento (F-05): kg/m² con historial, baseline, sin negativos ni div por cero, confianza heurística.</summary>
public class YieldMathTests
{
    [Fact]
    public void Historial_Con_Dos_Ciclos_Usa_Promedio_De_KgPorM2()
    {
        // Dos ciclos: 3.0 y 5.0 kg/m² → promedio 4.0; área 4.5 → 18 kg base.
        var estimate = YieldMath.HistoryScenarios([3.0m, 5.0m], 2, 4.5m);
        Assert.Equal(18.00m, estimate.Base);
        Assert.Equal(2, estimate.HistoryCycles);
        Assert.True(estimate.Conservative <= estimate.Base && estimate.Base <= estimate.Optimistic);
        Assert.Equal(76, estimate.ConfidencePercent); // 60 + 8*2
    }

    [Fact]
    public void Historial_Con_Dispersion_Respeta_Rango_Relativo()
    {
        // Dos ciclos: 2.0 y 6.0 kg/m² → promedio 4.0 → σ = 2.83 → factor 0.8/1.2
        // (pisos relativos): 40 × 0.8 = 32 / 40 × 1.2 = 48 para área 10 m².
        var estimate = YieldMath.HistoryScenarios([2.0m, 6.0m], 2, 10m);
        Assert.Equal(40.00m, estimate.Base);
        Assert.Equal(32.00m, estimate.Conservative);
        Assert.Equal(48.00m, estimate.Optimistic);
    }

    [Fact]
    public void Confianza_Heuristica_Sube_Con_Ciclos_Y_Tope_95()
    {
        Assert.Equal(76, YieldMath.HistoryScenarios([1.0m, 2.0m], 2, 1m).ConfidencePercent);
        Assert.Equal(95, YieldMath.HistoryScenarios([1.0m, 2.0m, 3.0m, 4.0m, 5.0m], 5, 1m).ConfidencePercent); // 60+40 → tope 95
        Assert.Equal(95, YieldMath.HistoryScenarios([1.0m, 2.0m], 10, 1m).ConfidencePercent);
    }

    [Fact]
    public void Baseline_Sin_Historial_Escenarios_Fijos_15()
    {
        var estimate = YieldMath.BaselineScenarios(3.0m, 4.5m, 0m);
        Assert.Equal(13.50m, estimate.Base);
        Assert.Equal(11.48m, estimate.Conservative);
        Assert.Equal(15.53m, estimate.Optimistic);
        Assert.Equal(0, estimate.HistoryCycles);
        Assert.Equal(80, estimate.ConfidencePercent);
    }

    [Fact]
    public void Baseline_Confianza_Avanza_Con_El_Ciclo()
    {
        Assert.Equal(80, YieldMath.BaselineScenarios(3.0m, 4.5m, 0.1m).ConfidencePercent);
        Assert.Equal(86, YieldMath.BaselineScenarios(3.0m, 4.5m, 0.5m).ConfidencePercent);
        Assert.Equal(92, YieldMath.BaselineScenarios(3.0m, 4.5m, 0.8m).ConfidencePercent);
    }

    [Fact]
    public void Nunca_Negativos_Ni_Division_Por_Cero()
    {
        var estimate = YieldMath.BaselineScenarios(0m, 0m, 0m); // rendimiento base 0 y área 0
        Assert.True(estimate.Base >= 0m && estimate.Conservative >= 0m && estimate.Optimistic >= 0m);

        var hist = YieldMath.HistoryScenarios([0m, 0m], 2, 10m); // kg/m² = 0 no rompe
        Assert.Equal(0m, hist.Base);
        Assert.Equal(0m, hist.Conservative);
        Assert.Equal(0m, hist.Optimistic);
    }

    [Fact]
    public void Desviacion_Muestral_Con_Un_Valor_Es_Cero()
    {
        Assert.Equal(0d, YieldMath.SampleStdDev([5.0m], 5.0d));
    }
}

/// <summary>Precisión (F-05): última predicción anterior a la cosecha, MAPE sin rendimiento cero, error de días separado.</summary>
public class ForecastAccuracyTests
{
    private static PredictionInput Pred(
        DateOnly asOf, decimal? yield, DateOnly? harvestDate) =>
        new(asOf, asOf.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), yield, harvestDate);

    [Fact]
    public void Usa_La_Ultima_Prediccion_Anterior_A_La_Cosecha()
    {
        var cycles = new[]
        {
            new ClosedCycleInput(
                ActualYieldKg: 100m,
                ActualHarvestDate: new DateOnly(2026, 1, 10),
                Predictions:
                [
                    Pred(new DateOnly(2026, 1, 2), 90m, new DateOnly(2026, 1, 9)),
                    Pred(new DateOnly(2026, 1, 8), 110m, new DateOnly(2026, 1, 11)), // la más cercana pre-cosecha
                    Pred(new DateOnly(2026, 1, 11), 95m, new DateOnly(2026, 1, 12))  // posterior a la cosecha: excluida
                ])
        };

        var result = ForecastAccuracy.Compute(cycles);

        Assert.Equal(1, result.Cycles);
        Assert.Equal(10.0m, result.Mape);        // |110-100|/100
        Assert.Equal(1.0m, result.DaysError);    // |11-10|
    }

    [Fact]
    public void Mape_No_Se_Calcula_Con_Rendimiento_Real_Cero()
    {
        var cycles = new[]
        {
            new ClosedCycleInput(0m, new DateOnly(2026, 1, 10),
                [Pred(new DateOnly(2026, 1, 8), 100m, new DateOnly(2026, 1, 9))])
        };

        var result = ForecastAccuracy.Compute(cycles);

        Assert.Null(result.Mape);      // división por cero → sin MAPE
        Assert.Equal(1, result.Cycles); // el ciclo sigue contando como elegible
        Assert.NotNull(result.DaysError);
    }

    [Fact]
    public void Sin_Prediccion_Previa_No_Cuenta_El_Ciclo()
    {
        var cycles = new[]
        {
            new ClosedCycleInput(100m, new DateOnly(2026, 1, 10),
                [Pred(new DateOnly(2026, 1, 12), 95m, new DateOnly(2026, 1, 13))]) // posterior
        };

        var result = ForecastAccuracy.Compute(cycles);

        Assert.Equal(0, result.Cycles);
        Assert.Null(result.Mape);
        Assert.Null(result.DaysError);
    }

    [Fact]
    public void Sin_Ciclos_Cerrados_Resultado_Vacio()
    {
        var result = ForecastAccuracy.Compute([]);
        Assert.Equal(0, result.Cycles);
        Assert.Null(result.Mape);
        Assert.Null(result.DaysError);
    }

    [Fact]
    public void Error_De_Dias_Separado_De_Mape()
    {
        var cycles = new[]
        {
            new ClosedCycleInput(
                ActualYieldKg: 50m,
                ActualHarvestDate: new DateOnly(2026, 2, 14),
                Predictions:
                [
                    // Predicción con rendimiento estimado pero SIN fecha → MAPE sí, días no.
                    Pred(new DateOnly(2026, 2, 10), 55m, null)
                ])
        };

        var result = ForecastAccuracy.Compute(cycles);
        Assert.Equal(10.0m, result.Mape);
        Assert.Null(result.DaysError);
    }

    [Fact]
    public void Efectiva_Usa_AsOfDate_O_Fecha_De_Generacion()
    {
        var p = new PredictionInput(null, new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc), 10m, null);
        Assert.Equal(new DateOnly(2026, 3, 5), ForecastAccuracy.EffectiveDate(p));

        var p2 = new PredictionInput(new DateOnly(2026, 3, 9), new DateTime(2026, 3, 12), 10m, null);
        Assert.Equal(new DateOnly(2026, 3, 9), ForecastAccuracy.EffectiveDate(p2));
    }
}