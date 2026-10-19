using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Control;
using HydroPilotWeb.Services.Anomalies;
using HydroPilotWeb.Services.Control;
using HydroPilotWeb.Services.Dashboard;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Tests;

/// <summary>Histórico de ciclos de prueba (en producción lo arma Lotes con BabyLeafOutlookService).</summary>
public sealed class FakeLotHistory : ILotHistorySource
{
    public List<LotCycleOutcome> Outcomes { get; } = [];

    public Task<IReadOnlyList<LotCycleOutcome>> GetCycleOutcomesAsync(int cropTypeId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LotCycleOutcome>>(Outcomes.ToList());
}

/// <summary>
/// Receta aprendida contra SQL Server: dos lotes cerrados con sus lecturas y niveles guardados, el lote actual
/// con score diario, y cómo el ajuste llega al motor, al estado de la solución (Optimización) y al historial.
/// </summary>
[Collection("control-sql")]
public class RecipeSqlServerTests
{
    private readonly ControlSqlFixture _fx;

    public RecipeSqlServerTests(ControlSqlFixture fx) => _fx = fx;

    [Fact]
    public async Task Receta_por_lotes_parecidos_corre_los_objetivos_y_el_score_en_baja_la_achica()
    {
        var history = new FakeLotHistory();
        var recipes = new RecipeService(_fx.Factory, history, NullLogger<RecipeService>.Instance);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var createdLots = new List<int>();
        int? plantId = null;
        try
        {
            // 1) Sin lotes cerrados: ajuste 0, rigen los niveles de la etapa.
            var empty = await recipes.RefreshLotAsync(_fx.LotId, DateTime.UtcNow);
            Assert.Equal((0m, 0m), (empty!.PhOffset, empty.EcOffset));
            Assert.StartsWith("Sin lotes cerrados parecidos", empty.Reason);

            // 2) Dos lotes cerrados de la misma temporada: el de 80 % de Baby Leaf trabajó con pH 6,3 y CE 1,6
            //    (objetivo de la etapa 6,0 / 1,5); el de 40 %, con pH 5,8 y CE 1,4.
            var good = await ClosedLotAsync($"Bueno {tag}", ph: 6.3m, ec: 1.6m, tag);
            var bad = await ClosedLotAsync($"Malo {tag}", ph: 5.8m, ec: 1.4m, tag);
            createdLots.AddRange([good, bad]);
            var sowing = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-12).AddYears(-1);
            history.Outcomes.Add(new LotCycleOutcome(good, "Bueno", sowing, 10, 8, 2, 0, 8, 0, 0, null, false));
            history.Outcomes.Add(new LotCycleOutcome(bad, "Malo", sowing, 10, 4, 6, 0, 4, 0, 0, null, false));

            var recipe = await recipes.RefreshLotAsync(_fx.LotId, DateTime.UtcNow);
            // Promedio 60 %: cuenta el bueno (+0,3 / +0,1); evidencia 2 → 2/5 del ajuste.
            Assert.Equal((0.10m, 0.05m, 1m, 2), (recipe!.PhOffset, recipe.EcOffset, recipe.Trust, recipe.SimilarLots));

            // El motor y Optimización usan los objetivos corridos, dentro de la banda de la etapa.
            var view = await _fx.Control.GetViewAsync(_fx.NodeId);
            Assert.Equal(new SolutionBand(5.6m, 6.5m, 6.1m), view!.Ph.Band);
            Assert.Equal(new SolutionBand(1.25m, 1.8m, 1.55m), view.Ec.Band);
            Assert.Equal(0.10m, view.Recipe!.PhOffset);

            var solution = await new SolutionStatusService(_fx.Factory, Microsoft.Extensions.Options.Options.Create(new DashboardOptions()))
                .GetLotAsync(_fx.LotId);
            Assert.Equal(6.1m, solution!.Ph.Band.Target);

            // El nivel vigente queda guardado con el ajuste (origen RECIPE).
            await _fx.Control.SweepAsync(DateTime.UtcNow);
            await using (var context = _fx.NewContext())
            {
                var last = await context.LotSetpointHistories.Where(h => h.LotId == _fx.LotId)
                    .OrderByDescending(h => h.ValidFromUtc).ThenByDescending(h => h.Id).FirstAsync();
                Assert.Equal((ControlContract.SetpointSourceRecipe, 6.1m, 0.10m), (last.Source, last.PhTarget!.Value, last.PhOffset!.Value));
            }

            // 3) Con el ajuste puesto, el score Baby Leaf del lote viene bajando 2 puntos por día: confianza a la mitad.
            plantId = await DecliningScoresAsync();
            var distrusted = await recipes.RefreshLotAsync(_fx.LotId, DateTime.UtcNow);
            Assert.Equal((0.5m, -2m, 0.05m), (distrusted!.Trust, distrusted.ScoreTrend!.Value, distrusted.PhOffset));
            Assert.Contains("viene bajando", distrusted.Reason);
        }
        finally
        {
            await using var context = _fx.NewContext();
            if (plantId is { } id)
            {
                await context.BabyLeafEvaluations.Where(e => e.PlantId == id).ExecuteDeleteAsync();
                await context.Plants.Where(p => p.Id == id).ExecuteDeleteAsync();
            }
            await context.LotRecipes.ExecuteDeleteAsync();
            await context.LotSetpointHistories.ExecuteDeleteAsync();
            await context.SensorReadings.Where(r => r.ExternalReadingId.StartsWith($"rcp-{tag}")).ExecuteDeleteAsync();
            await context.Lots.Where(l => createdLots.Contains(l.Id)).ExecuteDeleteAsync();
        }
    }

