namespace HydroPilotWeb.Services.Simulation;

/// <summary>
/// Cómo rinde un ciclo del invernadero (del histórico de lotes cerrados y los precios de Parámetros), por planta:
/// <list type="bullet">
/// <item>DenseCycleDays: días que un lote ocupa el circuito denso (de la siembra a que sale la última planta) más la
/// limpieza; AdultDays: días que una trasplantada ocupa su hueco de Adultas (del trasplante al corte).</item>
/// <item>BabyLeafShare: de las sembradas, las que se cortan como Baby Leaf; RescueShare: las que siguen vivas sin llegar
/// a Baby Leaf (pueden ir a Adultas; si no hay lugar, se descartan al cerrar la ventana); AdultLossRate: trasplantadas
/// que se pierden antes del corte.</item>
/// <item>BabyLeafValue: por Baby Leaf, venta − nutrientes hasta el corte; AdultValue: por trasplantada, venta × (1 −
/// pérdida) − nutrientes de toda su vida − mano de obra del trasplante; UnplacedCost: nutrientes de una que no entra
/// en Adultas hasta que cierra la ventana; SeedCost: por planta sembrada.</item>
/// </list>
/// </summary>
public sealed record GreenhouseCycle(
    decimal DenseCycleDays,
    decimal AdultDays,
    decimal BabyLeafShare,
    decimal RescueShare,
    decimal AdultLossRate,
    decimal BabyLeafValue,
    decimal AdultValue,
    decimal UnplacedCost,
    decimal SeedCost);

/// <summary>Un circuito de cada tipo: sus huecos y su superficie (huecos × separación²).</summary>
public sealed record GreenhouseCircuits(int DenseHoles, int AdultHoles, decimal DenseM2, decimal AdultM2);

/// <summary>
/// Una distribución del invernadero por mes: circuitos densos y de Adultas (los que entran en la superficie y los que se
/// llenan), plantas de cada destino, las que se descartan por no tener lugar y el margen (ventas − gastos del sistema).
/// </summary>
public sealed record GreenhouseOption(
    int DenseCircuits,
    int AdultCircuits,
    int AdultCircuitsUsed,
    decimal AreaM2,
    decimal BabyLeafPerMonth,
    decimal AdultsPerMonth,
    decimal UnplacedPerMonth,
    decimal MarginPerMonth);

/// <summary>
/// Plan del invernadero (lógica pura): cuántos circuitos densos y cuántos de Adultas conviene tener en una superficie.
/// En régimen, por mes:
/// <list type="bullet">
/// <item>sembradas = densos × huecos × 30,4 ÷ días de ciclo del denso: las Baby Leaf tienen ciclos cortos y huecos
/// chicos, el denso se vuelve a sembrar apenas sale la última planta;</item>
/// <item>lugar en Adultas = circuitos × huecos × 30,4 ÷ días de la adulta en su hueco;</item>
/// <item>Baby Leaf vendidas = las aptas hasta el tope de venta (las adultas no tienen tope); las que pasan el tope
/// cuentan como las que no llegan a Baby Leaf;</item>
/// <item>van a Adultas las que no llegan a Baby Leaf, hasta llenar ese lugar; si una adulta deja más que una Baby Leaf,
/// el lugar que sobra se llena con plantas que podían ser Baby Leaf; las que no entran se descartan;</item>
/// <item>margen = Baby Leaf × su valor + trasplantadas × su valor − sin lugar × sus nutrientes − sembradas × semilla.</item>
/// </list>
/// </summary>
public static class GreenhousePlanRules
{
    public const decimal DaysPerMonth = 365m / 12m;

