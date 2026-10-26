using HydroPilotWeb.Models.Control;
using HydroPilotWeb.Services.Control;

namespace HydroPilotWeb.Tests.Unit;

/// <summary>
/// Reglas del control de pH/CE: validación de la dosis manual, lo que cuenta para el máximo de 24 h, el
/// resultado que informa la Pi (idempotente) y la conversación de la vista Control. Lógica pura.
/// </summary>
public class ControlRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 13, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------
    // Dosis manual
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(ControlContract.KindPhUp, ControlContract.ParameterPh)]
    [InlineData(ControlContract.KindPhDown, ControlContract.ParameterPh)]
    [InlineData(ControlContract.KindNutrient, ControlContract.ParameterEc)]
    [InlineData(ControlContract.KindWater, ControlContract.ParameterEc)]
    public void Cada_bomba_corrige_su_magnitud(string kind, string parameter) =>
        Assert.Equal(parameter, ControlRules.ParameterFor(kind));

    [Fact]
    public void Dosis_valida_dentro_de_los_limites() =>
        Assert.Null(Validate(amount: 20m, used: 200m));

    [Theory]
    [InlineData(0, "mayor a 0")]
    [InlineData(-5, "mayor a 0")]
    [InlineData(51, "Máximo 50 ml por dosis")]
    public void Rechaza_cantidades_fuera_de_rango(decimal amount, string expected) =>
        Assert.Contains(expected, Validate(amount: amount));

    [Fact]
    public void Rechaza_si_supera_el_maximo_de_24_horas()
    {
        var error = Validate(amount: 40m, used: 220m);
        Assert.Contains("250 ml en 24 h", error);
        Assert.Contains("quedan 30 ml", error);
    }

    [Fact]
    public void Rechaza_con_nodo_apagado_bomba_desactivada_o_dosis_en_curso()
    {
        Assert.Contains("apagado", Validate(mode: ControlContract.ModeOff));
        Assert.Contains("desactivada", Validate(active: false));
        Assert.Contains("en curso", Validate(inFlight: true));
    }

    [Fact]
    public void Modo_automatico_tambien_acepta_dosis_manual() =>
        Assert.Null(Validate(mode: ControlContract.ModeAuto));

    [Theory]
    [InlineData(ControlContract.StatusExecuted, 20, null, 20)]
    [InlineData(ControlContract.StatusExecuted, 20, 18.5, 18.5)]
    [InlineData(ControlContract.StatusFailed, 20, 7.0, 7)]
    [InlineData(ControlContract.StatusFailed, 20, null, 0)]
    [InlineData(ControlContract.StatusPending, 20, null, 20)]
    [InlineData(ControlContract.StatusDelivered, 20, null, 20)]
    [InlineData(ControlContract.StatusUnconfirmed, 20, null, 20)]
    [InlineData(ControlContract.StatusExpired, 20, null, 0)]
    [InlineData(ControlContract.StatusRejected, 20, null, 0)]
    [InlineData(ControlContract.StatusCancelled, 20, null, 0)]
    public void Cuenta_para_el_maximo_lo_que_pudo_aplicarse(string status, decimal amount, double? executed, decimal expected) =>
        Assert.Equal(expected, ControlRules.UsedAmount(status, amount, executed is { } e ? (decimal)e : null));

    // ------------------------------------------------------------------
    // Resultado de la Pi
    // ------------------------------------------------------------------

    [Fact]
    public void Resultado_ejecutado_sin_cantidad_toma_la_pedida()
    {
        var check = Check(ControlContract.StatusDelivered, new("executed", null, null, null, null));
        Assert.Equal(ControlRules.ResultOutcome.Applied, check.Outcome);
        Assert.Equal(ControlContract.StatusExecuted, check.Status);
        Assert.Equal(20m, check.ExecutedAmount);
    }

    [Fact]
    public void Resultado_repetido_es_duplicado_y_distinto_es_conflicto()
    {
        Assert.Equal(ControlRules.ResultOutcome.Duplicate,
            Check(ControlContract.StatusExecuted, new("EXECUTED", 20m, null, null, null), executed: 20m).Outcome);
        Assert.Equal(ControlRules.ResultOutcome.Conflict,
            Check(ControlContract.StatusExecuted, new("EXECUTED", 15m, null, null, null), executed: 20m).Outcome);
        Assert.Equal(ControlRules.ResultOutcome.Conflict,
            Check(ControlContract.StatusExecuted, new("REJECTED", null, null, null, "vencido"), executed: 20m).Outcome);
    }

    [Theory]
    [InlineData(ControlContract.StatusExpired)]
    [InlineData(ControlContract.StatusUnconfirmed)]
    [InlineData(ControlContract.StatusPending)]
    public void Resultado_tardio_se_acepta_porque_es_lo_que_paso_en_el_tanque(string status) =>
        Assert.Equal(ControlRules.ResultOutcome.Applied, Check(status, new("EXECUTED", 20m, null, null, null)).Outcome);

    [Fact]
    public void Resultado_sobre_un_comando_cancelado_es_conflicto() =>
        Assert.Equal(ControlRules.ResultOutcome.Conflict,
            Check(ControlContract.StatusCancelled, new("EXECUTED", 20m, null, null, null)).Outcome);

    [Theory]
    [InlineData("DONE", null)]
    [InlineData(null, null)]
    [InlineData("EXECUTED", -1.0)]
    [InlineData("EXECUTED", 25.0)]
    [InlineData("REJECTED", 5.0)]
    public void Resultado_invalido(string? status, double? executed) =>
        Assert.Equal(ControlRules.ResultOutcome.Invalid,
            Check(ControlContract.StatusDelivered, new(status, executed is { } e ? (decimal)e : null, null, null, null)).Outcome);

    [Fact]
    public void Aplicar_resultado_cierra_el_comando()
    {
        var command = new ActuatorCommand { Amount = 20m, Status = ControlContract.StatusPending };
        var result = new ControlRules.DeviceResult("FAILED", 7m, Now.AddSeconds(-5), Now.AddSeconds(-2), "  bomba trabada  ");
        var check = ControlRules.CheckResult(command.Status, command.Amount, command.ExecutedAmount, result);

        ControlRules.ApplyResult(command, check, result, Now);

        Assert.Equal(ControlContract.StatusFailed, command.Status);
        Assert.Equal(7m, command.ExecutedAmount);
        Assert.Equal("bomba trabada", command.ResultError);
        Assert.Equal(Now, command.CompletedAtUtc);
        Assert.Equal(Now, command.DeliveredAtUtc); // llegó el resultado sin pasar por la consulta
    }

    // ------------------------------------------------------------------
    // Nivel vigente del lote
    // ------------------------------------------------------------------

    [Fact]
    public void Nivel_nuevo_solo_si_cambia_algo()
    {
        var current = Setpoint(ecTarget: 1.5m, stageId: 2);
        Assert.True(ControlRules.SetpointChanged(null, current));
        Assert.False(ControlRules.SetpointChanged(Setpoint(ecTarget: 1.5m, stageId: 2), current));
        Assert.True(ControlRules.SetpointChanged(Setpoint(ecTarget: 1.7m, stageId: 2), current));
        Assert.True(ControlRules.SetpointChanged(Setpoint(ecTarget: 1.5m, stageId: 3), current));
    }

    // ------------------------------------------------------------------
    // Conversación
    // ------------------------------------------------------------------

    [Fact]
    public void Conversacion_junta_lecturas_seguidas_y_ordena_los_mensajes()
    {
        var readings = new[]
        {
            Reading(-10, 6.80m, 1.48m),
            Reading(-9, 6.82m, 1.47m),
            Reading(-2, 6.40m, 1.47m),
            Reading(-1, 6.21m, 1.46m),
        }.SelectMany(r => r).ToList();
        var command = Command(createdMinutes: -8, deliveredMinutes: -7.5, completedMinutes: -7, ControlContract.StatusExecuted);

        var timeline = ControlRules.BuildTimeline(readings, [command], maxEntries: 40);

        Assert.Equal(
            [ControlTimelineKind.Readings, ControlTimelineKind.Result, ControlTimelineKind.Delivered,
                ControlTimelineKind.Requested, ControlTimelineKind.Readings],
            timeline.Select(e => e.Kind));

        var latest = timeline[0].Readings!;
        Assert.Equal(2, latest.Count);
        Assert.Equal(6.21m, latest.Ph); // la más reciente
        Assert.Equal(Now.AddMinutes(-2), latest.FirstUtc);
        Assert.Equal(Now.AddMinutes(-1), latest.LastUtc);

        Assert.Equal(2, timeline[^1].Readings!.Count);
    }

    [Fact]
    public void Conversacion_corta_en_el_maximo()
    {
        var commands = Enumerable.Range(1, 10)
            .Select(i => Command(createdMinutes: -i * 10, deliveredMinutes: null, completedMinutes: -i * 10 + 5, ControlContract.StatusExpired))
            .ToList();

        var timeline = ControlRules.BuildTimeline([], commands, maxEntries: 5);

        Assert.Equal(5, timeline.Count);
        Assert.Equal(ControlTimelineKind.Expired, timeline[0].Kind);
    }

    // ------------------------------------------------------------------
    // Auxiliares
    // ------------------------------------------------------------------

    private static string? Validate(
        decimal amount = 10m,
        decimal used = 0m,
        string mode = ControlContract.ModeManual,
        bool active = true,
        bool inFlight = false) =>
        ControlRules.ValidateManualDose(mode, active, inFlight, amount, 50m, 250m, used, ControlContract.UnitMl);

    private static ControlRules.ResultCheck Check(string commandStatus, ControlRules.DeviceResult result, decimal? executed = null) =>
        ControlRules.CheckResult(commandStatus, 20m, executed, result);

    private static LotSetpointHistory Setpoint(decimal ecTarget, int stageId) => new()
    {
        PhMin = 5.5m, PhTarget = 6.0m, PhMax = 6.5m,
        EcMin = 1.2m, EcTarget = ecTarget, EcMax = 1.8m,
        StageId = stageId,
    };

    private static IEnumerable<ControlRules.ReadingPoint> Reading(int minutes, decimal ph, decimal ec) =>
    [
        new(Now.AddMinutes(minutes), ControlContract.ParameterPh, ph),
        new(Now.AddMinutes(minutes), ControlContract.ParameterEc, ec),
    ];

    private static ControlCommandDto Command(double createdMinutes, double? deliveredMinutes, double? completedMinutes, string status) =>
        new(Guid.NewGuid(), 1, ControlContract.KindPhDown, "pH Down", 20m, ControlContract.UnitMl, status,
            ControlContract.SourceManual, "admin", Now.AddMinutes(createdMinutes), Now.AddMinutes(createdMinutes + 5),
            deliveredMinutes is { } d ? Now.AddMinutes(d) : null,
            completedMinutes is { } c ? Now.AddMinutes(c) : null,
            status == ControlContract.StatusExecuted ? 20m : null, null, ControlContract.ParameterPh, 6.8m, 6.0m);
}
