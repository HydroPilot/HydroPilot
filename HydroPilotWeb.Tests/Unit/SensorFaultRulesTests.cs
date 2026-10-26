using HydroPilotWeb.Models;
using HydroPilotWeb.Services.SensorFaults;

namespace HydroPilotWeb.Tests.Unit;

/// <summary>Errores de sensores: rango normal, saltos, falta de datos, cuándo se vuelve a abrir y los textos de las alertas.</summary>
public class SensorFaultRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SensorFaultLimits Ph = new(AnomalyContract.SensorTypePh, 4.0m, 8.0m, 0.5m);
    private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(10);

    [Theory]
    [InlineData(3.9, true)]
    [InlineData(4.0, false)]
    [InlineData(4.8, false)]   // zona crítica (5–7) pero posible: lo corrige el control, no es error de sensor
    [InlineData(8.0, false)]
    [InlineData(8.1, true)]
    public void Fuera_del_rango_normal(double value, bool expected) =>
        Assert.Equal(expected, SensorFaultRules.IsOutOfRange((decimal)value, Ph));

    [Fact]
    public void Salto_mayor_al_maximo_entre_lecturas_seguidas()
    {
        var previous = new PreviousReading(6.0m, Now.AddSeconds(-15), Usable: true);
        Assert.True(SensorFaultRules.IsJump(previous, 5.4m, Now, Ph, MaxGap, doseInBetween: false));
        Assert.False(SensorFaultRules.IsJump(previous, 5.6m, Now, Ph, MaxGap, doseInBetween: false));   // 0,4: normal
    }

    [Fact]
    public void Salto_no_cuenta_con_mucho_tiempo_entre_lecturas_o_con_una_dosis_en_el_medio()
    {
        var old = new PreviousReading(6.0m, Now.AddMinutes(-30), Usable: true);
        Assert.False(SensorFaultRules.IsJump(old, 5.2m, Now, Ph, MaxGap, doseInBetween: false));

        var recent = new PreviousReading(6.0m, Now.AddSeconds(-15), Usable: true);
        Assert.False(SensorFaultRules.IsJump(recent, 5.2m, Now, Ph, MaxGap, doseInBetween: true));
    }

    [Fact]
    public void Volver_de_un_valor_fuera_de_rango_no_es_un_salto()
    {
        var outOfRange = new PreviousReading(3.0m, Now.AddSeconds(-15), Usable: true);
        Assert.False(SensorFaultRules.IsJump(outOfRange, 6.0m, Now, Ph, MaxGap, doseInBetween: false));

        var invalid = new PreviousReading(6.0m, Now.AddSeconds(-15), Usable: false);
        Assert.False(SensorFaultRules.IsJump(invalid, 5.0m, Now, Ph, MaxGap, doseInBetween: false));
    }

    [Fact]
    public void Rango_se_abre_salvo_que_la_racha_ya_la_haya_resuelto_alguien()
    {
        // Primera vez (nunca hubo error de rango) o venía en rango: se abre.
        Assert.True(SensorFaultRules.ShouldOpenRange(previousOutOfRange: false, Now.AddSeconds(-15), lastRangeDetectedUtc: null));
        Assert.True(SensorFaultRules.ShouldOpenRange(previousOutOfRange: true, Now.AddSeconds(-15), lastRangeDetectedUtc: null));
        Assert.True(SensorFaultRules.ShouldOpenRange(previousOutOfRange: false, Now.AddSeconds(-15), Now.AddHours(-1)));

        // Alguien resolvió el error y las lecturas siguieron fuera: se toma como real, no se vuelve a frenar.
        Assert.False(SensorFaultRules.ShouldOpenRange(previousOutOfRange: true, Now.AddSeconds(-15), Now.AddMinutes(-5)));
    }

    [Fact]
    public void Lapso_no_confiable_va_desde_la_lectura_que_lo_disparo_hasta_que_se_resolvio()
    {
        var resolved = new UntrustedPeriod(1, Now.AddMinutes(-10), Now.AddMinutes(-5));
        Assert.True(resolved.Covers(Now.AddMinutes(-10)));
        Assert.True(resolved.Covers(Now.AddMinutes(-7)));
        Assert.False(resolved.Covers(Now.AddMinutes(-11)));
        Assert.False(resolved.Covers(Now.AddMinutes(-4)));
        Assert.False(resolved.IsOpen);

        var open = new UntrustedPeriod(1, Now.AddMinutes(-10), null);
        Assert.True(open.IsOpen);
        Assert.True(open.Covers(Now));
    }

    [Fact]
    public void Sin_datos_despues_de_dos_intervalos_del_nodo()
    {
        var silence = SensorFaultRules.SilenceAfter(300, 2);
        Assert.Equal(TimeSpan.FromMinutes(10), silence);
        Assert.False(SensorFaultRules.IsSilent(Now.AddMinutes(-9), Now, silence));
        Assert.True(SensorFaultRules.IsSilent(Now.AddMinutes(-11), Now, silence));
        Assert.True(SensorFaultRules.IsSilent(null, Now, silence));
    }

    [Theory]
    [InlineData(AnomalyContract.SensorTypePh, true)]
    [InlineData(AnomalyContract.SensorTypeCe, true)]
    [InlineData(AnomalyContract.SensorTypeTemperatura, false)]
    [InlineData(AnomalyContract.SensorTypeHumedad, false)]
    public void Solo_pH_y_CE_frenan_el_control(string type, bool expected) =>
        Assert.Equal(expected, SensorFaultRules.BlocksControl(type));

    [Fact]
    public void La_falta_de_datos_se_resuelve_sola_y_los_errores_por_valor_los_resuelve_una_persona()
    {
        Assert.False(SensorFaultRules.ResolvedByUser(SensorFaultKinds.SinDatos));
        Assert.True(SensorFaultRules.ResolvedByUser(SensorFaultKinds.FueraDeRango));
        Assert.True(SensorFaultRules.ResolvedByUser(SensorFaultKinds.Salto));
    }

    [Fact]
    public void Huella_de_la_alerta_lleva_circuito_y_error()
    {
        var fingerprint = SensorFaultAlerts.Fingerprint(3, 41);
        Assert.Equal("sensor-fault-3-41", fingerprint);
        Assert.Equal(3, SensorFaultAlerts.CircuitId(fingerprint));
        Assert.Equal(41, SensorFaultAlerts.FaultId(fingerprint));
        // Sensor del invernadero (sin circuito).
        Assert.Equal("sensor-fault-0-42", SensorFaultAlerts.Fingerprint(null, 42));
        Assert.Null(SensorFaultAlerts.CircuitId("sensor-fault-0-42"));
        Assert.Null(SensorFaultAlerts.CircuitId("node-offline-3-20261026120000"));
    }

    [Fact]
    public void Valores_de_fabrica_el_pH_normal_es_mas_ancho_que_la_zona_critica()
    {
        var ph = SensorFaultRules.Defaults.Single(d => d.SensorType == AnomalyContract.SensorTypePh);
        Assert.Equal((4.0m, 8.0m, 0.5m), (ph.Min, ph.Max, ph.Jump));
        Assert.True(ph.Min < HydroPilotWeb.Models.Control.ControlContract.CriticalPhMin);
        Assert.True(ph.Max > HydroPilotWeb.Models.Control.ControlContract.CriticalPhMax);
    }

    [Fact]
    public void Alerta_de_pH_avisa_que_el_control_queda_frenado()
    {
        var fault = new SensorFault
        {
            SensorType = AnomalyContract.SensorTypePh, Kind = SensorFaultKinds.Salto,
            PreviousValue = 6.02m, Value = 4.1m, Detail = SensorFaultRules.JumpDetail(Ph), DetectedAtUtc = Now,
        };
        var (title, message) = SensorFaultAlerts.Text(fault, "rpi-inv-01", TimeZoneInfo.Utc);

        Assert.Equal("rpi-inv-01: salto de pH (6,02 → 4,1)", title);
        Assert.StartsWith("Más de 0,5 entre lecturas.", message);
        Assert.Contains("El control automático de pH queda frenado hasta que lo marques como resuelto", message);
    }

    [Fact]
    public void Alerta_de_temperatura_solo_avisa()
    {
        var limits = new SensorFaultLimits(AnomalyContract.SensorTypeTemperatura, 10m, 40m, 5m);
        var fault = new SensorFault
        {
            SensorType = AnomalyContract.SensorTypeTemperatura, Kind = SensorFaultKinds.FueraDeRango,
            Value = 45m, Detail = SensorFaultRules.RangeDetail(limits), DetectedAtUtc = Now,
        };
        var (title, message) = SensorFaultAlerts.Text(fault, "rpi-inv-01", TimeZoneInfo.Utc);

        Assert.Equal("rpi-inv-01: temperatura inusual (45 °C)", title);
        Assert.Equal("Rango normal 10–40 °C. Revisá el sensor y el invernadero.", message);
    }

    [Fact]
    public void Alerta_de_sensor_sin_datos_dice_desde_cuando()
    {
        var fault = new SensorFault
        {
            SensorType = AnomalyContract.SensorTypeCe, Kind = SensorFaultKinds.SinDatos,
            DetectedAtUtc = Now, LastReadingAtUtc = new DateTime(2026, 10, 26, 11, 40, 0, DateTimeKind.Utc),
        };
        var (title, message) = SensorFaultAlerts.Text(fault, "rpi-inv-01", TimeZoneInfo.Utc);

        Assert.Equal("rpi-inv-01: sensor de CE sin datos", title);
        Assert.Equal("Sin lecturas desde el 26/10 11:40. El control automático de CE queda frenado hasta que vuelvan las lecturas.", message);
    }
}
