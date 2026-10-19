using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models.Optimization;
using HydroPilotWeb.Services.Anomalies;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.Optimization;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Integración de Optimización sobre SQL Server local: recomendaciones R1–R6 con la lógica real de Lotes,
/// Simulación y Anomalías; una recomendación por problema (no se duplica ni se vuelve a avisar), aceptar
/// verifica solo si se cumplió, descartar silencia el problema, y sin datos frescos no se cierra nada.
/// </summary>
[Collection("optimization-sql")]
public class OptimizationSqlServerTests
{
    private readonly OptimizationSqlFixture _fixture;

    public OptimizationSqlServerTests(OptimizationSqlFixture fixture) => _fixture = fixture;

    private HydroPilotDbContext Ctx() => _fixture.NewContext();

    // Sembrado hace 16 días: 17 × 20,5 = 348,5 GDD (ventana Baby Leaf, pasado el óptimo de 300).
    private const int BabyLeafWindowDaysAgo = 16;

    private async Task<int> LotAsync(int daysAgo, int apta = 0, int candidate = 0, int developing = 0, int risk = 0,
        decimal? goal = null, decimal? maxLoss = null)
    {
        await using var context = Ctx();
        return await OptimizationSqlFixture.SeedLotAsync(context, daysAgo, apta, candidate, developing, risk, goal, maxLoss);
    }

    private async Task<OptimizationViewDto> ViewAsync(int lotId) =>
        (await _fixture.Optimization.GetViewAsync(lotId))!;

    private static RecommendationDto Active(OptimizationViewDto view, string type) =>
        Assert.Single(view.Active, r => r.Type == type);

    [Fact]
    public async Task Corte_Baby_Leaf_Ahora_Crea_Una_Recomendacion_Y_Avisa_Una_Sola_Vez()
    {
        var lotId = await LotAsync(BabyLeafWindowDaysAgo, apta: 3, developing: 3);

        var first = await ViewAsync(lotId);
        var cut = Active(first, OptimizationContractType.BabyLeafCut);
        Assert.Equal("Cortar Baby Leaf ahora", cut.Title);
        Assert.Equal(OptimizationContractStatus.Pending, cut.Status);
        Assert.Equal(OptimizationContractPriority.High, cut.Priority);
        Assert.Equal(4_500m, cut.Impact); // 3 aptas × 30 g × $50.000/kg
        Assert.True(first.Kpis.ToCapture >= 4_500m);

        // Repetir no duplica ni vuelve a avisar: es el mismo problema.
        var second = await ViewAsync(lotId);
        Assert.Equal(cut.Id, Active(second, OptimizationContractType.BabyLeafCut).Id);
        Assert.Single(_fixture.Sink.Events, e => e.RecommendationId == cut.Id);

        await using var context = Ctx();
        Assert.Equal(1, await context.OptimizationRecommendations.CountAsync(
            r => r.LotId == lotId && r.RecommendationType == OptimizationContractType.BabyLeafCut));
    }

    [Fact]
    public async Task Aceptar_Y_Cosechar_Las_Aptas_La_Cierra_Cumplida_Con_Lo_Logrado()
    {
        var lotId = await LotAsync(BabyLeafWindowDaysAgo, apta: 3, developing: 3);
        var cut = Active(await ViewAsync(lotId), OptimizationContractType.BabyLeafCut);

        var accepted = await _fixture.Optimization.AcceptAsync(cut.Id, "test");
        Assert.True(accepted.Succeeded);
        Assert.True((await _fixture.Optimization.AcceptAsync(cut.Id, "test")).Repeated);

        await using (var context = Ctx())
            await OptimizationSqlFixture.HarvestAsync(context, lotId, CommercialStageNames.BabyLeafApta);

        var view = await ViewAsync(lotId);
        var done = Assert.Single(view.History, r => r.Id == cut.Id);
        Assert.Equal(OptimizationContractStatus.Done, done.Status);
        Assert.Equal(4_500m, done.RealizedAmount);
        Assert.Equal(4_500m, view.Kpis.Achieved);
        Assert.Equal(1, view.Kpis.Done);
    }