    // ------------------------------------------------------------------

    /// <summary>Lote cosechado con un nivel guardado (pH 6,0 / CE 1,5, sin ajuste) y 3 lecturas de pH y CE.</summary>
    private async Task<int> ClosedLotAsync(string name, decimal ph, decimal ec, string tag)
    {
        await using var context = _fx.NewContext();
        var current = await context.Lots.AsNoTracking().SingleAsync(l => l.Id == _fx.LotId);
        var lot = new Lot
        {
            GreenhouseId = current.GreenhouseId,
            CropTypeId = current.CropTypeId,
            StatusId = await context.LotStatuses.Where(s => s.Name == LotStatusNames.Cosechado).Select(s => s.Id).SingleAsync(),
            Name = name,
            SowingDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-12).AddYears(-1),
        };
        context.Lots.Add(lot);
        await context.SaveChangesAsync();

        var start = DateTime.UtcNow.AddYears(-1);
        context.LotSetpointHistories.Add(new LotSetpointHistory
        {
            LotId = lot.Id, ValidFromUtc = start.AddDays(-1),
            PhMin = 5.5m, PhTarget = 6.0m, PhMax = 6.5m, EcMin = 1.2m, EcTarget = 1.5m, EcMax = 1.8m,
            Source = ControlContract.SetpointSourceStage, StageId = _fx.VegetativeStageId,
        });

        var sensors = await context.Sensors.Where(s => s.NodeId == _fx.NodeId)
            .Select(s => new { s.Id, Type = s.SensorType!.Name, s.MeasurementUnitId })
            .ToListAsync();
        for (var i = 0; i < 3; i++)
        {
            foreach (var sensor in sensors)
            {
                var observed = start.AddHours(i);
                context.SensorReadings.Add(new SensorReading
                {
                    SensorId = sensor.Id,
                    NodeId = _fx.NodeId,
                    LotId = lot.Id,
                    MeasurementUnitId = sensor.MeasurementUnitId,
                    Value = sensor.Type == "pH" ? ph : ec,
                    Timestamp = observed,
                    ObservedAtUtc = observed,
                    ReceivedAtUtc = observed,
                    ExternalReadingId = $"rcp-{tag}-{lot.Id}-{sensor.Id}-{i}",
                    Quality = TelemetryContract.QualityValid,
                    IngestionResult = TelemetryContract.ResultAceptada,
                    CreatedAt = DateTime.UtcNow,
                });
            }
        }
        await context.SaveChangesAsync();
        return lot.Id;
    }

    /// <summary>Una planta del lote actual con score diario 80, 78, 76, 74 en los últimos 4 días.</summary>
    private async Task<int> DecliningScoresAsync()
    {
        await using var context = _fx.NewContext();
        var cropId = await context.Lots.Where(l => l.Id == _fx.LotId).Select(l => l.CropTypeId).SingleAsync();
        var config = await context.BabyLeafConfigs.FirstOrDefaultAsync(c => c.CropTypeId == cropId);
        if (config is null)
        {
            config = new BabyLeafConfig
            {
                CropTypeId = cropId, Name = "Baby Leaf control test", GddMin = 250m, GddMax = 450m,
                ScoreMinCandidate = 60m, ScoreMinReady = 80m, Version = "1.0",
            };
            context.BabyLeafConfigs.Add(config);
        }
        var plant = new Plant { LotId = _fx.LotId, Row = 99, Column = 99 };
        context.Plants.Add(plant);
        await context.SaveChangesAsync();

        var today = DateTime.UtcNow.Date;
        for (var day = 0; day < 4; day++)
        {
            context.BabyLeafEvaluations.Add(new BabyLeafEvaluation
            {
                PlantId = plant.Id,
                BabyLeafConfigId = config.Id,
                EvaluatedAtUtc = today.AddDays(-3 + day).AddHours(8),
                BabyLeafScore = 80m - 2m * day,
                Result = "candidata",
                Confidence = 0.8m,
                ModelVersion = "test",
                GddAtEvaluation = 300m,
                MandatoryCriteriaMet = true,
            });
        }
        await context.SaveChangesAsync();
        return plant.Id;
    }
}
