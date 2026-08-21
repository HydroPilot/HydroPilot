using HydroPilotWeb.Models;
using HydroPilotWeb.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace HydroPilotWeb.Tests.Unit;

/// <summary>Reglas puras de calidad/validación (sin base de datos).</summary>
public class TelemetryValidationServiceTests
{
    private static TelemetryValidationService Create(TelemetryOptions? options = null)
    {
        var opts = options ?? new TelemetryOptions
        {
            FutureToleranceMinutes = 5,
            StaleMinAgeHours = 24,
            StaleIntervalFactor = 4,
            DegradedIntervalFactor = 2,
        };
        return new TelemetryValidationService(Options.Create(opts));
    }

    private static readonly DateTime Now = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    // ---------- Rango físico ----------

    [Theory]
    [InlineData("pH", 5.9, "VALID")]
    [InlineData("pH", 6.5, "VALID")]
    [InlineData("EC", 1.8, "VALID")]
    [InlineData("Temperatura", 24.0, "VALID")]
    [InlineData("Humedad", 68.0, "VALID")]
    public void Valor_dentro_de_rango_es_VALID(string sensorType, decimal value, string expected)
    {
        var svc = Create();
        var result = svc.Classify(sensorType, value, Now, "pH", "pH", null, Now, 300);
        Assert.Equal(expected, result.Quality);
        Assert.True(result.Persisted);
        Assert.Equal(TelemetryContract.ResultAceptada, result.Result);
    }

    [Theory]
    [InlineData("pH", 20.0)]      // físicamente imposible
    [InlineData("pH", -3.0)]
    [InlineData("Temperatura", 200.0)]
    [InlineData("Humedad", 150.0)]
    [InlineData("CE", -1.0)]
    public void Valor_fisicamente_imposible_es_FueraDeRango_INVALID(string sensorType, decimal value)
    {
        var svc = Create();
        var result = svc.Classify(sensorType, value, Now, null, "pH", null, Now, 300);

        Assert.Equal(TelemetryContract.ResultFueraDeRango, result.Result);
        Assert.Equal(TelemetryContract.QualityInvalid, result.Quality);
        Assert.Equal(TelemetryContract.ReasonFueraDeRango, result.Reason);
        // Persiste como cuarentena consultable (no entra a GDD por calidad).
        Assert.True(result.Persisted);
    }

    [Theory]
    [InlineData("pH", 3.5)]       // posible pero fuera de banda operativa
    [InlineData("pH", 8.0)]
    [InlineData("Temperatura", 5.0)]
    [InlineData("Humedad", 10.0)]
    public void Valor_posible_fuera_de_banda_es_SUSPECT(string sensorType, decimal value)
    {
        var svc = Create();
        var result = svc.Classify(sensorType, value, Now, null, null, null, Now, 300);

        Assert.Equal(TelemetryContract.QualitySuspect, result.Quality);
        Assert.Equal(TelemetryContract.ReasonFueraDeBanda, result.Reason);
        Assert.True(result.Persisted);
    }

    // ---------- Timestamps ----------

    [Fact]
    public void Timestamp_futuro_mas_alla_de_tolerancia_es_rechazado_FUTURE()
    {
        var svc = Create();
        var future = Now.AddMinutes(10);
        var result = svc.Classify("pH", 6.0m, future, null, null, null, Now, 300);

        Assert.Equal(TelemetryContract.ResultInvalida, result.Result);
        Assert.Equal(TelemetryContract.QualityFuture, result.Quality);
        Assert.Equal(TelemetryContract.ReasonTimestampFuturo, result.Reason);
        Assert.False(result.Persisted); // no persiste una medición que aún no ocurrió
    }

    [Fact]
    public void Timestamp_dentro_de_tolerancia_no_es_futuro()
    {
        var svc = Create();
        var future = Now.AddMinutes(4);
        var result = svc.Classify("pH", 6.0m, future, null, null, null, Now, 300);
        Assert.Equal(TelemetryContract.QualityValid, result.Quality);
        Assert.True(result.Persisted);
    }

    [Fact]
    public void Timestamp_antiguo_es_STALE_pero_persiste()
    {
        var svc = Create();
        var old = Now.AddDays(-2); // > 24h y > 4×300s
        var result = svc.Classify("pH", 6.0m, old, null, null, null, Now, 300);

        Assert.Equal(TelemetryContract.QualityStale, result.Quality);
        Assert.Equal(TelemetryContract.ReasonTimestampAnterior, result.Reason);
        Assert.True(result.Persisted);
    }

