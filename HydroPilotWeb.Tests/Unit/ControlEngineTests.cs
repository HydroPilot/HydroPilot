using HydroPilotWeb.Models.Control;
using HydroPilotWeb.Services.Anomalies;
using HydroPilotWeb.Services.Control;
using static HydroPilotWeb.Services.Control.ControlEngine;

namespace HydroPilotWeb.Tests.Unit;

/// <summary>
/// Motor del control automático: decisión (orden, esperas, bloqueos), tamaño de la dosis con lógica difusa,
/// efecto medido y autocalibración. Lógica pura, tanque de 500 L.
/// </summary>
public class ControlEngineTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 13, 0, 0, DateTimeKind.Utc);
    private static readonly SolutionBand PhBand = new(5.5m, 6.5m, 6.0m);
    private static readonly SolutionBand EcBand = new(1.2m, 1.8m, 1.5m);
    private static readonly Settings Defaults = new(MixingMinutes: 10, DirectionChangeMinutes: 30, PollStaleMinutes: 2, TrendMinutes: 30);

    // ------------------------------------------------------------------
    // Lógica difusa
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1.1, 0.0, 0.3)]   // leve y estable → suave
    [InlineData(1.1, -2.0, 0.6)]  // leve y alejándose → media
    [InlineData(1.1, 2.0, 0.0)]   // leve y acercándose → nada (se corrige solo)
    [InlineData(1.75, 0.0, 0.6)]  // medio y estable → media
    [InlineData(1.75, -2.0, 0.9)] // medio y alejándose → fuerte
    [InlineData(3.0, 0.0, 0.9)]   // grande y estable → fuerte
    [InlineData(3.0, 2.0, 0.6)]   // grande y acercándose → media
    public void Fuerza_de_la_dosis_segun_desvio_y_tendencia(double deviation, double trend, double expected) =>
        Assert.Equal((decimal)expected, Strength((decimal)deviation, (decimal)trend));

    [Fact]
    public void Fuerza_nunca_llega_al_cien_por_ciento() =>
        Assert.True(Strength(10m, -10m) <= 0.9m);

    [Fact]
    public void Fuerza_crece_con_el_desvio()
    {
        var previous = 0m;
        foreach (var deviation in new[] { 1.0m, 1.5m, 2.0m, 2.5m, 3.0m })
        {
            var strength = Strength(deviation, 0m);
            Assert.True(strength >= previous);
            previous = strength;
        }
    }

    [Fact]
    public void Desvio_se_mide_en_bandas_desde_el_objetivo()
    {
        Assert.Equal(1m, DeviationRatio(6.5m, PhBand));
        Assert.Equal(2m, DeviationRatio(7.0m, PhBand));
        Assert.Equal(1.2m, DeviationRatio(5.4m, PhBand));
    }

    [Fact]
    public void Tendencia_positiva_si_se_acerca_al_objetivo()
    {
        // pH 6,8 (arriba del objetivo) bajando 0,5 por hora: se acerca 1 media banda por hora.
        Assert.Equal(1m, TrendRatio(6.8m, 6.0m, PhBand, -0.5m));
        Assert.Equal(-1m, TrendRatio(6.8m, 6.0m, PhBand, 0.5m));
        Assert.Equal(0m, TrendRatio(6.8m, 6.0m, PhBand, null));
    }

    [Fact]
    public void Tendencia_por_minimos_cuadrados_necesita_cinco_minutos()
    {
        var rising = Enumerable.Range(0, 7).Select(i => new Point(Now.AddMinutes(i), 6.0m + 0.01m * i)).ToList();
        Assert.Equal(0.6m, TrendPerHour(rising)); // 0,01 por minuto

        var tooShort = Enumerable.Range(0, 5).Select(i => new Point(Now.AddSeconds(i * 30), 6.0m + 0.01m * i)).ToList();
        Assert.Null(TrendPerHour(tooShort));
        Assert.Null(TrendPerHour(rising.Take(2).ToList()));
    }

    // ------------------------------------------------------------------
    // Dosis
    // ------------------------------------------------------------------

    [Fact]
    public void Dosis_completa_por_bomba()
    {
        // pH 6,52 → 6,0 con 2,5 de efecto por ml/L en 500 L: 0,52 × 500 ÷ 2,5 = 104 ml.
        Assert.Equal(104m, RawDose(ControlContract.KindPhDown, 6.52m, 6.0m, 500m, 2.5m, 0.2m));
        Assert.Equal(100m, RawDose(ControlContract.KindPhUp, 5.5m, 6.0m, 500m, 2.5m, 0.2m));
        // CE 1,1 → 1,5 con 0,3 por ml/L: 0,4 × 500 ÷ 0,3 ≈ 666,67 ml.
        Assert.Equal(666.67m, Math.Round(RawDose(ControlContract.KindNutrient, 1.1m, 1.5m, 500m, 0.3m, 0.2m)!.Value, 2));
        // CE 2,0 → 1,7 con agua de 0,2: 500 × 0,3 ÷ 1,5 = 100 L.
        Assert.Equal(100m, RawDose(ControlContract.KindWater, 2.0m, 1.7m, 500m, 0m, 0.2m));
        // El agua no puede bajar la CE por debajo de la del agua de red.
        Assert.Null(RawDose(ControlContract.KindWater, 0.5m, 0.15m, 500m, 0m, 0.2m));
    }

    [Fact]
    public void Efecto_esperado_con_signo()
    {
        Assert.Equal(-0.25m, ExpectedEffect(ControlContract.KindPhDown, 50m, 500m, 2.5m, 6.8m, 0.2m));
        Assert.Equal(0.25m, ExpectedEffect(ControlContract.KindPhUp, 50m, 500m, 2.5m, 5.2m, 0.2m));
        Assert.Equal(0.3m, ExpectedEffect(ControlContract.KindNutrient, 500m, 500m, 0.3m, 1.1m, 0.2m));
        Assert.Equal(-0.1636m, ExpectedEffect(ControlContract.KindWater, 50m, 500m, 0m, 2.0m, 0.2m));
    }

    // ------------------------------------------------------------------
    // Decisión
    // ------------------------------------------------------------------

    [Fact]
    public void En_manual_el_motor_no_hace_nada() =>
        Assert.Equal(Outcome.Off, Decide(Tank(mode: ControlContract.ModeManual, ph: Out(6.9m))).Outcome);

    [Fact]
    public void En_rango_no_dosifica()
    {
        var decision = Decide(Tank());
        Assert.Equal((Outcome.InBand, Codes.InBand), (decision.Outcome, decision.Code));
    }

    [Fact]
    public void pH_alto_dosifica_pH_Down_recortado_al_maximo_por_dosis()
    {
        // pH 6,9 (1,8 bandas), estable: fuerza ≈ 0,6 → 0,9 × 500 ÷ 2,5 = 180 ml × fuerza → tope 50 ml.
        var decision = Decide(Tank(ph: Out(6.9m)));

        Assert.Equal(Outcome.Dose, decision.Outcome);
        Assert.Equal(ControlContract.ParameterPh, decision.Parameter);
        Assert.Equal(ControlContract.KindPhDown, decision.Pump!.Kind);
        Assert.Equal(50m, decision.Amount);
        Assert.Equal(180m, decision.RawAmount);
        Assert.Equal(-0.25m, decision.ExpectedEffect);
        Assert.Equal((6.9m, 6.0m), (decision.Value!.Value, decision.Target!.Value));
    }

    [Fact]
    public void Dosis_chica_aplica_solo_una_parte_y_al_menos_un_paso()
    {
        // pH 6,55 apenas arriba, estable: 0,55 × 500 ÷ 2,5 = 110 ml × 0,3 = 33 ml.
        var decision = Decide(Tank(ph: Out(6.55m)));
        Assert.Equal(0.3m, decision.Strength);
        Assert.Equal(33m, decision.Amount);
    }

    [Fact]
    public void CE_antes_que_pH_salvo_pH_critico()
    {
        Assert.Equal(ControlContract.ParameterEc, Decide(Tank(ph: Out(6.9m), ec: Out(1.0m))).Parameter);
        Assert.Equal(ControlContract.ParameterPh, Decide(Tank(ph: Out(7.3m), ec: Out(1.0m))).Parameter);
    }

    [Fact]
    public void CE_alta_se_baja_con_agua_y_baja_con_nutrientes()
    {
        Assert.Equal(ControlContract.KindWater, Decide(Tank(ec: Out(2.0m))).Pump!.Kind);
        Assert.Equal(ControlContract.KindNutrient, Decide(Tank(ec: Out(1.0m))).Pump!.Kind);
    }

    [Fact]
    public void Sin_lote_o_sin_consultas_de_la_Pi_no_dosifica()
    {
        Assert.Equal(Codes.NoLot, Decide(Tank(ph: Out(6.9m), hasLot: false)).Code);
        Assert.Equal(Codes.PiSilent, Decide(Tank(ph: Out(6.9m), lastPoll: Now.AddMinutes(-3))).Code);
        Assert.Equal(Codes.PiSilent, Decide(Tank(ph: Out(6.9m), neverPolled: true)).Code);
    }

    [Fact]
    public void Espera_si_hay_dosis_en_curso_o_mezclando()
    {
        Assert.Equal(Codes.InFlight, Decide(Tank(ph: Out(6.9m), inFlight: true)).Code);

        var mixing = Decide(Tank(ph: Out(6.9m), doses: [new(ControlContract.KindPhDown, Now.AddMinutes(-4))]));
        Assert.Equal((Outcome.Waiting, Codes.Mixing), (mixing.Outcome, mixing.Code));
        Assert.Equal(Now.AddMinutes(6), mixing.UntilUtc);
    }

    [Fact]
    public void Despues_de_mezclar_necesita_dos_lecturas_nuevas()
    {
        AppliedDose[] doses = [new(ControlContract.KindPhDown, Now.AddMinutes(-11))];

        var one = Decide(Tank(ph: Out(6.9m, readingsAfter: Now.AddSeconds(-30)), doses: doses));
        Assert.Equal(Codes.AwaitReading, one.Code);

        var two = Decide(Tank(ph: Out(6.9m, readingsAfter: Now.AddSeconds(-50), count: 2),
            ec: InBand(1.5m, readingsAfter: Now.AddSeconds(-50), count: 2), doses: doses));
        Assert.Equal(Outcome.Dose, two.Outcome);
    }

    [Fact]
    public void No_dosifica_en_sentido_contrario_a_una_dosis_reciente()
    {
        // pH Down hace 12 min (ya mezclado) y ahora el pH quedó bajo: pH Up recién a los 30 min.
        var decision = Decide(Tank(
            ph: Out(5.3m, readingsAfter: Now.AddMinutes(-1), count: 2),
            ec: InBand(1.5m, readingsAfter: Now.AddMinutes(-1), count: 2),
            doses: [new(ControlContract.KindPhDown, Now.AddMinutes(-12))]));
        Assert.Equal((Outcome.Blocked, Codes.Direction), (decision.Outcome, decision.Code));
        Assert.Equal(Now.AddMinutes(18), decision.UntilUtc);
    }

    [Fact]
    public void Respeta_el_maximo_de_24_horas()
    {
        Assert.Equal(Codes.DailyLimit, Decide(Tank(ph: Out(6.9m), phDownUsed: 250m)).Code);
        Assert.Equal(20m, Decide(Tank(ph: Out(6.9m), phDownUsed: 230m)).Amount);
    }

    [Fact]
    public void Bomba_desactivada_bloquea()
    {
        var decision = Decide(Tank(ph: Out(6.9m), phDownActive: false));
        Assert.Equal((Outcome.Blocked, Codes.PumpOff), (decision.Outcome, decision.Code));
    }

    [Fact]
    public void Si_ya_vuelve_solo_espera()
    {
        // pH 6,6 (leve) bajando rápido hacia el objetivo: fuerza nula.
        var trend = Enumerable.Range(0, 11).Select(i => new Point(Now.AddMinutes(-10 + i), 6.8m - 0.02m * i)).ToList();
        var decision = Decide(Tank(ph: new Magnitude(Status(6.6m, PhBand, SolutionState.Lower), trend, Paused: false)));
        Assert.Equal((Outcome.Waiting, Codes.Approaching), (decision.Outcome, decision.Code));
    }

    [Fact]
    public void Magnitud_pausada_no_se_dosifica_y_sigue_la_otra()
    {
        var paused = Decide(Tank(ph: Out(6.9m) with { Paused = true }));
        Assert.Equal((Outcome.Paused, ControlContract.ParameterPh), (paused.Outcome, paused.Parameter));

        var other = Decide(Tank(ph: Out(6.9m) with { Paused = true }, ec: Out(1.0m)));
        Assert.Equal(ControlContract.KindNutrient, other.Pump!.Kind);
    }

    [Fact]
    public void Lectura_sin_confirmar_o_vieja_no_dosifica()
    {
        Assert.Equal(Codes.Unconfirmed, Decide(Tank(ph: new Magnitude(Status(6.9m, PhBand, SolutionState.Unconfirmed), [], false))).Code);
        Assert.Equal(Codes.NoData, Decide(Tank(ph: new Magnitude(Status(6.9m, PhBand, SolutionState.Stale), [], false))).Code);
    }

    // ------------------------------------------------------------------
    // Efecto y calibración
    // ------------------------------------------------------------------

    [Fact]
    public void Efecto_descuenta_la_deriva_previa()
    {
        // Esperado −0,25; el pH subía 1 por hora: en 12 min la deriva sumó +0,2.
        var measure = MeasureEffect(before: 6.9m, after: 6.8m, driftPerHour: 1m, hours: 0.2m, expected: -0.25m);
        Assert.Equal(-0.3m, measure.Observed);
        Assert.True(measure.Effective);
    }

    [Fact]
    public void Sin_efecto_si_no_se_movio_o_fue_al_reves()
    {
        Assert.False(MeasureEffect(6.9m, 6.89m, null, 0.2m, -0.25m).Effective);   // −0,01 < 20 % de 0,25
        Assert.False(MeasureEffect(6.9m, 7.0m, null, 0.2m, -0.25m).Effective);    // subió
        Assert.True(MeasureEffect(6.9m, 6.84m, null, 0.2m, -0.25m).Effective);    // −0,06 ≥ 0,05
    }

    [Fact]
    public void Calibracion_promedia_y_descarta_mediciones_raras()
    {
        // 50 ml en 500 L bajaron 0,3: efecto medido 3,0 por ml/L; 2,5 × 0,7 + 3,0 × 0,3 = 2,65.
        Assert.Equal(2.65m, Calibrate(ControlContract.KindPhDown, 2.5m, -0.3m, 50m, 500m));
        Assert.Equal(2.5m, Calibrate(ControlContract.KindPhDown, 2.5m, -0.01m, 50m, 500m)); // 0,1: menos de 1/4
        Assert.Null(Calibrate(ControlContract.KindWater, null, -0.2m, 50m, 500m));
    }

    [Fact]
    public void Pausa_tras_varias_dosis_seguidas_sin_efecto()
    {
        Assert.True(ShouldPause([false, false, false], 3));
        Assert.False(ShouldPause([false, false, true], 3));
        Assert.False(ShouldPause([false, false], 3));
    }

    [Fact]
    public void Magnitudes_controladas_en_automatico_menos_las_pausadas()
    {
        Assert.Empty(AutoControlled(ControlContract.ModeManual, []));
        Assert.Equal([ControlContract.ParameterEc], AutoControlled(ControlContract.ModeAuto, [ControlContract.ParameterPh]));
    }

    // ------------------------------------------------------------------
    // Auxiliares
    // ------------------------------------------------------------------

    private static SolutionParameterStatus Status(decimal value, SolutionBand band, SolutionState state) =>
        new(band == PhBand ? ControlContract.ParameterPh : ControlContract.ParameterEc, "u", value, Now.AddMinutes(-1), band, state, 5);

    /// <summary>Magnitud fuera de banda (firme), con lecturas estables en los últimos minutos.</summary>
    private static Magnitude Out(decimal value, DateTime? readingsAfter = null, int count = 1)
    {
        var band = IsEc(value) ? EcBand : PhBand;
        var state = value > band.Max ? SolutionState.Lower : SolutionState.Raise;
        return new Magnitude(Status(value, band, state), Readings(value, readingsAfter, count), Paused: false);
    }

    private static Magnitude InBand(decimal value, DateTime? readingsAfter = null, int count = 1)
    {
        var band = IsEc(value) ? EcBand : PhBand;
        return new Magnitude(Status(value, band, SolutionState.InRange), Readings(value, readingsAfter, count), Paused: false);
    }

    private static bool IsEc(decimal value) => value < 3m;

    private static IReadOnlyList<Point> Readings(decimal value, DateTime? after, int count) =>
        after is { } start
            ? Enumerable.Range(0, count).Select(i => new Point(start.AddSeconds(15 * i), value)).ToList()
            : [new Point(Now.AddMinutes(-1), value)];

    private static Input Tank(
        string mode = ControlContract.ModeAuto,
        Magnitude? ph = null,
        Magnitude? ec = null,
        bool hasLot = true,
        DateTime? lastPoll = null,
        bool neverPolled = false,
        bool inFlight = false,
        AppliedDose[]? doses = null,
        decimal phDownUsed = 0m,
        bool phDownActive = true) =>
        new(
            Now,
            mode,
            hasLot,
            neverPolled ? null : lastPoll ?? Now.AddSeconds(-10),
            500m,
            0.2m,
            ph ?? InBand(6.0m),
            ec ?? InBand(1.5m),
            [
                new(1, ControlContract.KindPhUp, "pH Up", ControlContract.UnitMl, 50m, 250m, true, 0m, 2.5m),
                new(2, ControlContract.KindPhDown, "pH Down", ControlContract.UnitMl, 50m, 250m, phDownActive, phDownUsed, 2.5m),
                new(3, ControlContract.KindNutrient, "Nutrientes", ControlContract.UnitMl, 500m, 2_000m, true, 0m, 0.3m),
                new(4, ControlContract.KindWater, "Agua", ControlContract.UnitLiters, 50m, 150m, true, 0m, null),
            ],
            inFlight,
            doses ?? [],
            Defaults);
}
