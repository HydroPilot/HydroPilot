using HydroPilotWeb.Models;
using Microsoft.Extensions.Options;

namespace HydroPilotWeb.Services;

/// <summary>
/// Reglas puras de validación del contrato de telemetría (sin acceso a base de datos).
/// Separa el estado físico (rangos plausibles) del estado operativo (bandas de
/// cultivo), y normaliza timestamps a UTC. Un futuro adaptador MQTT reutiliza
/// exactamente estas reglas.
/// </summary>
public sealed class TelemetryValidationService
{
    private readonly TelemetryOptions _options;

    public TelemetryValidationService(IOptions<TelemetryOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>
    /// Resultado de la validación de una lectura: qué calidad se asigna, con qué
    /// resultado por lectura, y si corresponde persistir una SensorReading
    /// (false ⇒ registro en TelemetryRejection como cuarentena de diagnóstico).
    /// </summary>
    public sealed record Classification(string Result, string Quality, string? Reason, bool Persisted)
    {
        public static Classification Rejected(string result, string quality, string reason) =>
            new(result, quality, reason, Persisted: false);
    }

    /// <summary>Catálogo físico: rango posible y banda operativa por tipo de sensor.</summary>
    public sealed record SensorBand(string SensorType, decimal PhysicalMin, decimal PhysicalMax, decimal OperationalMin, decimal OperationalMax);

    // Mismos rangos que el baseline (TelemetryController.ValidateReading):
    // los valores físicos delimitan lo posible; la banda operativa marca sospecha.
    public static readonly IReadOnlyList<SensorBand> Bands =
    [
        new("pH", 0m, 14m, 5.0m, 7.0m),
        new("CE", 0m, 10m, 0.5m, 3.5m),
        new("Temperatura", -20m, 80m, 10m, 40m),
        new("Humedad", 0m, 100m, 20m, 95m),
    ];

    public static SensorBand? FindBand(string? sensorTypeName) =>
        Bands.FirstOrDefault(b => string.Equals(b.SensorType, sensorTypeName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Valida que la unidad reportada coincida con la unidad configurada del sensor.
    /// null (no reportada) se acepta y usa la unidad del sensor. Comparación
    /// insensible a mayúsculas; se ignoran espacios y acentos.
    /// </summary>
    public static bool UnitMatches(string? reportedUnit, string? sensorUnitSymbol) =>
        string.IsNullOrWhiteSpace(reportedUnit) ||
        string.Equals(
            NormalizeUnit(reportedUnit),
            NormalizeUnit(sensorUnitSymbol ?? string.Empty),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeUnit(string unit)
    {
        // Solo letras y dígitos: elimina símbolos (°, /, espacios) y acentos,
        // de modo que "°C"/"C", "mS/cm"/"ms/cm" y variantes colisionen igual.
        var decomposed = unit.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var chars = decomposed
            .Where(c => char.IsLetterOrDigit(c))
            .ToArray();
        return new string(chars);
    }

    /// <summary>
    /// Clasifica la lectura según el contrato. Precondiciones: <paramref name="sensorTypeName"/>
    /// puede ser null (sensor desconocido); value/observedAt pueden ser null (faltantes).
    /// </summary>
    public Classification Classify(
        string? sensorTypeName,
        decimal? value,
        DateTime? observedAtUtc,
        string? reportedUnit,
        string? sensorUnitSymbol,
        string? deviceReportedQuality,
        DateTime nowUtc,
        int expectedIntervalSeconds)
    {
        // Estados reportados por el dispositivo que no representan una medición útil.
        if (string.Equals(deviceReportedQuality, TelemetryContract.QualityNoData, StringComparison.OrdinalIgnoreCase))
            return Classification.Rejected(TelemetryContract.ResultInvalida, TelemetryContract.QualityNoData, TelemetryContract.ReasonNoDataReportado);
        if (string.Equals(deviceReportedQuality, TelemetryContract.QualitySensorError, StringComparison.OrdinalIgnoreCase))
            return Classification.Rejected(TelemetryContract.ResultInvalida, TelemetryContract.QualitySensorError, TelemetryContract.ReasonSensorErrorReportado);

        // ------- Validez estructural (no persiste nada confiable) -------
        if (value is null)
            return Classification.Rejected(TelemetryContract.ResultInvalida, TelemetryContract.QualityInvalid, TelemetryContract.ReasonValorFaltante);

        if (observedAtUtc is null)
            return Classification.Rejected(TelemetryContract.ResultInvalida, TelemetryContract.QualityInvalid, TelemetryContract.ReasonObservedAtFaltante);

        // ------- Unidad (no persiste: el valor con unidad equivocada no es interpretable) -------
        if (!UnitMatches(reportedUnit, sensorUnitSymbol))
            return Classification.Rejected(TelemetryContract.ResultErrorUnidad, TelemetryContract.QualityInvalid, TelemetryContract.ReasonErrorUnidad);

        // ------- Timestamp -------
        var futureTolerance = TimeSpan.FromMinutes(_options.FutureToleranceMinutes);
        if (observedAtUtc.Value > nowUtc.Add(futureTolerance))
            return Classification.Rejected(TelemetryContract.ResultInvalida, TelemetryContract.QualityFuture, TelemetryContract.ReasonTimestampFuturo);

        var staleAge = TimeSpan.FromHours(Math.Max(_options.StaleMinAgeHours, expectedIntervalSeconds * _options.StaleIntervalFactor / 3600.0));
        var isStale = observedAtUtc.Value < nowUtc.Subtract(staleAge);

        // ------- Rango físico y banda operativa -------
        var band = FindBand(sensorTypeName);
        if (band is not null)
        {
            if (value.Value < band.PhysicalMin || value.Value > band.PhysicalMax)
                return new Classification(TelemetryContract.ResultFueraDeRango, TelemetryContract.QualityInvalid, TelemetryContract.ReasonFueraDeRango, Persisted: true);

            if (isStale)
                return new Classification(TelemetryContract.ResultAceptada, TelemetryContract.QualityStale, TelemetryContract.ReasonTimestampAnterior, Persisted: true);

            if (value.Value < band.OperationalMin || value.Value > band.OperationalMax)
                return new Classification(TelemetryContract.ResultAceptada, TelemetryContract.QualitySuspect, TelemetryContract.ReasonFueraDeBanda, Persisted: true);

            return new Classification(TelemetryContract.ResultAceptada, TelemetryContract.QualityValid, null, Persisted: true);
        }

        // Tipo de sensor sin catálogo: se acepta sin sospecha (no hay valor aprobado que aplicar).
        if (isStale)
            return new Classification(TelemetryContract.ResultAceptada, TelemetryContract.QualityStale, TelemetryContract.ReasonTimestampAnterior, Persisted: true);

        return new Classification(TelemetryContract.ResultAceptada, TelemetryContract.QualityValid, null, Persisted: true);
    }
}