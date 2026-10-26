using HydroPilotWeb.Services.Anomalies;
using HydroPilotWeb.Services.Control;
using static HydroPilotWeb.Services.Control.RecipeRules;

namespace HydroPilotWeb.Tests.Unit;

/// <summary>
/// Receta aprendida: ajuste por lotes cerrados parecidos (temporada × clima, Baby Leaf logrado; los buenos contra
/// el conjunto), evidencia,
/// confianza según el score diario del lote y corrimiento del objetivo dentro de la banda. Lógica pura.
/// </summary>
public class RecipeRulesTests
{
    private static readonly DateOnly Sowing = new(2026, 10, 1);

    private static PastLot Lot(int id, decimal? rate, decimal? ph, decimal? ec, int daysApart = 0, decimal? gdd = null) =>
        new(id, Sowing.AddYears(-1).AddDays(daysApart), gdd, rate, ph, ec);

    [Fact]
    public void Sin_historial_rigen_los_niveles_de_la_etapa()
    {
        Assert.Equal(None, FromHistory([], Sowing, null));
        // Lotes de otra temporada (más de 90 días) no cuentan.
        Assert.Equal(None, FromHistory([Lot(1, 0.9m, 0.3m, 0.1m, daysApart: 120)], Sowing, null));
        // Sin lo que se midió de pH/CE tampoco.
        Assert.Equal(None, FromHistory([Lot(1, 0.9m, null, null)], Sowing, null));
    }

    [Fact]
    public void Toma_cuanto_se_apartaron_del_resto_los_de_mas_Baby_Leaf_y_achica_por_evidencia()
    {
        // Promedio 60 %: los buenos son los de 80 % y 70 % (pH +0,2, CE +0,1); el conjunto trabajó con pH +0,075
        // y CE 0. Evidencia 4 → se aplica 4/7 de la diferencia.
        var recipe = FromHistory(
        [
            Lot(1, 0.8m, 0.2m, 0.1m),
            Lot(2, 0.7m, 0.2m, 0.1m),
            Lot(3, 0.5m, -0.1m, -0.1m),
            Lot(4, 0.4m, 0.0m, -0.1m),
        ], Sowing, null);

        Assert.Equal(0.05m, recipe.PhOffset); // (0,2 − 0,075) × 4/7 = 0,071 → 0,05
        Assert.Equal(0.05m, recipe.EcOffset); // (0,1 − 0) × 4/7 = 0,057 → 0,05
        Assert.Equal((4, 2, 4m, 0.6m), (recipe.SimilarLots, recipe.GoodLots, recipe.Evidence, recipe.MeanBabyLeafRate!.Value));
    }

    [Fact]
    public void El_corrimiento_que_tienen_todos_los_lotes_no_mueve_el_objetivo()
    {
        // El control deja el pH ~0,3 arriba del objetivo en todos los lotes: eso no dice qué funcionó.
        var recipe = FromHistory(
        [
            Lot(1, 0.8m, 0.30m, -0.2m),
            Lot(2, 0.7m, 0.30m, -0.2m),
            Lot(3, 0.5m, 0.30m, -0.2m),
            Lot(4, 0.4m, 0.30m, -0.2m),
        ], Sowing, null);

        Assert.Equal((0m, 0m), (recipe.PhOffset, recipe.EcOffset));
    }

    [Fact]
    public void Con_un_solo_lote_no_hay_con_que_comparar_y_nunca_pasa_el_maximo()
    {
        Assert.Equal(0m, FromHistory([Lot(1, 0.8m, 0.2m, null)], Sowing, null).PhOffset);

        // 5 buenos con pH/CE +1 y 5 malos con −1: la diferencia se recorta al máximo.
        var many = Enumerable.Range(1, 10)
            .Select(i => i <= 5 ? Lot(i, 0.8m, 1.0m, 1.0m) : Lot(i, 0.4m, -1.0m, -1.0m))
            .ToList();
        var recipe = FromHistory(many, Sowing, null);
        Assert.Equal((MaxPhOffset, MaxEcOffset), (recipe.PhOffset, recipe.EcOffset));
    }