    /// <param name="babyLeafCap">Baby Leaf que se pueden vender por mes (plantas); null = sin tope.</param>
    public static GreenhouseOption Evaluate(GreenhouseCycle c, GreenhouseCircuits k, int dense, int adult, decimal? babyLeafCap = null)
    {
        var seeded = c.DenseCycleDays > 0 ? dense * k.DenseHoles * DaysPerMonth / c.DenseCycleDays : 0m;
        var apt = seeded * c.BabyLeafShare;
        var sellable = babyLeafCap is { } cap ? Math.Min(apt, Math.Max(0m, cap)) : apt;
        var candidates = seeded * c.RescueShare + apt - sellable; // no llegan a Baby Leaf o pasan el tope
        var perCircuit = c.AdultDays > 0 ? k.AdultHoles * DaysPerMonth / c.AdultDays : 0m;
        var capacity = adult * perCircuit;

        var rescued = Math.Min(candidates, capacity);
        var extra = c.AdultValue > c.BabyLeafValue ? Math.Min(capacity - rescued, sellable) : 0m;
        var transplanted = rescued + extra;
        var babyLeaf = sellable - extra;
        var unplaced = candidates - rescued;

        var margin = babyLeaf * c.BabyLeafValue + transplanted * c.AdultValue - unplaced * c.UnplacedCost - seeded * c.SeedCost;
        var used = perCircuit > 0 ? (int)Math.Ceiling(transplanted / perCircuit - 0.0001m) : 0;
        return new GreenhouseOption(
            dense,
            adult,
            Math.Min(adult, Math.Max(0, used)),
            Math.Round(dense * k.DenseM2 + adult * k.AdultM2, 2),
            Math.Round(babyLeaf, 0),
            Math.Round(transplanted * (1m - c.AdultLossRate), 0),
            Math.Round(unplaced, 0),
            Math.Round(margin, 0));
    }

    /// <summary>Cada cantidad de circuitos densos que entra en la superficie, con el resto en circuitos de Adultas.</summary>
    public static IReadOnlyList<GreenhouseOption> Options(
        GreenhouseCycle c, GreenhouseCircuits k, decimal areaM2, decimal? babyLeafCap = null)
    {
        var options = new List<GreenhouseOption>();
        if (k.DenseM2 <= 0 || k.AdultM2 <= 0)
            return options;
        for (var dense = 1; dense * k.DenseM2 <= areaM2 + 0.0001m; dense++)
        {
            var adult = (int)Math.Floor((areaM2 - dense * k.DenseM2) / k.AdultM2 + 0.0001m);
            options.Add(Evaluate(c, k, dense, adult, babyLeafCap));
        }
        return options;
    }

    /// <summary>La de más margen; a igual margen, la de menos circuitos.</summary>
    public static GreenhouseOption? Best(IEnumerable<GreenhouseOption> options) =>
        options.OrderByDescending(o => o.MarginPerMonth).ThenBy(o => o.DenseCircuits + o.AdultCircuitsUsed).FirstOrDefault();

    /// <summary>Lo que deja por mes un circuito denso solo (las que no son Baby Leaf se descartan).</summary>
    public static decimal DenseCircuitPerMonth(GreenhouseCycle c, GreenhouseCircuits k) =>
        c.DenseCycleDays > 0
            ? Math.Round(k.DenseHoles * DaysPerMonth / c.DenseCycleDays
                         * (c.BabyLeafShare * c.BabyLeafValue - c.RescueShare * c.UnplacedCost - c.SeedCost), 0)
            : 0m;

    /// <summary>Cuántos circuitos densos cubren el tope de Baby Leaf: más densos solo sirven para mandar plantas a Adultas.</summary>
    public static decimal DenseCircuitsForCap(GreenhouseCycle c, GreenhouseCircuits k, decimal babyLeafCap)
    {
        var perCircuit = c.DenseCycleDays > 0 ? k.DenseHoles * DaysPerMonth / c.DenseCycleDays * c.BabyLeafShare : 0m;
        return perCircuit > 0 ? Math.Round(babyLeafCap / perCircuit, 1) : 0m;
    }

    /// <summary>Lo que suma por mes un circuito de Adultas lleno con plantas que, si no, se descartan.</summary>
    public static decimal AdultCircuitPerMonth(GreenhouseCycle c, GreenhouseCircuits k) =>
        c.AdultDays > 0 ? Math.Round(k.AdultHoles * DaysPerMonth / c.AdultDays * (c.AdultValue + c.UnplacedCost), 0) : 0m;
}
