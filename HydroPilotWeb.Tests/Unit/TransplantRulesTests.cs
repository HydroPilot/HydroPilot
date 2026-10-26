using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.Simulation;

namespace HydroPilotWeb.Tests.Unit;

/// <summary>Trasplante a Adultas, óptimo Baby Leaf por la curva de aptas y lugar ocupado por las Adultas (lógica pura).</summary>
public class TransplantRulesTests
{
    private static List<TransplantCandidate> Plants(params decimal?[] scores) =>
        scores.Select((s, i) => new TransplantCandidate(i + 1, s)).ToList();

    [Fact]
    public void Pasan_Las_De_Score_Bajo_Y_El_Excedente_Sobre_La_Meta_Si_Entran()
    {
        // 10 plantas: 2 con score bajo (40, 50), 8 buenas. Meta 60 % = 6 Baby Leaf → 2 buenas de más (las de menor score).
        var candidates = Plants(40, 50, 95, 90, 85, 80, 75, 70, 65, 62);

        var proposal = TransplantRules.Propose(candidates, 10, 60m, capacity: 10, scoreMinCandidate: 60m);

        Assert.Equal([1, 2, 10, 9], proposal.PlantIds);
        Assert.Equal(2, proposal.LowScore);
        Assert.Equal(2, proposal.OverGoal);
        Assert.Equal(0, proposal.NoRoom);
    }

    [Fact]
    public void Las_Que_No_Entran_Quedan_En_El_Denso()
    {
        var candidates = Plants(40, 50, 95, 90, 85, 80, 75, 70, 65, 62);

        var proposal = TransplantRules.Propose(candidates, 10, 60m, capacity: 3, scoreMinCandidate: 60m);

        Assert.Equal([1, 2, 10], proposal.PlantIds); // primero las de score bajo
        Assert.Equal(1, proposal.NoRoom);
    }

    [Fact]
    public void Sin_Meta_Solo_Pasan_Las_De_Score_Bajo_Y_Sin_Score_Se_Quedan()
    {
        var proposal = TransplantRules.Propose(Plants(null, 30, 90), 3, null, capacity: 10, scoreMinCandidate: 60m);

        Assert.Equal([2], proposal.PlantIds);
        Assert.True(TransplantRules.IsDue(250m, 250m, 450m));
        Assert.False(TransplantRules.IsDue(450m, 250m, 450m));
        Assert.Equal(new[] { (1, 2), (2, 1), (2, 2) }, TransplantRules.FreeHoles(2, 2, new HashSet<(int, int)> { (1, 1) }));
        Assert.Equal(11.88m, TransplantParameters.Defaults.CostPerPlant); // $9.500 ÷ 800 plantas por hora
    }

    [Fact]
    public void El_Optimo_Es_El_Pico_De_La_Curva_De_Aptas()
    {
        var d = new DateOnly(2026, 10, 1);
        AptCurvePoint P(int day, decimal gdd, int apt) => new(d.AddDays(day), gdd, apt, 100);

        // Suben y bajan: el pico fue el día 2 (340 GDD) y ya pasó → cortar ahora.
        var passed = BabyLeafOptimumRules.Resolve([P(0, 300, 20), P(1, 320, 50), P(2, 340, 70), P(3, 360, 60)], 330m, 360m, 250m, 450m);
        Assert.Equal(new BabyLeafOptimum(340m, OptimumSource.Lote, true, 70), passed);

        // Siguen subiendo: no es hoy; el histórico ya pasó → mañana.
        var rising = BabyLeafOptimumRules.Resolve([P(0, 300, 20), P(1, 320, 50)], 310m, 320m, 250m, 450m);
        Assert.False(rising.PeakReached);
        Assert.Equal(321m, rising.Gdd);

        // Un par de aptas al abrir la ventana no es el pico (menos del 10 % de las evaluadas).
        Assert.False(BabyLeafOptimumRules.PeakReached([P(0, 260, 3), P(1, 275, 2)]));

        // Antes de la ventana: el histórico o, sin historial, 300.
        Assert.Equal(new BabyLeafOptimum(330m, OptimumSource.Historico, false, null), BabyLeafOptimumRules.Resolve([], 330m, 100m, 250m, 450m));
        Assert.Equal(BabyLeafOptimum.Factory, BabyLeafOptimumRules.Resolve([], null, 100m, 250m, 450m));
    }

    [Fact]
    public void Pico_Historico_Pesa_Los_Lotes_De_La_Misma_Temporada()
    {
        var sowing = new DateOnly(2026, 10, 1);
        var lots = new List<PeakHistoryLot>
        {
            new(new DateOnly(2025, 10, 5), 320m, null), // misma temporada: pesa 1
            new(new DateOnly(2025, 4, 1), 380m, null),  // otoño: no pesa
            new(new DateOnly(2025, 9, 20), 340m, null), // pesa 1
        };

        Assert.Equal(330m, BabyLeafOptimumRules.HistoricalPeak(lots, sowing, null));
        Assert.Null(BabyLeafOptimumRules.HistoricalPeak([], sowing, null));
    }

    [Fact]
    public void Una_Adulta_Ocupa_El_Lugar_De_Unas_Diez_Baby_Leafs()
    {
        // 6,5 y 22,5 cm; Baby Leaf 30 días contra Adulta 12 + 25: 1.268 contra 13.163 cm²·día.
        var equivalence = SpaceCostRules.Equivalence(6.5m, 22.5m, babyLeafDays: 30, daysToTransplant: 12, adultDays: 25);

        Assert.Equal(10.39m, equivalence);
        Assert.Equal(4 * 9.39m * 400m, SpaceCostRules.Total(4, 10.39m, 400m)); // 4 adultas × 9,39 × $400
        Assert.Equal(0m, SpaceCostRules.Total(4, 10.39m, -50m));              // un margen negativo no se cobra
        Assert.Null(SpaceCostRules.Equivalence(6.5m, 22.5m, 0, 12, 25));
    }
}
