using System.Globalization;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Services.SensorFaults;

/// <summary>Límites de un tipo de sensor: rango normal y salto máximo entre dos lecturas seguidas.</summary>
public sealed record SensorFaultLimits(string SensorType, decimal Min, decimal Max, decimal Jump);

/// <summary>Lectura anterior de un sensor (la última guardada), para comparar la que llega.</summary>
public sealed record PreviousReading(decimal Value, DateTime ObservedAtUtc, bool Usable);

/// <summary>
/// Lapso en que las lecturas de un sensor no son confiables: un error por valor (fuera de lo normal o salto), desde la
/// lectura que lo disparó hasta que alguien lo resolvió (abierto = <see cref="ToUtc"/> null).
/// </summary>
public sealed record UntrustedPeriod(int SensorId, DateTime FromUtc, DateTime? ToUtc)
{
    public bool IsOpen => ToUtc is null;

    public bool Covers(DateTime atUtc) => atUtc >= FromUtc && (ToUtc is not { } to || atUtc <= to);
}

/// <summary>
/// Reglas de errores de sensores (lógica pura). Se vigilan tres cosas:
/// <list type="bullet">
/// <item>Sin datos: más de <see cref="SilenceAfter"/> sin lecturas (lo mismo que tarda un nodo en quedar
/// desconectado). Se resuelve sola cuando vuelven las lecturas.</item>
/// <item>Fuera de lo normal: lectura fuera del rango normal del tipo (pH, CE y temperatura).</item>
/// <item>Salto: dos lecturas seguidas que difieren más que el salto máximo (pH, CE y temperatura).</item>
/// </list>
/// Los de pH y CE frenan el control automático de ese parámetro (el otro sigue); los de temperatura y humedad solo
/// avisan.
/// Los rangos y saltos son parámetros (Ajustes &gt; Parámetros); estos son los valores de fábrica.
/// </summary>
public static class SensorFaultRules
{
    public static readonly IReadOnlyList<SensorFaultLimits> Defaults =
    [
        // pH 4–8: más ancho que la zona crítica 5–7, que el control corrige con prioridad (un pH de 4,8 puede ser
        // real y hay que corregirlo ya). Las fallas típicas de sonda se ven como salto, valor absurdo o falta de datos.
        new(AnomalyContract.SensorTypePh, 4.0m, 8.0m, 0.5m),
        new(AnomalyContract.SensorTypeCe, 0.5m, 3.5m, 0.5m),
        new(AnomalyContract.SensorTypeTemperatura, 10m, 40m, 5m),
    ];

    /// <summary>Veces el intervalo esperado del nodo sin lecturas para dar el sensor por sin datos (como el nodo).</summary>
    public const int DefaultSilenceFactor = 2;

    public static bool BlocksControl(string sensorType) =>
        sensorType is AnomalyContract.SensorTypePh or AnomalyContract.SensorTypeCe;

    /// <summary>Solo los errores por valor (fuera de rango, salto) los resuelve una persona; la falta de datos se resuelve sola.</summary>
    public static bool ResolvedByUser(string kind) => kind != SensorFaultKinds.SinDatos;

    public static TimeSpan SilenceAfter(int expectedIntervalSeconds, int factor) =>
        TimeSpan.FromSeconds((double)Math.Max(1, expectedIntervalSeconds) * Math.Max(1, factor));

    public static bool IsSilent(DateTime? lastReadingUtc, DateTime nowUtc, TimeSpan silenceAfter) =>
        lastReadingUtc is not { } last || nowUtc - last > silenceAfter;

    public static bool IsOutOfRange(decimal value, SensorFaultLimits limits) =>
        value < limits.Min || value > limits.Max;

    /// <summary>
    /// Abre un error de rango salvo que la lectura anterior ya estuviera fuera y alguien haya resuelto el último
    /// error de rango después de esa racha: ya lo revisó, el valor se toma como real y no se vuelve a frenar hasta
    /// que las lecturas vuelvan a lo normal y salgan otra vez. (Con un error de rango abierto no se llama.)
    /// </summary>
    public static bool ShouldOpenRange(bool previousOutOfRange, DateTime? previousObservedUtc, DateTime? lastRangeDetectedUtc) =>
        !(previousOutOfRange
          && previousObservedUtc is { } previous
          && lastRangeDetectedUtc is { } detected
          && previous >= detected);