    [Theory]
    [InlineData(true, 300)]
    [InlineData(true, 3600)] // con intervalo grande, el umbral STALE sube más allá de 24h
    public void Timestamp_reciente_no_es_stale(bool _, int intervalSeconds)
    {
        var svc = Create();
        Assert.Equal(TelemetryContract.QualityValid,
            svc.Classify("pH", 6.0m, Now.AddHours(-5), null, null, null, Now, intervalSeconds).Quality);
    }

    // ---------- Estructura ----------

    [Fact]
    public void Valor_faltante_es_invalida_y_no_persiste()
    {
        var svc = Create();
        var result = svc.Classify("pH", null, Now, null, null, null, Now, 300);
        Assert.Equal(TelemetryContract.ResultInvalida, result.Result);
        Assert.Equal(TelemetryContract.ReasonValorFaltante, result.Reason);
        Assert.False(result.Persisted);
    }

    [Fact]
    public void ObservedAt_faltante_es_invalida_y_no_persiste()
    {
        var svc = Create();
        var result = svc.Classify("pH", 6.0m, null, null, null, null, Now, 300);
        Assert.Equal(TelemetryContract.ResultInvalida, result.Result);
        Assert.Equal(TelemetryContract.ReasonObservedAtFaltante, result.Reason);
        Assert.False(result.Persisted);
    }

    // ---------- Unidad ----------

    [Theory]
    [InlineData("mS/cm", "mS/cm", true)]
    [InlineData("ms/cm", "mS/cm", true)]
    [InlineData("°C", "°C", true)]
    [InlineData("C", "°C", true)]      // sin acento
    [InlineData("pH", "mS/cm", false)]
    [InlineData(null, "mS/cm", true)]  // no reportada ⇒ usa la del sensor
    [InlineData("", "mS/cm", true)]
    public void Unidad_reporte(string? reported, string sensorUnit, bool expectedMatch)
    {
        var svc = Create();
        if (expectedMatch)
        {
            var ok = svc.Classify("EC", 1.5m, Now, reported, sensorUnit, null, Now, 300);
            Assert.Equal(TelemetryContract.ResultAceptada, ok.Result);
        }
        else
        {
            var bad = svc.Classify("EC", 1.5m, Now, reported, sensorUnit, null, Now, 300);
            Assert.Equal(TelemetryContract.ResultErrorUnidad, bad.Result);
            Assert.Equal(TelemetryContract.ReasonErrorUnidad, bad.Reason);
            Assert.False(bad.Persisted);
        }
    }

    // ---------- Calidad reportada por el dispositivo ----------

    [Fact]
    public void Calidad_reportada_NO_DATA_no_persiste()
    {
        var svc = Create();
        var result = svc.Classify("pH", 6.0m, Now, null, null, TelemetryContract.QualityNoData, Now, 300);
        Assert.Equal(TelemetryContract.QualityNoData, result.Quality);
        Assert.False(result.Persisted);
    }

    [Fact]
    public void Calidad_reportada_SENSOR_ERROR_no_persiste()
    {
        var svc = Create();
        var result = svc.Classify("pH", 6.0m, Now, null, null, TelemetryContract.QualitySensorError, Now, 300);
        Assert.Equal(TelemetryContract.QualitySensorError, result.Quality);
        Assert.False(result.Persisted);
    }

    // ---------- Tipo sin catálogo ----------

    [Fact]
    public void Tipo_de_sensor_sin_catalogo_se_acepta_sin_sospecha()
    {
        var svc = Create();
        var result = svc.Classify("Luminosidad", 42.0m, Now, null, null, null, Now, 300);
        Assert.Equal(TelemetryContract.QualityValid, result.Quality);
        Assert.True(result.Persisted);
    }
}

/// <summary>Predicado compartido que consumen forecasting/anomalías/dashboard/reportes.</summary>
public class TelemetryQualityPolicyTests
{
    [Theory]
    [InlineData(TelemetryContract.QualityValid, true)]
    [InlineData(TelemetryContract.QualitySuspect, true)]
    [InlineData(TelemetryContract.QualityStale, true)]   // retransmisión real, utilizable
    [InlineData(TelemetryContract.QualityInvalid, false)]
    [InlineData(TelemetryContract.QualityFuture, false)]
    [InlineData(TelemetryContract.QualityNoData, false)]
    [InlineData(TelemetryContract.QualitySensorError, false)]
    public void Usabilidad_operativa(string quality, bool expected) =>
        Assert.Equal(expected, TelemetryQualityPolicy.IsOperationallyUsable(quality));
}