namespace HydroPilotWeb.Services.SimulationEnvironment.Model;

/// <summary>
/// Tanque de solución nutritiva (lógica pura, compartida con la Pi virtual). Guarda litros, sales (CE × litros) y pH:
/// - las plantas sacan agua y sales (<see cref="LettuceUptake"/>): el nivel baja y la CE sube o baja según el clima;
/// - cada dosis se mezcla de a poco, como con recirculación (63 % a los 40 s, 95 % a los 2 min);
/// - el agua de red entra y lo que pasa la capacidad rebalsa.
/// La química "real" del tanque es distinta a propósito de la de fábrica que supone la web (pH Up/Down 2,5 y
/// nutrientes 0,3 por ml/L): la web lo aprende con la calibración de cada bomba.
/// </summary>
public sealed class NutrientTank
{
    /// <summary>pH que baja 1 ml de pH Down por litro (la web supone 2,5).</summary>
    public const double PhDownPerMlPerLiter = 2.2;

    /// <summary>pH que sube 1 ml de pH Up por litro (la web supone 2,5).</summary>
    public const double PhUpPerMlPerLiter = 2.0;

    /// <summary>CE que sube 1 ml de A+B por litro (tipo 1:500 ≈ 0,25; la web supone 0,3).</summary>
    public const double EcPerNutrientMlPerLiter = 0.26;

    /// <summary>Los nutrientes son algo ácidos: pH que baja 1 ml/L.</summary>
    public const double PhPerNutrientMlPerLiter = 0.08;

    public const double SourceWaterEc = 0.2;
    public const double SourceWaterPh = 7.3;

    /// <summary>El agua de red trae alcalinidad: corre el pH hacia el suyo más de lo que diría la proporción.</summary>
    public const double SourceWaterPhPull = 0.7;

    public const double MixTauSeconds = 40;

    private readonly List<Mix> _mixing = [];

    private sealed class Mix
    {
        public double Salts;
        public double Ph;
    }

    public NutrientTank(double capacityLiters, double ph, double ec, double? liters = null)
    {
        CapacityLiters = Math.Max(1, capacityLiters);
        Liters = Math.Clamp(liters ?? CapacityLiters, 1, CapacityLiters);
        Ph = ph;
        Salts = Math.Max(0, ec) * Liters;
    }

    public double CapacityLiters { get; }
    public double Liters { get; private set; }

    /// <summary>Sales disueltas, como CE × litros.</summary>
    public double Salts { get; private set; }

    public double Ph { get; private set; }

    public double Ec => Liters > 0 ? Salts / Liters : 0;

    /// <summary>Nivel del tanque (0–1).</summary>
    public double Level => Liters / CapacityLiters;

    /// <summary>Hay dosis que todavía se están mezclando.</summary>
    public bool IsMixing => _mixing.Count > 0;

    /// <summary>Avanza el tiempo: la parte de cada dosis que ya se mezcló y lo que absorbieron las plantas.</summary>
    public void Step(double seconds, Uptake uptake)
    {
        if (seconds <= 0)
            return;

        var fraction = 1 - Math.Exp(-seconds / MixTauSeconds);
        foreach (var mix in _mixing)
        {
            var salts = mix.Salts * fraction;
            var ph = mix.Ph * fraction;
            Salts += salts;
            Ph += ph;
            mix.Salts -= salts;
            mix.Ph -= ph;
        }
        _mixing.RemoveAll(m => Math.Abs(m.Salts) < 0.001 && Math.Abs(m.Ph) < 0.0005);

        var water = Math.Min(uptake.WaterLiters, Math.Max(0, Liters - 20));
        Liters -= water;
        Salts = Math.Max(0, Salts - Math.Min(uptake.Salts, Salts));
        Ph += uptake.PhRise;
        Clamp();
    }

    /// <summary>Dosis de una bomba de la web (ml de producto o litros de agua); se mezcla de a poco.</summary>
    public void Dose(string kind, double amount)
    {
        if (amount <= 0)
            return;

        switch (kind)
        {
            case "ph-down":
                _mixing.Add(new Mix { Ph = -PhDownPerMlPerLiter * amount / Liters });
                break;
            case "ph-up":
                _mixing.Add(new Mix { Ph = PhUpPerMlPerLiter * amount / Liters });
                break;
            case "nutrient":   // Solución A
            case "nutrient-b": // Solución B
                _mixing.Add(new Mix
                {
                    Salts = EcPerNutrientMlPerLiter * amount,
                    Ph = -PhPerNutrientMlPerLiter * amount / Liters,
                });
                break;
            case "water":
                AddWater(amount, mixed: false);
                break;
        }
    }

    /// <summary>Reposición de nivel con agua de red (flotante u operario): entra enseguida.</summary>
    public double Refill(double? liters = null)
    {
        var added = liters ?? CapacityLiters - Liters;
        if (added <= 0)
            return 0;
        AddWater(added, mixed: true);
        return added;
    }

    /// <summary>Cambio brusco (derrame, sobredosis a mano).</summary>
    public void Nudge(double ph, double ec)
    {
        Ph += ph;
        Salts = Math.Max(0, Salts + ec * Liters);
        Clamp();
    }

    /// <summary>
    /// Agua de red: suma su CE y corre el pH hacia el del agua. Lo que pasa la capacidad rebalsa con la
    /// concentración del tanque. Con <paramref name="mixed"/> el efecto es inmediato; si no, se mezcla de a poco.
    /// </summary>
    private void AddWater(double liters, bool mixed)
    {
        var total = Liters + liters;
        var phShift = (SourceWaterPh - Ph) * liters / total * SourceWaterPhPull;
        var ecBefore = Ec;
        var mixedEc = (Salts + SourceWaterEc * liters) / total;
        var finalLiters = Math.Min(total, CapacityLiters);

        Liters = finalLiters;
        if (mixed)
        {
            Salts = mixedEc * finalLiters;
            Ph += phShift;
        }
        else
        {
            // De entrada la sonda no ve el cambio: la dilución aparece a medida que se mezcla.
            Salts = ecBefore * finalLiters;
            _mixing.Add(new Mix { Salts = (mixedEc - ecBefore) * finalLiters, Ph = phShift });
        }
        Clamp();
    }

    private void Clamp()
    {
        Ph = Math.Clamp(Ph, 3, 9);
        Salts = Math.Clamp(Salts, 0, 6 * Liters);
    }
}
