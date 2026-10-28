using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Tests.Unit;

/// <summary>
/// Previsión de Baby Leaf y pérdidas (histórico pesado por temporada × clima, estado actual del
/// lote, pérdidas por etapa) y corrección del pronóstico a la temperatura de adentro. Lógica pura.
/// </summary>
public class BabyLeafOutlookRulesTests
{
    private static LotCycleOutcome Cycle(
        DateOnly sowing, int apt, int total = 100, int early = 0, int late = 0, int cutAsBabyLeaf = 0, decimal? averageGdd = null) =>
        new(1, "Lote", sowing, total, cutAsBabyLeaf, 0, early + late, apt, early, late, averageGdd, IsManual: true);

    [Fact]
    public void Distancia_De_Temporada_No_Depende_Del_Anio()
    {
        Assert.Equal(21, BabyLeafOutlookRules.SeasonDistanceDays(new DateOnly(2025, 12, 20), new DateOnly(2026, 1, 10)));
        Assert.Equal(44, BabyLeafOutlookRules.SeasonDistanceDays(new DateOnly(2025, 4, 1), new DateOnly(2026, 5, 15)));
    }

    [Theory]
    [InlineData(10, 1.0)]
    [InlineData(30, 1.0)]
    [InlineData(44, 0.77)]
    [InlineData(60, 0.5)]
    [InlineData(90, 0.0)]
    [InlineData(150, 0.0)]
    public void Peso_Por_Temporada_Baja_De_30_A_90_Dias(int days, double expected)
    {
        Assert.Equal((decimal)expected, BabyLeafOutlookRules.SeasonWeight(days));
    }

    [Fact]
    public void Peso_Por_Clima_Baja_Con_La_Diferencia_De_Gdd_Diario()
    {
        Assert.Equal(1m, BabyLeafOutlookRules.ClimateWeight(10m, 10.8m));
        Assert.Equal(0.5m, BabyLeafOutlookRules.ClimateWeight(10m, 12.5m));
        Assert.Equal(0m, BabyLeafOutlookRules.ClimateWeight(10m, 15m));
        Assert.Equal(1m, BabyLeafOutlookRules.ClimateWeight(null, 12m)); // sin dato de clima: solo cuenta la temporada
    }

    [Fact]
    public void Historico_Pesa_Mas_Los_Lotes_De_La_Misma_Temporada()
    {
        // Escenario 15/5: A (1/4, peso 0,77), B (10/5, 1), C (15/1, 0: otra temporada), D (20/6, 0,9).
        var lots = new[]
        {
            Cycle(new DateOnly(2026, 4, 1), apt: 80),
            Cycle(new DateOnly(2026, 5, 10), apt: 70),
            Cycle(new DateOnly(2026, 1, 15), apt: 90),
            Cycle(new DateOnly(2026, 6, 20), apt: 65),
        };

        var history = BabyLeafOutlookRules.FromHistory(lots, new DateOnly(2026, 5, 15), null);

        Assert.False(history.UsedAllLots);
        Assert.Equal(3, history.Lots.Count);           // C no pesa
        Assert.Equal(0.712m, history.AptRate);          // (0,77×80 + 1×70 + 0,9×65) / 2,67 → 71,2 % (promedio simple: 76,25 %)
    }

    [Fact]
    public void Sin_Lotes_Parecidos_Usa_El_Promedio_De_Todos()
    {
        var lots = new[]
        {
            Cycle(new DateOnly(2026, 4, 1), apt: 80),
            Cycle(new DateOnly(2026, 5, 10), apt: 70),
        };

        var history = BabyLeafOutlookRules.FromHistory(lots, new DateOnly(2026, 10, 15), null);

        Assert.True(history.UsedAllLots);
        Assert.Equal(0.75m, history.AptRate);
    }

    [Fact]
    public void Con_Clima_Distinto_El_Lote_No_Pesa_Aunque_Sea_De_La_Temporada()
    {
        var lots = new[]
        {
            Cycle(new DateOnly(2026, 5, 10), apt: 80, averageGdd: 10m),
            Cycle(new DateOnly(2026, 5, 12), apt: 40, averageGdd: 16m), // 6 GDD/día de diferencia: no pesa
        };

        var history = BabyLeafOutlookRules.FromHistory(lots, new DateOnly(2026, 5, 15), expectedAverageDailyGdd: 10m);

        Assert.Single(history.Lots);
        Assert.Equal(0.8m, history.AptRate);
    }

    [Fact]
    public void Tasas_Del_Ciclo_Separan_Capacidad_Y_Perdidas_Por_Etapa()
    {
        // 20 plantas: 1 perdida temprano, 14 aptas en el óptimo, 12 cortadas como Baby Leaf, 2 perdidas esperando.
        var cycle = Cycle(new DateOnly(2026, 5, 1), apt: 14, total: 20, early: 1, late: 2, cutAsBabyLeaf: 12);

        Assert.Equal(Math.Round(14m / 19m, 4), Math.Round(cycle.AptRate!.Value, 4)); // de las vivas en el óptimo
        Assert.Equal(0.05m, cycle.EarlyLossRate);
        Assert.Equal(Math.Round(2m / 7m, 4), Math.Round(cycle.LateLossRate!.Value, 4)); // de las 7 que siguieron
    }

