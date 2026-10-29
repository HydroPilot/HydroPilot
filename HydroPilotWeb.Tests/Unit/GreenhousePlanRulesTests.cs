using HydroPilotWeb.Services.Simulation;

namespace HydroPilotWeb.Tests.Unit;

/// <summary>Plan del invernadero: cuántos circuitos densos y de Adultas en la misma superficie (lógica pura).</summary>
public class GreenhousePlanRulesTests
{
    // Un lote por mes en el denso y una tanda por mes en cada hueco de Adultas, para que las cuentas sean redondas.
    private static GreenhouseCycle Cycle(decimal adultValue = 1000m) => new(
        DenseCycleDays: GreenhousePlanRules.DaysPerMonth,
        AdultDays: GreenhousePlanRules.DaysPerMonth,
        BabyLeafShare: 0.6m,
        RescueShare: 0.3m,
        AdultLossRate: 0.1m,
        BabyLeafValue: 600m,
        AdultValue: adultValue,
        UnplacedCost: 50m,
        SeedCost: 2m);

    private static readonly GreenhouseCircuits Circuits = new(200, 20, 0.845m, 1.0125m);

    [Fact]
    public void Las_Que_No_Llegan_A_Baby_Leaf_Van_A_Adultas_Hasta_Llenarlas()
    {
        // 200 sembradas: 120 Baby Leaf, 60 pueden ir a Adultas, entran 40 (2 circuitos × 20) y se descartan 20.
        var option = GreenhousePlanRules.Evaluate(Cycle(), Circuits, dense: 1, adult: 2);

        Assert.Equal(120m, option.BabyLeafPerMonth);
        Assert.Equal(36m, option.AdultsPerMonth);       // 40 trasplantadas − 10 % de pérdida
        Assert.Equal(20m, option.UnplacedPerMonth);
        Assert.Equal(110_600m, option.MarginPerMonth);  // 120 × 600 + 40 × 1000 − 20 × 50 − 200 × 2
        Assert.Equal(2, option.AdultCircuitsUsed);
        Assert.Equal(2.87m, option.AreaM2);
    }

    [Fact]
    public void Si_Una_Adulta_Deja_Mas_El_Lugar_Que_Sobra_Se_Llena_Con_Baby_Leafs()
    {
        // 4 circuitos (80 huecos): las 60 que no llegan a Baby Leaf y 20 que podían ser Baby Leaf.
        var option = GreenhousePlanRules.Evaluate(Cycle(), Circuits, dense: 1, adult: 4);

        Assert.Equal(100m, option.BabyLeafPerMonth);
        Assert.Equal(139_600m, option.MarginPerMonth);  // 100 × 600 + 80 × 1000 − 200 × 2
        Assert.Equal(4, option.AdultCircuitsUsed);
    }

    [Fact]
    public void Si_Una_Baby_Leaf_Deja_Mas_Sobran_Circuitos_De_Adultas()
    {
        var option = GreenhousePlanRules.Evaluate(Cycle(adultValue: 500m), Circuits, dense: 1, adult: 4);

        Assert.Equal(120m, option.BabyLeafPerMonth);
        Assert.Equal(101_600m, option.MarginPerMonth);  // 120 × 600 + 60 × 500 − 200 × 2
        Assert.Equal(3, option.AdultCircuitsUsed);      // 60 plantas: se llenan 3 de los 4
    }

    [Fact]
    public void Reparte_La_Superficie_Y_Elige_La_De_Mas_Margen()
    {
        // 3 m²: 1 denso + 2 de Adultas, 2 + 1 o 3 + 0.
        var options = GreenhousePlanRules.Options(Cycle(), Circuits, 3m);

        Assert.Equal([(1, 2), (2, 1), (3, 0)], options.Select(o => (o.DenseCircuits, o.AdultCircuits)));
        Assert.Equal(3, GreenhousePlanRules.Best(options)!.DenseCircuits); // más densos: cada uno deja más que uno de Adultas
        Assert.Equal(68_600m, GreenhousePlanRules.DenseCircuitPerMonth(Cycle(), Circuits)); // 200 × (0,6 × 600 − 0,3 × 50 − 2)
        Assert.Equal(21_000m, GreenhousePlanRules.AdultCircuitPerMonth(Cycle(), Circuits)); // 20 × (1000 + 50)
    }

    [Fact]
    public void Las_Baby_Leaf_Que_Pasan_El_Tope_Van_A_Adultas_O_Se_Descartan()
    {
        // Tope 50: de las 120 aptas se venden 50; las otras 70 y las 60 que no llegan compiten por 80 huecos.
        var option = GreenhousePlanRules.Evaluate(Cycle(), Circuits, dense: 1, adult: 4, babyLeafCap: 50m);

        Assert.Equal(50m, option.BabyLeafPerMonth);
        Assert.Equal(72m, option.AdultsPerMonth);       // 80 trasplantadas − 10 %
        Assert.Equal(50m, option.UnplacedPerMonth);
        Assert.Equal(107_100m, option.MarginPerMonth);  // 50 × 600 + 80 × 1000 − 50 × 50 − 200 × 2
    }

    [Fact]
    public void Con_Tope_De_Baby_Leaf_Conviene_Menos_Densos_Y_Mas_Adultas()
    {
        var options = GreenhousePlanRules.Options(Cycle(), Circuits, 3m, babyLeafCap: 50m);

        Assert.Equal([65_100m, 34_700m, 4_300m], options.Select(o => o.MarginPerMonth));
        Assert.Equal(1, GreenhousePlanRules.Best(options)!.DenseCircuits);
        Assert.Equal(0.4m, GreenhousePlanRules.DenseCircuitsForCap(Cycle(), Circuits, 50m)); // 50 ÷ (200 × 0,6)
    }
}