    [Fact]
    public void Temporada_y_clima_parecidos_pesan_mas()
    {
        // Misma temporada: el bueno trabajó con pH +0,3 y el malo con −0,3. A 60 días (pesan la mitad), al revés.
        // Sin pesos se anularían; con pesos manda la misma temporada.
        var recipe = FromHistory(
        [
            Lot(1, 0.8m, 0.3m, null),
            Lot(2, 0.4m, -0.3m, null),
            Lot(3, 0.8m, -0.3m, null, daysApart: 60),
            Lot(4, 0.4m, 0.3m, null, daysApart: 60),
        ], Sowing, null);
        Assert.True(recipe.PhOffset > 0m);

        // Clima muy distinto (5 GDD por día de diferencia): no cuenta.
        Assert.Equal(None, FromHistory([Lot(1, 0.8m, 0.3m, null, gdd: 20m)], Sowing, 15m));
    }

    [Fact]
    public void Tendencia_del_score_de_las_mismas_plantas()
    {
        // Dos plantas que bajan 2 puntos por día durante 4 días.
        var samples = Enumerable.Range(0, 5)
            .SelectMany(i => new[]
            {
                new ScoreSample(1, Sowing.AddDays(i), 80m - 2m * i, 300m),
                new ScoreSample(2, Sowing.AddDays(i), 70m - 2m * i, 300m),
            })
            .ToList();
        var changes = DailyScoreChanges(samples, 250m, 450m);
        Assert.Equal(4, changes.Count);
        Assert.Equal(-2m, ScoreTrendPerDay(changes));
        Assert.Null(ScoreTrendPerDay(changes.Take(2).ToList()));
    }

    [Fact]
    public void Cortar_las_mejores_o_cruzar_la_ventana_no_baja_la_tendencia()
    {
        // Las plantas suben 1 punto por día. El día 2 se cortan las dos mejores (el promedio del lote cae 15 puntos)
        // y el día 3 el lote entra en la ventana (+20 a todas): ninguna de las dos cosas es "el score bajando".
        var samples = new List<ScoreSample>();
        for (var day = 0; day < 6; day++)
        {
            var gdd = day < 3 ? 240m : 260m;
            var window = gdd >= 250m ? 20m : 0m;
            for (var plant = 1; plant <= 4; plant++)
            {
                if (day >= 2 && plant <= 2)
                    continue; // cortadas
                samples.Add(new ScoreSample(plant, Sowing.AddDays(day), 40m + 10m * (4 - plant) + day + window, gdd));
            }
        }

        Assert.Equal(1m, ScoreTrendPerDay(DailyScoreChanges(samples, 250m, 450m)));
    }

    [Theory]
    [InlineData(1.0, -2.0, true, 0.5)]   // score cayendo con el ajuste puesto → la mitad
    [InlineData(0.5, -2.0, true, 0.25)]
    [InlineData(0.5, 1.0, true, 0.75)]   // score subiendo → se recupera
    [InlineData(0.5, -0.5, true, 0.5)]   // baja apenas → igual
    [InlineData(0.3, -2.0, false, 1.0)]  // sin ajuste no hay en qué desconfiar
    public void Confianza_segun_el_score_del_lote(double trust, double trend, bool adjusted, double expected) =>
        Assert.Equal((decimal)expected, NextTrust((decimal)trust, (decimal)trend, adjusted));

    [Fact]
    public void Sin_score_la_confianza_no_cambia() =>
        Assert.Equal(0.5m, NextTrust(0.5m, null, adjusted: true));

    [Fact]
    public void Ajuste_aplicado_redondeado_a_005() =>
        Assert.Equal((0.10m, 0.05m, 0m), (Apply(0.2m, 0.5m), Apply(0.1m, 0.5m), Apply(0.3m, 0m)));

    [Fact]
    public void Corre_el_objetivo_dentro_de_la_banda_de_la_etapa()
    {
        var band = new SolutionBand(5.5m, 6.5m, 6.0m);
        Assert.Equal(new SolutionBand(5.7m, 6.5m, 6.2m), Shift(band, 0.2m));
        Assert.Equal(new SolutionBand(5.75m, 6.5m, 6.25m), Shift(band, 0.4m)); // como mucho un cuarto de la banda
        Assert.Equal(new SolutionBand(5.5m, 6.4m, 5.9m), Shift(band, -0.1m));
        Assert.Equal(band, Shift(band, 0m));
        Assert.Equal(new SolutionBand(null, null, null), Shift(new SolutionBand(null, null, null), 0.2m));
    }
}
