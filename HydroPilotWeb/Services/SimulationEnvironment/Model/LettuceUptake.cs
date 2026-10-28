namespace HydroPilotWeb.Services.SimulationEnvironment.Model;

/// <summary>Lo que las plantas sacan del tanque en un lapso: agua (L), sales (CE × L) y cuánto sube el pH.</summary>
public readonly record struct Uptake(double WaterLiters, double Salts, double PhRise)
{
    public static readonly Uptake None = new(0, 0, 0);
}

/// <summary>
/// Cuánto toman las plantas de lechuga del tanque (lógica pura, compartida con la Pi virtual):
/// - Agua por planta y día según la etapa: los mismos litros que Simulación (NutrientConsumptionRules: 0,04 L antes
///   de la ventana Baby Leaf, 0,075 L en la ventana y 0,25 L la adulta). Desde 2026-10-26 cada hueco es una planta (a
///   6,5 cm; 30 g en Baby Leaf, 280 g de adulta): 200 Baby Leafs toman lo mismo que las 60 celdas de antes (~6 kg).
/// - El calor y el aire seco suben la transpiración; casi toda pasa de día.
/// - Sales: lo que absorben con cada litro ("CE de absorción"). Con frío toman más sales que agua y la CE del
///   tanque baja; con calor toman más agua y la CE sube (Cornell CEA: la CE sube con la evapotranspiración).
/// - pH: absorber nitrato sube el pH (~0,1–0,3 por día con plantas chicas, más con plantas grandes).
/// </summary>
public static class LettuceUptake
{
    public const double WaterBeforeBabyLeaf = 0.015;
    public const double WaterDuringBabyLeaf = 0.055;
    public const double WaterConventional = 0.2;
    public const double BabyLeafOpensGdd = 250;
    public const double BabyLeafClosesGdd = 450;

    /// <summary>pH que sube por cada CE × litro de sales absorbidas, por litro de tanque.</summary>
    public const double PhPerSaltPerLiter = 3.2;

    /// <summary>Agua por planta y día (L) según el GDD del lote.</summary>
    public static double DailyWaterPerPlant(double gdd) =>
        gdd < BabyLeafOpensGdd ? WaterBeforeBabyLeaf
        : gdd < BabyLeafClosesGdd ? WaterDuringBabyLeaf
        : WaterConventional;

    /// <summary>Transpiración relativa al día normal (20 °C, 65 %): más calor y aire más seco, más agua.</summary>
    public static double ClimateFactor(double temperature, double humidity) =>
        Math.Clamp(1 + 0.045 * (temperature - 20) + 0.008 * (65 - humidity), 0.35, 2.2);

    /// <summary>Parte del agua del día que se transpira en esta hora local: poco de noche, el pico al mediodía.</summary>
    public static double HourShare(double hour)
    {
        // 20 % parejo en las 24 h + 80 % con forma de seno entre las 7 y las 19 (integra 1).
        const double night = 0.2 / 24;
        if (hour < 7 || hour > 19)
            return night;
        return night + 0.8 * Math.PI / (2 * 12) * Math.Sin(Math.PI * (hour - 7) / 12);
    }

    /// <summary>Concentración de lo que absorben respecto de la CE del tanque: &gt; 1 con frío (la CE baja), &lt; 1 con calor (sube).</summary>
    public static double UptakeRatio(double temperature) =>
        Math.Clamp(1.25 - 0.06 * (temperature - 18), 0.45, 1.35);

    /// <summary>
    /// Absorción de <paramref name="plants"/> plantas en <paramref name="seconds"/> segundos a esta hora y clima.
    /// <paramref name="rootHealth"/> (0–1) baja cuando las raíces están enfermas (Pythium: toman menos).
    /// </summary>
    public static Uptake For(
        int plants,
        double gdd,
        double seconds,
        double hour,
        double temperature,
        double humidity,
        double solutionEc,
        double tankLiters,
        double rootHealth = 1)
    {
        if (plants <= 0 || seconds <= 0 || tankLiters <= 0)
            return Uptake.None;

        var water = plants * DailyWaterPerPlant(gdd) * ClimateFactor(temperature, humidity)
                    * HourShare(hour) * (seconds / 3600) * Math.Clamp(rootHealth, 0, 1);
        var salts = water * solutionEc * UptakeRatio(temperature);
        return new Uptake(water, salts, PhPerSaltPerLiter * salts / tankLiters);
    }
}
