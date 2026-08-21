using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Optimization;
using HydroPilotWeb.Services.Optimization;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Integración del módulo de optimización sobre SQL Server local (plan 16):
/// generación idempotente por snapshot, bloqueo por frescura/calidad, acciones
/// aceptar/descartar idempotentes con motivo, expiración, comparativa económica
/// sin precios y cero llamadas a hardware (nada de dosis ni comandos).
/// </summary>
[Collection("optimization-sql")]
public class OptimizationSqlServerTests : IAsyncLifetime
{
    private readonly OptimizationSqlFixture _fixture;

    public OptimizationSqlServerTests(OptimizationSqlFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    private HydroPilotDbContext Ctx() => _fixture.NewContext();

    /// <summary>Lote fresco con lecturas de pH/CE fuera de rango y asignadas al lote.</summary>
    private async Task<(int LotId, DateTime NowUtc)> SeedOutOfRangeLotAsync(decimal ph = 7.0m, decimal ec = 2.0m)
    {
        await using var context = Ctx();
        var seeded = await OptimizationSqlFixture.SeedOperativeLotAsync(context, currentPh: null, currentEc: null);
        var now = DateTime.UtcNow;
        await OptimizationSqlFixture.AddReadingAsync(context, "ph-solucion", ph, now.AddMinutes(-5));
        await OptimizationSqlFixture.AddReadingAsync(context, "ec-solucion", ec, now.AddMinutes(-4));
        await OptimizationSqlFixture.AssignLatestReadingsToLotAsync(context, seeded.LotId);
        return (seeded.LotId, now);
    }

    [Fact]
    public async Task Generar_Crea_Recomendacion_Quimica_Cuando_Ph_CE_Fueran_De_Rango()
    {
        var (lotId, _) = await SeedOutOfRangeLotAsync();

        var result = await _fixture.Optimization.GenerateAsync(lotId);
        Assert.NotNull(result);

        var chemical = result!.State.ActiveRecommendations
            .FirstOrDefault(r => r.RecommendationType == OptimizationContractType.ChemicalSolution);
        Assert.NotNull(chemical);
        Assert.Equal(OptimizationContractStatus.Pending, chemical!.Status);
        Assert.Equal(OptimizationContractDirection.Lower, chemical.Direction);
        Assert.Equal(6.0m, chemical.TargetValue); // objetivo de pH del cultivo
        Assert.True(chemical.ConfidencePercent > 0);
    }

    [Fact]
    public async Task Repetir_Calculo_Con_Mismo_Snapshot_No_Duplica()
    {
        var (lotId, _) = await SeedOutOfRangeLotAsync();

        var first = await _fixture.Optimization.GenerateAsync(lotId);
        var second = await _fixture.Optimization.GenerateAsync(lotId);

        var firstId = first!.State.ActiveRecommendations
            .First(r => r.RecommendationType == OptimizationContractType.ChemicalSolution).Id;
        var secondId = second!.State.ActiveRecommendations
            .First(r => r.RecommendationType == OptimizationContractType.ChemicalSolution).Id;

        Assert.Equal(firstId, secondId);
        Assert.Equal(first.State.ActiveRecommendations.Count, second.State.ActiveRecommendations.Count);
        Assert.True(second.DuplicatedSnapshotsSkipped);

        await using var context = Ctx();
        var count = await context.OptimizationRecommendations
            .CountAsync(r => r.RecommendationType == OptimizationContractType.ChemicalSolution && r.LotId == lotId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Nuevas_Lecturas_Generan_Nueva_Recomendacion_Y_Expiram_La_Anterior()
    {
        var (lotId, _) = await SeedOutOfRangeLotAsync();
        var first = await _fixture.Optimization.GenerateAsync(lotId);
        var oldHash = first!.State.ActiveRecommendations
            .First(r => r.RecommendationType == OptimizationContractType.ChemicalSolution).SnapshotHash;

        // El estado cambia (lectura nueva): hash distinto ⇒ recomendación nueva;
        // la PENDING anterior queda EXPIRED en el historial.
        await using var context = Ctx();
        var now = DateTime.UtcNow;
        await OptimizationSqlFixture.AddReadingAsync(context, "ph-solucion", 6.2m, now.AddMinutes(-2));
        await OptimizationSqlFixture.AssignLatestReadingsToLotAsync(context, lotId);

        var second = (await _fixture.Optimization.GenerateAsync(lotId))!;
        var newHash = second!.State.ActiveRecommendations
            .First(r => r.RecommendationType == OptimizationContractType.ChemicalSolution).SnapshotHash;
        Assert.NotEqual(oldHash, newHash);

        var history = await _fixture.Optimization.GetHistoryAsync(lotId);
        Assert.Contains(history, r => r.Status == OptimizationContractStatus.Expired);
    }

    [Fact]
    public async Task Aceptar_Es_Idempotente_Y_Registra_Accion_Manual()
    {
        var (lotId, _) = await SeedOutOfRangeLotAsync();
        var result = await _fixture.Optimization.GenerateAsync(lotId);
        var rec = result!.State.ActiveRecommendations
            .First(r => r.RecommendationType == OptimizationContractType.ChemicalSolution);

        var first = await _fixture.Optimization.AcceptAsync(rec.Id, "Revisado por operador", "test-user");
        Assert.True(first.Succeeded);
        Assert.False(first.Repeated);
        Assert.Equal(OptimizationContractStatus.Accepted, first.Recommendation!.Status);

        var repeat = await _fixture.Optimization.AcceptAsync(rec.Id, "otra nota", "test-user");
        Assert.True(repeat.Succeeded);
        Assert.True(repeat.Repeated);

        // Solo UNA acción persistida (la transición de estado es única).
        await using var context = Ctx();
        var actions = await context.RecommendationActions.Where(a => a.RecommendationId == rec.Id).ToListAsync();
        Assert.Single(actions);
        Assert.Equal(OptimizationContractAction.Accept, actions[0].Action);
    }

    [Fact]
    public async Task Descartar_Requiere_Motivo_Y_Es_Idempotente()
    {
        var (lotId, _) = await SeedOutOfRangeLotAsync();
        var result = await _fixture.Optimization.GenerateAsync(lotId);
        var rec = result!.State.ActiveRecommendations
            .First(r => r.RecommendationType == OptimizationContractType.ChemicalSolution);

        var withoutReason = await _fixture.Optimization.DiscardAsync(rec.Id, "", "test-user");
        Assert.False(withoutReason.Succeeded);
        Assert.Contains("Motivo", withoutReason.Message);

        var first = await _fixture.Optimization.DiscardAsync(rec.Id, "Decisión de operación: ajuste manual del reservorio", "test-user");
        Assert.True(first.Succeeded);
        Assert.Equal(OptimizationContractStatus.Discarded, first.Recommendation!.Status);
        Assert.Equal("Decisión de operación: ajuste manual del reservorio", first.Recommendation.DecisionNote);

        var repeat = await _fixture.Optimization.DiscardAsync(rec.Id, "otro motivo", "test-user");
        Assert.True(repeat.Succeeded);
        Assert.True(repeat.Repeated);
    }

    [Fact]
    public async Task Aceptar_Una_Descartada_Es_Error_Claro()
    {
        var (lotId, _) = await SeedOutOfRangeLotAsync();
        var result = await _fixture.Optimization.GenerateAsync(lotId);
        var rec = result!.State.ActiveRecommendations
            .First(r => r.RecommendationType == OptimizationContractType.ChemicalSolution);

        await _fixture.Optimization.DiscardAsync(rec.Id, "No aplica por cambio de cultivo", "test-user");
        var accept = await _fixture.Optimization.AcceptAsync(rec.Id, null, "test-user");

        Assert.False(accept.Succeeded);
        Assert.Contains("descartada", accept.Message);
    }

    [Fact]
    public async Task Lectura_Vieja_Bloquea_La_Recomendacion_Quimica()
    {
        await using var context = Ctx();
        var seeded = await OptimizationSqlFixture.SeedOperativeLotAsync(context, currentPh: null, currentEc: null);
        var now = DateTime.UtcNow;
        // Lecturas de hace 150 minutos: ventana de frescura default = 60.
        await OptimizationSqlFixture.AddReadingAsync(context, "ph-solucion", 7.0m, now.AddMinutes(-150));
        await OptimizationSqlFixture.AddReadingAsync(context, "ec-solucion", 2.0m, now.AddMinutes(-148));
        await OptimizationSqlFixture.AssignLatestReadingsToLotAsync(context, seeded.LotId);

        var result = await _fixture.Optimization.GenerateAsync(seeded.LotId);
        var chemical = result!.State.ActiveRecommendations
            .FirstOrDefault(r => r.RecommendationType == OptimizationContractType.ChemicalSolution);

        Assert.NotNull(chemical);
        Assert.Equal(OptimizationContractStatus.Blocked, chemical!.Status);
        Assert.Contains("frescura", chemical.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Lectura_Con_Calidad_Invalida_Bloquea_Lo_Quimico()
    {
        await using var context = Ctx();
        var seeded = await OptimizationSqlFixture.SeedOperativeLotAsync(context, currentPh: null, currentEc: null);
        var now = DateTime.UtcNow;
        await OptimizationSqlFixture.AddReadingAsync(context, "ph-solucion", 7.0m, now.AddMinutes(-5), TelemetryContract.QualityInvalid);
        await OptimizationSqlFixture.AddReadingAsync(context, "ec-solucion", 2.0m, now.AddMinutes(-4), TelemetryContract.QualityInvalid);
        await OptimizationSqlFixture.AssignLatestReadingsToLotAsync(context, seeded.LotId);

        var result = await _fixture.Optimization.GenerateAsync(seeded.LotId);
        var chemical = result!.State.ActiveRecommendations
            .FirstOrDefault(r => r.RecommendationType == OptimizationContractType.ChemicalSolution);

        Assert.NotNull(chemical);
        Assert.Equal(OptimizationContractStatus.Blocked, chemical!.Status);
        Assert.Contains("utilizables", chemical.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sin_Precios_La_Comparacion_Economica_Es_No_Calculable()
    {
        await using var context = Ctx();
        var seeded = await OptimizationSqlFixture.SeedOperativeLotAsync(context, currentPh: null, currentEc: null);
        await OptimizationSqlFixture.AddReadingAsync(context, "ph-solucion", 6.0m, DateTime.UtcNow.AddMinutes(-5));
        await OptimizationSqlFixture.AddReadingAsync(context, "ec-solucion", 1.5m, DateTime.UtcNow.AddMinutes(-4));
        await OptimizationSqlFixture.AssignLatestReadingsToLotAsync(context, seeded.LotId);

        // Sin catálogo de precios vigente (backup para restaurar: la base es
        // compartida por la colección serializada).
        var backup = await context.CostPriceCatalogs.AsNoTracking().ToListAsync();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM dbo.CostPriceCatalogs");

        var result = await _fixture.Optimization.GenerateAsync(seeded.LotId);
        var economic = result!.State.ActiveRecommendations
            .FirstOrDefault(r => r.RecommendationType == OptimizationContractType.EconomicAlternative);

        Assert.NotNull(economic);
        Assert.Equal(OptimizationContractStatus.Blocked, economic!.Status);
        Assert.Contains(OptimizationContract.NotCalculableReason, economic.Explanation);
        Assert.Null(result.State.EconomicComparison!.RecommendedDestination);
        Assert.False(result.State.EconomicComparison.Calculable);

        // Restaurar el catálogo para los tests siguientes (sin ids explícitos).
        context.CostPriceCatalogs.AddRange(backup.Select(b => new CostPriceCatalog
        {
            CropTypeId = b.CropTypeId,
            LotId = b.LotId,
            Destination = b.Destination,
            Item = b.Item,
            Value = b.Value,
            Currency = b.Currency,
            ValidFrom = b.ValidFrom,
            ValidUntil = b.ValidUntil,
            Source = b.Source,
            CreatedAtUtc = DateTime.UtcNow,
        }));
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Con_Precios_Y_Diferencia_Mayor_Al_Umbral_Recomienda_Destino()
    {
        var (lotId, _) = await SeedOutOfRangeLotAsync();
        var result = await _fixture.Optimization.GenerateAsync(lotId);

        Assert.NotNull(result!.State.EconomicComparison);
        Assert.True(result.State.EconomicComparison!.Calculable);
        Assert.True(result.State.EconomicComparison.ExceedsThreshold);

        var economic = result.State.ActiveRecommendations
            .FirstOrDefault(r => r.RecommendationType == OptimizationContractType.EconomicAlternative);
        Assert.NotNull(economic);
        Assert.Equal(OptimizationContractDirection.Switch, economic!.Direction);
        Assert.Equal(OptimizationContractPriority.High, economic.Priority);
    }

    [Fact]
    public async Task Pendiente_Sin_Decision_Se_Expira_Por_Antiguedad()
    {
        var (lotId, _) = await SeedOutOfRangeLotAsync();
        await _fixture.Optimization.GenerateAsync(lotId);

        await using var context = Ctx();
        var pending = await context.OptimizationRecommendations
            .Where(r => r.LotId == lotId && r.Status == OptimizationContractStatus.Pending)
            .ToListAsync();
        Assert.NotEmpty(pending);
        foreach (var rec in pending)
        {
            rec.GeneratedAtUtc = DateTime.UtcNow.AddHours(-48);
        }
        await context.SaveChangesAsync();

        var expired = await _fixture.Optimization.ExpirePendingAsync();
        Assert.True(expired >= pending.Count);

        var history = await _fixture.Optimization.GetHistoryAsync(lotId);
        Assert.Contains(history, r => r.Status == OptimizationContractStatus.Expired && r.DecisionNote!.Contains("antigüedad"));
    }

    [Fact]
    public async Task Snapshot_Reproducible_En_Integracion()
    {
        var (lotId, _) = await SeedOutOfRangeLotAsync();
        var first = await _fixture.Optimization.GenerateAsync(lotId);
        var second = await _fixture.Optimization.GenerateAsync(lotId);

        var h1 = first!.State.ActiveRecommendations
            .First(r => r.RecommendationType == OptimizationContractType.ChemicalSolution).SnapshotHash;
        var h2 = second!.State.ActiveRecommendations
            .First(r => r.RecommendationType == OptimizationContractType.ChemicalSolution).SnapshotHash;
        Assert.Equal(h1, h2);
    }

    [Fact]
    public async Task Explicacion_No_Contiene_Dosis_Exactas_Ni_Mililitros()
    {
        var (lotId, _) = await SeedOutOfRangeLotAsync();
        var result = await _fixture.Optimization.GenerateAsync(lotId);

        foreach (var rec in result!.State.ActiveRecommendations)
        {
            Assert.DoesNotContain("ml", rec.Explanation, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("mililitros", rec.Explanation, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("mililitros", rec.DataSourceSummary, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Riesgo_Comercial_Se_Muestra_Y_No_Bloquea_La_Quimica()
    {
        var (lotId, _) = await SeedOutOfRangeLotAsync();
        var result = await _fixture.Optimization.GenerateAsync(lotId);

        // La química sigue PENDING (el riesgo no bloquea la recomendación de lote).
        var chemical = result!.State.ActiveRecommendations
            .First(r => r.RecommendationType == OptimizationContractType.ChemicalSolution);
        Assert.Equal(OptimizationContractStatus.Pending, chemical.Status);

        // El hallazgo comercial (1 planta en Riesgo / fuera de ventana) se
        // persiste con dirección VERIFY y prioridad alta; el lote no se descarta.
        var commercial = result.State.ActiveRecommendations
            .FirstOrDefault(r => r.RecommendationType == OptimizationContractType.CommercialGrowout);
        Assert.NotNull(commercial);
        Assert.Equal(OptimizationContractDirection.Verify, commercial!.Direction);
        Assert.Equal(OptimizationContractPriority.High, commercial.Priority);
        Assert.Contains("No se descarta el lote", commercial.Explanation);
        // El riesgo por conteo comercial (estado "Riesgo / fuera de ventana") no
        // bloquea la recomendación química ni descarta el lote.
        Assert.Equal(2, result.State.AptaPlantCount);
        Assert.Equal(4, result.State.ActivePlants);
        Assert.Equal(50m, result.State.AptaPercent);
    }

    [Fact]
    public async Task Lote_Cosechado_No_Genera_Recomendaciones_Nuevas()
    {
        await using var context = Ctx();
        var seeded = await OptimizationSqlFixture.SeedOperativeLotAsync(context, currentPh: null, currentEc: null);
        var cosechado = await context.LotStatuses.FirstAsync(s => s.Name == "COSECHADO");
        var lot = await context.Lots.FindAsync(seeded.LotId);
        lot!.StatusId = cosechado.Id;
        lot.ActualHarvestDate = DateOnly.FromDateTime(DateTime.UtcNow);
        await context.SaveChangesAsync();

        var result = await _fixture.Optimization.GenerateAsync(seeded.LotId);
        Assert.NotNull(result);
        Assert.Empty(result!.State.ActiveRecommendations);
        Assert.Contains(result.State.Warnings, w => w.Contains("COSECHADO", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cero_Llamadas_A_Hardware_No_Existe_Tabla_De_Comandos()
    {
        // Verificación de diseño sobre la base migrada: el módulo no persiste
        // comandos/órdenes; solo recomendaciones, detalles y acciones manuales.
        await using var context = Ctx();
        var tables = (await context.Database
                .SqlQueryRaw<string>("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME LIKE '%Command%' OR TABLE_NAME LIKE '%Actuator%'")
                .ToListAsync());
        Assert.Empty(tables);

        // La generación solo toca las tablas del módulo + consumidores.
        var (lotId, _) = await SeedOutOfRangeLotAsync();
        await _fixture.Optimization.GenerateAsync(lotId);
        var snapshots = await context.OptimizationRecommendations
            .AnyAsync(r => r.LotId == lotId && !string.IsNullOrEmpty(r.SnapshotJson));
        Assert.True(snapshots);
    }
}