    /// <summary>
    /// Salto: la lectura anterior estaba en el rango normal y se movió más que el salto máximo en menos de
    /// <paramref name="maxGap"/> (con más tiempo entre lecturas un cambio grande puede ser real). No cuenta si hubo
    /// una dosis en ese lapso (la dosis mueve el pH y la CE a propósito).
    /// </summary>
    public static bool IsJump(PreviousReading previous, decimal value, DateTime observedUtc, SensorFaultLimits limits, TimeSpan maxGap, bool doseInBetween) =>
        previous.Usable
        && !IsOutOfRange(previous.Value, limits)
        && !doseInBetween
        && observedUtc > previous.ObservedAtUtc
        && observedUtc - previous.ObservedAtUtc <= maxGap
        && Math.Abs(value - previous.Value) > limits.Jump;

    /// <summary>"rango normal 5–7 °C" / "más de 0,5 entre lecturas".</summary>
    public static string RangeDetail(SensorFaultLimits limits) =>
        $"rango normal {Num(limits.Min)}–{Num(limits.Max)}{Unit(limits.SensorType)}";

    public static string JumpDetail(SensorFaultLimits limits) =>
        $"más de {Num(limits.Jump)}{Unit(limits.SensorType)} entre lecturas";

    public static string Unit(string sensorType) => sensorType switch
    {
        AnomalyContract.SensorTypeCe => " mS/cm",
        AnomalyContract.SensorTypeTemperatura => " °C",
        AnomalyContract.SensorTypeHumedad => " %",
        _ => string.Empty,
    };

    public static string Num(decimal value) =>
        Math.Round(value, 2).ToString("0.##", CultureInfo.GetCultureInfo("es-AR"));

    /// <summary>
    /// Lapsos no confiables de cualquier sensor que tocan desde <paramref name="sinceUtc"/> (los abiertos y los resueltos
    /// después). La falta de datos no cuenta: no hay lecturas que descartar.
    /// </summary>
    public static async Task<IReadOnlyList<UntrustedPeriod>> UntrustedPeriodsAsync(HydroPilotDbContext context, DateTime sinceUtc, CancellationToken ct) =>
        await context.SensorFaults.AsNoTracking()
            .Where(f => f.Kind != SensorFaultKinds.SinDatos && (f.ResolvedAtUtc == null || f.ResolvedAtUtc >= sinceUtc))
            .Select(f => new UntrustedPeriod(f.SensorId, f.DetectedAtUtc, f.ResolvedAtUtc))
            .ToListAsync(ct);

    // ------------------------------------------------------------------
    // Parámetros: AppSettings "SensorFault:{tipo}:Min|Max|Jump"
    // ------------------------------------------------------------------

    public const string SettingKeyPrefix = "SensorFault:";

    public static string MinKey(string sensorType) => $"{SettingKeyPrefix}{sensorType}:Min";
    public static string MaxKey(string sensorType) => $"{SettingKeyPrefix}{sensorType}:Max";
    public static string JumpKey(string sensorType) => $"{SettingKeyPrefix}{sensorType}:Jump";

    /// <summary>Límites vigentes por tipo de sensor: lo cargado en Parámetros o, si falta, el valor de fábrica.</summary>
    public static async Task<IReadOnlyDictionary<string, SensorFaultLimits>> LoadLimitsAsync(HydroPilotDbContext context, CancellationToken ct)
    {
        var values = await context.AppSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith(SettingKeyPrefix))
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        decimal Read(string key, decimal fallback) =>
            values.TryGetValue(key, out var raw) && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        return Defaults.ToDictionary(
            d => d.SensorType,
            d => new SensorFaultLimits(d.SensorType, Read(MinKey(d.SensorType), d.Min), Read(MaxKey(d.SensorType), d.Max), Read(JumpKey(d.SensorType), d.Jump)),
            StringComparer.OrdinalIgnoreCase);
    }
}