    [Fact]
    public async Task Pendiente_Que_Deja_De_Aplicar_Queda_En_El_Historial()
    {
        var lotId = await LotAsync(BabyLeafWindowDaysAgo, apta: 2, developing: 2);
        var cut = Active(await ViewAsync(lotId), OptimizationContractType.BabyLeafCut);

        await using (var context = Ctx())
            await OptimizationSqlFixture.HarvestAsync(context, lotId, CommercialStageNames.BabyLeafApta);

        var expired = Assert.Single((await ViewAsync(lotId)).History, r => r.Id == cut.Id);
        Assert.Equal(OptimizationContractStatus.Expired, expired.Status);
        Assert.NotNull(expired.ClosedAtUtc);
    }

    [Fact]
    public async Task Descartar_Pide_Motivo_Y_Silencia_El_Problema_Mientras_Siga()
    {
        var lotId = await LotAsync(BabyLeafWindowDaysAgo, apta: 3, developing: 3);
        var cut = Active(await ViewAsync(lotId), OptimizationContractType.BabyLeafCut);

        Assert.False((await _fixture.Optimization.DiscardAsync(cut.Id, " ", "test")).Succeeded);
        Assert.True((await _fixture.Optimization.DiscardAsync(cut.Id, "Sin mano de obra", "test")).Succeeded);

        var view = await ViewAsync(lotId);
        Assert.DoesNotContain(view.Active, r => r.Type == OptimizationContractType.BabyLeafCut);
        Assert.Equal("Sin mano de obra", Assert.Single(view.History, r => r.Id == cut.Id).DecisionNote);

        await using var context = Ctx();
        Assert.Equal(1, await context.OptimizationRecommendations.CountAsync(
            r => r.LotId == lotId && r.RecommendationType == OptimizationContractType.BabyLeafCut));
    }

    [Fact]
    public async Task Corte_Convencional_En_Su_Optimo_Con_Las_Que_No_Estan_En_Riesgo()
    {
        // Sembrado hace 33 días: 34 × 20,5 = 697 GDD (pasado el óptimo convencional de 650).
        var lotId = await LotAsync(33, developing: 4, risk: 1);

        var cut = Active(await ViewAsync(lotId), OptimizationContractType.ConventionalCut);
        Assert.Equal("Cortar convencional ahora", cut.Title);
        Assert.Equal(11_800m, cut.Impact); // 4 × 250 g × $11.800/kg
    }

    [Fact]
    public async Task PH_Alto_Firme_Pide_Bajarlo_Y_Anomalias_Muestra_Que_Corregir()
    {
        var lotId = await LotAsync(5, developing: 6);
        await using (var context = Ctx())
        {
            await OptimizationSqlFixture.AddReadingAsync(context, lotId, "ph-solucion", 7.1m, DateTime.UtcNow.AddMinutes(-15));
            await OptimizationSqlFixture.AddReadingAsync(context, lotId, "ph-solucion", 7.2m, DateTime.UtcNow.AddMinutes(-5));
        }

        var solution = await _fixture.Solutions.GetLotAsync(lotId);
        Assert.Equal(SolutionState.Lower, solution!.Ph.State);
        Assert.Equal(6.0m, solution.Ph.Band.Target);

        var risk = Active(await ViewAsync(lotId), OptimizationContractType.SolutionRisk);
        Assert.Equal("Bajar pH", risk.Title);
        Assert.Equal(17_700m, risk.Impact); // 6 en desarrollo × 250 g × $11.800 (rendimiento potencial)
    }

    [Fact]
    public async Task CE_Alta_Firme_Estima_El_Ahorro_Semanal_De_Nutrientes_En_Duda()
    {
        var lotId = await LotAsync(5, developing: 10);
        await using (var context = Ctx())
        {
            await OptimizationSqlFixture.AddReadingAsync(context, lotId, "ec-solucion", 2.2m, DateTime.UtcNow.AddMinutes(-15));
            await OptimizationSqlFixture.AddReadingAsync(context, lotId, "ec-solucion", 2.1m, DateTime.UtcNow.AddMinutes(-5));
        }

        var view = await ViewAsync(lotId);
        var overspend = Active(view, OptimizationContractType.NutrientOverspend);
        // 2,1 / 1,5 − 1 = 40 %; 10 plantas × 0,15 L × 7 días × 5 ml/L = 0,0525 L; × 0,4 × $25.000 = $525.
        Assert.Equal(525m, overspend.Impact);
        Assert.True(overspend.IsEstimate);
        Assert.Equal(525m, view.Kpis.WeeklySaving);
    }