    [Fact]
    public void Con_Trasplante_La_Perdida_Tardia_Es_Sobre_Las_Trasplantadas()
    {
        // 200 plantas: 120 Baby Leaf, 40 trasplantadas (2 descartadas después), el resto del denso se descartó al
        // cerrar la ventana (sin lugar en Adultas): no son pérdidas tardías.
        var cycle = new LotCycleOutcome(1, "Lote", new DateOnly(2026, 5, 1), 200, 120, 38, 42, 130, 10, 2, null,
            IsManual: false, LateLossBase: 40);

        Assert.Equal(0.05m, cycle.LateLossRate);                       // 2 de 40, no 2 de 70
        Assert.Null((cycle with { LateLossBase = 0 }).LateLossRate);    // sin trasplantadas no hay dato
    }

    [Fact]
    public void Estado_Actual_Pesa_Segun_Cuanto_Falta_Para_El_Optimo()
    {
        // En desarrollo no pesa (como A26 en 245 GDD, con todas "En desarrollo"): solo el histórico.
        Assert.Equal(0m, BabyLeafOutlookRules.CurrentStateWeight(120m, 250m, 300m));
        Assert.Equal(0m, BabyLeafOutlookRules.CurrentStateWeight(245m, 250m, 317m));
        // Desde que abre la ventana crece hasta el óptimo.
        Assert.Equal(0m, BabyLeafOutlookRules.CurrentStateWeight(250m, 250m, 300m));
        Assert.Equal(0.6m, BabyLeafOutlookRules.CurrentStateWeight(280m, 250m, 300m));
        Assert.Equal(1m, BabyLeafOutlookRules.CurrentStateWeight(320m, 250m, 300m));

        // 87 % estado actual (63 %) + 13 % histórico (71 %) ≈ 64 %.
        Assert.Equal(0.6404m, BabyLeafOutlookRules.Blend(0.71m, 0.63m, 0.87m));
        Assert.Equal(0.71m, BabyLeafOutlookRules.Blend(0.71m, null, 0.87m)); // sin evaluaciones: solo el histórico
        Assert.Equal(0.63m, BabyLeafOutlookRules.Blend(null, 0.63m, 0.5m));  // sin histórico: el estado actual
    }

    [Fact]
    public void Perdidas_Que_Faltan_Segun_La_Etapa()
    {
        Assert.Equal(0.06m, BabyLeafOutlookRules.RemainingEarlyLoss(0.06m, 0m, 450m));
        Assert.Equal(0.0333m, BabyLeafOutlookRules.RemainingEarlyLoss(0.06m, 200m, 450m));
        Assert.Equal(0m, BabyLeafOutlookRules.RemainingEarlyLoss(0.06m, 460m, 450m));

        Assert.Equal(0.1m, BabyLeafOutlookRules.RemainingLateLoss(0.1m, 300m, 450m, 650m));  // todavía no empezó
        Assert.Equal(0.05m, BabyLeafOutlookRules.RemainingLateLoss(0.1m, 550m, 450m, 650m)); // va por la mitad
        Assert.Equal(0m, BabyLeafOutlookRules.RemainingLateLoss(null, 300m, 450m, 650m));
    }

    // --- Pronóstico corregido a la temperatura de adentro ---

    [Fact]
    public void Correccion_Del_Pronostico_Es_La_Diferencia_Promedio_De_Los_Dias_Completos()
    {
        var today = new DateOnly(2026, 10, 14);
        var measured = Enumerable.Range(1, 5)
            .Select(i => new DailyGddPoint(today.AddDays(-i), 20m + i % 2, GddPointSource.Observed)) // 21, 20, 21, 20, 21
            .Append(new DailyGddPoint(today, 5m, GddPointSource.Observed))                          // hoy (incompleto): no cuenta
            .ToList();
        var forecast = Enumerable.Range(0, 6).ToDictionary(i => today.AddDays(-i), _ => 15.5m);

        var correction = GddService.ComputeForecastCorrection(measured, forecast, today);

        Assert.NotNull(correction);
        Assert.Equal(5, correction.CalibrationDays);
        Assert.Equal(5.1m, correction.OffsetPerDay); // (5,5 + 4,5 + 5,5 + 4,5 + 5,5) / 5
        Assert.Equal(20.6m, GddService.CorrectedForecastGdd(15.5m, correction));
        Assert.Equal(0m, GddService.CorrectedForecastGdd(2m, new ForecastCorrection(-5m, 5))); // nunca negativo
    }

    [Fact]
    public void Sin_Dias_Suficientes_No_Se_Corrige()
    {
        var today = new DateOnly(2026, 10, 14);
        var measured = new[]
        {
            new DailyGddPoint(today.AddDays(-1), 20m, GddPointSource.Observed),
            new DailyGddPoint(today.AddDays(-2), 20m, GddPointSource.Observed),
        };
        var forecast = new Dictionary<DateOnly, decimal> { [today.AddDays(-1)] = 15m, [today.AddDays(-2)] = 15m };

        Assert.Null(GddService.ComputeForecastCorrection(measured, forecast, today));
        Assert.Equal(15m, GddService.CorrectedForecastGdd(15m, null));
    }
}