    [Fact]
    public async Task Sin_Lecturas_Frescas_La_Recomendacion_De_Solucion_Queda_Como_Esta()
    {
        var lotId = await LotAsync(5, developing: 6);
        await using (var context = Ctx())
        {
            await OptimizationSqlFixture.AddReadingAsync(context, lotId, "ph-solucion", 7.1m, DateTime.UtcNow.AddMinutes(-15));
            await OptimizationSqlFixture.AddReadingAsync(context, lotId, "ph-solucion", 7.2m, DateTime.UtcNow.AddMinutes(-5));
        }
        var risk = Active(await ViewAsync(lotId), OptimizationContractType.SolutionRisk);

        // Las lecturas envejecen (2 h): no se sabe si se corrigió, así que no se cierra.
        await using (var context = Ctx())
        {
            var readings = await context.SensorReadings.Where(r => r.LotId == lotId).ToListAsync();
            foreach (var reading in readings)
                reading.ObservedAtUtc = reading.ObservedAtUtc.AddHours(-2);
            await context.SaveChangesAsync();
        }

        Assert.Equal(risk.Id, Active(await ViewAsync(lotId), OptimizationContractType.SolutionRisk).Id);
    }

    [Fact]
    public async Task Meta_Baby_Leaf_Por_Debajo_Del_Potencial_Es_Una_Meta_En_Riesgo()
    {
        var lotId = await LotAsync(BabyLeafWindowDaysAgo, apta: 1, candidate: 1, developing: 8, goal: 80m);

        var goal = Active(await ViewAsync(lotId), OptimizationContractType.CycleGoals);
        Assert.Equal("Meta Baby Leaf en riesgo", goal.Title);
        Assert.Equal(9_000m, goal.Impact); // faltan 8 − 2 = 6 plantas × 30 g × $50.000
    }

    [Fact]
    public async Task Barrido_Cierra_Las_Recomendaciones_De_Un_Lote_Que_Se_Cerro()
    {
        var lotId = await LotAsync(BabyLeafWindowDaysAgo, apta: 2, developing: 2);
        var cut = Active(await ViewAsync(lotId), OptimizationContractType.BabyLeafCut);

        await SetStatusAsync(lotId, "DESCARTADO");
        try
        {
            await _fixture.Optimization.EvaluateOpenLotsAsync();

            var view = (await _fixture.Optimization.GetViewAsync(lotId, evaluate: false))!;
            Assert.Empty(view.Active);
            Assert.Equal(OptimizationContractStatus.Expired, Assert.Single(view.History, r => r.Id == cut.Id).Status);
        }
        finally
        {
            // Un lote cerrado entra en la previsión de Simulación de los demás tests: se vuelve a abrir.
            await SetStatusAsync(lotId, "ACTIVO");
        }
    }

    private async Task SetStatusAsync(int lotId, string status)
    {
        await using var context = Ctx();
        var lot = await context.Lots.FirstAsync(l => l.Id == lotId);
        lot.StatusId = (await context.LotStatuses.FirstAsync(s => s.Name == status)).Id;
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Aceptar_Todo_Convencional_Reemplaza_El_Corte_Baby_Leaf()
    {
        var lotId = await LotAsync(BabyLeafWindowDaysAgo, apta: 3, developing: 3);
        await using (var context = Ctx())
        {
            // Precios propios del lote: el convencional deja mucho más que Baby Leaf.
            context.CostPriceCatalogs.AddRange(
                OptimizationSqlFixture.Price(lotId, OptimizationContractDestination.BabyLeaf, 1_000m),
                OptimizationSqlFixture.Price(lotId, OptimizationContractDestination.Conventional, 200_000m));
            await context.SaveChangesAsync();
        }

        var view = await ViewAsync(lotId);
        var strategy = Active(view, OptimizationContractType.HarvestStrategy);
        Assert.Equal("Cambiar a Todo convencional", strategy.Title);
        Assert.True(strategy.Impact > 10_000m);
        var cut = Active(view, OptimizationContractType.BabyLeafCut);

        Assert.True((await _fixture.Optimization.AcceptAsync(strategy.Id, "test")).Succeeded);

        var after = await ViewAsync(lotId);
        Assert.Equal(OptimizationContractStatus.Accepted, Active(after, OptimizationContractType.HarvestStrategy).Status);
        var replaced = Assert.Single(after.History, r => r.Id == cut.Id);
        Assert.Equal(OptimizationContractStatus.Expired, replaced.Status);
        Assert.Equal("Se eligió Todo convencional.", replaced.DecisionNote);
    }
}
