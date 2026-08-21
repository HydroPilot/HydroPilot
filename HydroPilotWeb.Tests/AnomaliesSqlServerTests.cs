using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Anomalies;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Pruebas de integración del motor de anomalías (ANO-02..08) sobre SQL Server local:
/// apertura/promoción/resolución de episodios, deduplicación ante re-barridos,
/// cooldown, exclusión de inválidos, lote finalizado, riesgo por planta y consultas.
/// </summary>
[Collection("anomalies-sql")]
public class AnomaliesSqlServerTests
{
    private readonly AnomaliesSqlFixture _fixture;

    public AnomaliesSqlServerTests(AnomaliesSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private static DateTime Ago(int minutes) => DateTime.UtcNow.AddMinutes(-minutes);

    // ---------------------------------------------------------------------------
    // ANO-02/03: apertura y promoción
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SingleOutOfBandReading_RegistersWithoutCriticalEpisode()
    {
        await using var context = _fixture.NewContext();
        var lot = await AnomaliesSqlFixture.CreateActiveLotAsync(context, "Lote single-read");
        var ph = await AnomaliesSqlFixture.GetSensorAsync(context, "pH");

        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.0m, Ago(10), "single");

        await _fixture.NewEventService().SweepAsync(CancellationToken.None);

        var ev = await context.AnomalyEvents.SingleAsync(e => e.LotId == lot.Id);
        Assert.Equal(AnomalyContract.StatusSeguimiento, ev.Status);
        Assert.Equal(AnomalyContract.SeverityAdvertencia, ev.Severity);
        Assert.Equal(1, ev.ReadingCount);
        Assert.Equal(AnomalyContract.TypePhFueraDeBanda, ev.Type);
        Assert.Equal("telemetria", ev.Origin);
        Assert.Equal(AnomalyContract.ContractOpen, AnomalyContract.ToContractStatus(ev.Status));
    }

    [Fact]
    public async Task TwoConsecutiveOutOfBand_OpenCriticalEpisode()
    {
        await using var context = _fixture.NewContext();
        var lot = await AnomaliesSqlFixture.CreateActiveLotAsync(context, "Lote dos reads");
        var ph = await AnomaliesSqlFixture.GetSensorAsync(context, "pH");

        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.0m, Ago(10), "two-a");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.05m, Ago(9), "two-b");

        await _fixture.NewEventService().SweepAsync(CancellationToken.None);

        var ev = await context.AnomalyEvents.SingleAsync(e => e.LotId == lot.Id);
        Assert.Equal(AnomalyContract.StatusAbierta, ev.Status);
        Assert.Equal(AnomalyContract.SeverityCritica, ev.Severity);
        Assert.Equal(2, ev.ReadingCount);
        Assert.Equal(6.0m, ev.TargetValue); // objetivo desde CropType
    }

    [Fact]
    public async Task SweepTwice_DoesNotDuplicateEpisode()
    {
        await using var context = _fixture.NewContext();
        var lot = await AnomaliesSqlFixture.CreateActiveLotAsync(context, "Lote dedup");
        var ph = await AnomaliesSqlFixture.GetSensorAsync(context, "pH");

        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.0m, Ago(10), "dedup-a");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.05m, Ago(9), "dedup-b");

        var service = _fixture.NewEventService();
        await service.SweepAsync(CancellationToken.None);
        var first = await context.AnomalyEvents.AsNoTracking().SingleAsync(e => e.LotId == lot.Id);

        // "Reinicio": un segundo barrido (el servicio no guarda estado en memoria).
        await service.SweepAsync(CancellationToken.None);

        var all = await context.AnomalyEvents.AsNoTracking().Where(e => e.LotId == lot.Id).ToListAsync();
        Assert.Single(all); // el MISMO episodio reencuentra su fingerprint
        Assert.Equal(first.Fingerprint, all[0].Fingerprint);
        Assert.Equal(2, all[0].ReadingCount); // el delta no suma dos veces
    }

    [Fact]
    public async Task InvalidQualityReadings_NeverOpenAnAgronomicEpisode()
    {
        await using var context = _fixture.NewContext();
        var lot = await AnomaliesSqlFixture.CreateActiveLotAsync(context, "Lote invalidos");
        var ph = await AnomaliesSqlFixture.GetSensorAsync(context, "pH");

        // Físicamente inválido (calidad INVALID) → excluido del motor (nunca abre episodio).
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 25.0m, Ago(10), "invalid", TelemetryContract.QualityInvalid);
        await _fixture.NewEventService().SweepAsync(CancellationToken.None);
        Assert.Empty(await context.AnomalyEvents.Where(e => e.LotId == lot.Id).ToListAsync());

        // SUSPECT es operativamente usable: el episodio SÍ abre (la sospecha por banda
        // genérica de IoT no sustituye la banda agronómica del cultivo).
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.0m, Ago(9), "suspect-a", TelemetryContract.QualitySuspect);
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.05m, Ago(8), "suspect-b", TelemetryContract.QualitySuspect);
        await _fixture.NewEventService().SweepAsync(CancellationToken.None);

        var ev = await context.AnomalyEvents.SingleAsync(e => e.LotId == lot.Id);
        Assert.Equal(AnomalyContract.StatusAbierta, ev.Status);
    }

    [Fact]
    public async Task TemperatureRule_IsPending_AndDoesNotFire()
    {
        await using var context = _fixture.NewContext();
        var lot = await AnomaliesSqlFixture.CreateActiveLotAsync(context, "Lote temp pendiente");
        var temp = await AnomaliesSqlFixture.GetSensorAsync(context, "Temperatura");

        // 55°C es físicamente plausible (SUSPECT por la banda genérica) pero no hay
        // umbral agronómico aprobado → la regla queda pendiente y NO genera episodio.
        await AnomaliesSqlFixture.AddReadingAsync(context, temp, lot, 55m, Ago(10), "temp-a");
        await AnomaliesSqlFixture.AddReadingAsync(context, temp, lot, 56m, Ago(9), "temp-b");

        await _fixture.NewEventService().SweepAsync(CancellationToken.None);

        Assert.Empty(await context.AnomalyEvents
            .Where(e => e.LotId == lot.Id && e.Type == AnomalyContract.TypeTemperaturaFueraDeBanda)
            .ToListAsync());
    }

    // ---------------------------------------------------------------------------
    // ANO-04: recuperación, cooldown y lote finalizado
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task NormalConsecutiveReadings_ResolveEpisode()
    {
        await using var context = _fixture.NewContext();
        var lot = await AnomaliesSqlFixture.CreateActiveLotAsync(context, "Lote recuperacion");
        var ph = await AnomaliesSqlFixture.GetSensorAsync(context, "pH");

        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.0m, Ago(30), "rec-a");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.1m, Ago(29), "rec-b");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 6.0m, Ago(20), "rec-n1");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 6.05m, Ago(19), "rec-n2");

        var service = _fixture.NewEventService();
        await service.SweepAsync(CancellationToken.None); // crea el episodio
        await service.SweepAsync(CancellationToken.None); // lo resuelve

        var ev = await context.AnomalyEvents.SingleAsync(e => e.LotId == lot.Id);
        Assert.Equal(AnomalyContract.StatusResuelta, ev.Status);
        Assert.NotNull(ev.ResolvedAtUtc);
        Assert.Contains("recuperación", ev.ResolutionReason);
        Assert.Equal(AnomalyContract.ContractResolved, AnomalyContract.ToContractStatus(ev.Status));
    }

    [Fact]
    public async Task OneNormalReading_DoesNotResolveYet()
    {
        await using var context = _fixture.NewContext();
        var lot = await AnomaliesSqlFixture.CreateActiveLotAsync(context, "Lote sin recuperar");
        var ph = await AnomaliesSqlFixture.GetSensorAsync(context, "pH");

        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.0m, Ago(30), "nor-a");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.1m, Ago(29), "nor-b");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 6.0m, Ago(20), "nor-n1");

        var service = _fixture.NewEventService();
        await service.SweepAsync(CancellationToken.None);
        await service.SweepAsync(CancellationToken.None);

        var ev = await context.AnomalyEvents.SingleAsync(e => e.LotId == lot.Id);
        Assert.Equal(AnomalyContract.StatusAbierta, ev.Status); // 1 normal < 2 requeridas
    }

    [Fact]
    public async Task NewRunAfterRecovery_CreatesDistinctEpisode()
    {
        await using var context = _fixture.NewContext();
        var lot = await AnomaliesSqlFixture.CreateActiveLotAsync(context, "Lote dos corridas");
        var ph = await AnomaliesSqlFixture.GetSensorAsync(context, "pH");

        // Corrida 1 (crítica) + recuperación.
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.0m, Ago(60), "run1-a");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.1m, Ago(59), "run1-b");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 6.0m, Ago(50), "run1-n1");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 6.05m, Ago(49), "run1-n2");

        var service = _fixture.NewEventService();
        await service.SweepAsync(CancellationToken.None);
        await service.SweepAsync(CancellationToken.None); // resuelve corrida 1

        // El cooldown (30 min) bloquea reabrir la misma regla tras la resolución: lo
        // expiramos para aislar el comportamiento de "nueva corrida → nuevo episodio".
        var resolvedRun1 = await context.AnomalyEvents.SingleAsync(e => e.LotId == lot.Id);
        Assert.Equal(AnomalyContract.StatusResuelta, resolvedRun1.Status);
        resolvedRun1.ResolvedAtUtc = Ago(60);
        await context.SaveChangesAsync();

        // Corrida 2 (nueva corrida → fingerprint distinto).
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 6.9m, Ago(10), "run2-a");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 7.0m, Ago(9), "run2-b");
        await service.SweepAsync(CancellationToken.None);

        var events = (await context.AnomalyEvents.AsNoTracking().Where(e => e.LotId == lot.Id).ToListAsync())
            .OrderBy(e => e.FirstObservedAtUtc).ToList();
        Assert.Equal(2, events.Count);
        Assert.NotEqual(events[0].Fingerprint, events[1].Fingerprint);
        Assert.Equal(AnomalyContract.StatusResuelta, events[0].Status);
        Assert.Equal(AnomalyContract.StatusAbierta, events[1].Status);
    }

    [Fact]
    public async Task HarvestedLot_ResolvesOpenEpisodesAndStopsGenerating()
    {
        await using var context = _fixture.NewContext();
        var lot = await AnomaliesSqlFixture.CreateActiveLotAsync(context, "Lote cosechado");
        var ph = await AnomaliesSqlFixture.GetSensorAsync(context, "pH");

        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.0m, Ago(20), "harv-a");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.1m, Ago(19), "harv-b");

        var service = _fixture.NewEventService();
        await service.SweepAsync(CancellationToken.None);
        Assert.Equal(AnomalyContract.StatusAbierta,
            (await context.AnomalyEvents.AsNoTracking().SingleAsync(e => e.LotId == lot.Id)).Status);

        // El lote se cosecha: los episodios abiertos se resuelven y no se generan nuevos.
        await AnomaliesSqlFixture.ChangeLotStatusAsync(context, lot, "COSECHADO");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.2m, Ago(18), "harv-c");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.3m, Ago(17), "harv-d");
        await service.SweepAsync(CancellationToken.None);

        var events = await context.AnomalyEvents.AsNoTracking().Where(e => e.LotId == lot.Id).ToListAsync();
        Assert.Single(events);
        Assert.Equal(AnomalyContract.StatusResuelta, events[0].Status);
        Assert.Contains("lote finalizado", events[0].ResolutionReason);
    }

    [Fact]
    public async Task Cooldown_BlocksReopening_UntilItExpires()
    {
        await using var context = _fixture.NewContext();
        var lot = await AnomaliesSqlFixture.CreateActiveLotAsync(context, "Lote cooldown");
        var ph = await AnomaliesSqlFixture.GetSensorAsync(context, "pH");

        // Episodio 1 abierto y resuelto por recuperación.
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.0m, Ago(120), "cd1-a");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.1m, Ago(119), "cd1-b");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 6.0m, Ago(110), "cd1-n1");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 6.05m, Ago(109), "cd1-n2");

        var service = _fixture.NewEventService();
        await service.SweepAsync(CancellationToken.None);
        await service.SweepAsync(CancellationToken.None);
        var resolved = await context.AnomalyEvents.SingleAsync(e => e.LotId == lot.Id);
        Assert.Equal(AnomalyContract.StatusResuelta, resolved.Status);

        // La resolución quedó en "ahora": forzamos un instante conocido dentro del cooldown (10 min atrás).
        var resolvedAt = Ago(10);
        resolved.ResolvedAtUtc = resolvedAt;
        await context.SaveChangesAsync();

        // Nueva corrida que arranca hace 5 min: dentro del cooldown de 30 min → no se abre.
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.2m, Ago(5), "cd2-a");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.3m, Ago(4), "cd2-b");
        await service.SweepAsync(CancellationToken.None);
        Assert.Single(await context.AnomalyEvents.Where(e => e.LotId == lot.Id).ToListAsync());

        // Cooldown expirado (resolución hace 60 min > 30 min): la misma corrida abre episodio nuevo.
        resolved.ResolvedAtUtc = Ago(60);
        await context.SaveChangesAsync();
        await service.SweepAsync(CancellationToken.None);
        Assert.Equal(2, await context.AnomalyEvents.CountAsync(e => e.LotId == lot.Id));
    }

    // ---------------------------------------------------------------------------
    // ANO-07/08: riesgo por planta y conteo
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RiskProvider_SignalsAllActivePlants_WhileEpisodeOpen_AndClearsOnResolution()
    {
        await using var context = _fixture.NewContext();
        var lot = await AnomaliesSqlFixture.CreateActiveLotAsync(context, "Lote riesgo");
        var ph = await AnomaliesSqlFixture.GetSensorAsync(context, "pH");

        await AnomaliesSqlFixture.AddPlantAsync(context, lot, 1, 1);
        await AnomaliesSqlFixture.AddPlantAsync(context, lot, 1, 2);

        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.0m, Ago(20), "risk-a");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.1m, Ago(19), "risk-b");
        await _fixture.NewEventService().SweepAsync(CancellationToken.None);

        var provider = _fixture.NewRiskProvider();
        var risks = await provider.GetActiveRisksAsync(lot.Id);
        Assert.Equal(2, risks.Count);
        Assert.All(risks.Values, r => Assert.Equal("anomalies", r.Source));
        Assert.Contains("pH", risks.Values.First().Reason);

        // La señal es de riesgo, no de descarte operativo: las plantas siguen Activas.
        Assert.Equal(2, await context.Plants.CountAsync(p => p.LotId == lot.Id
            && p.OperationalState == PlantOperationalState.Activa));

        // Al resolver, el riesgo desaparece.
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 6.0m, Ago(10), "risk-n1");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 6.05m, Ago(9), "risk-n2");
        var service = _fixture.NewEventService();
        await service.SweepAsync(CancellationToken.None);
        await service.SweepAsync(CancellationToken.None);

        Assert.Empty(await provider.GetActiveRisksAsync(lot.Id));
    }

    [Fact]
    public async Task RiskCount_IsExposedPerLot_WithoutInventedLotThreshold()
    {
        await using var context = _fixture.NewContext();
        var lot = await AnomaliesSqlFixture.CreateActiveLotAsync(context, "Lote conteo riesgo");
        var ph = await AnomaliesSqlFixture.GetSensorAsync(context, "pH");
        await AnomaliesSqlFixture.AddPlantAsync(context, lot, 1, 1);

        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.0m, Ago(20), "cnt-a");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.1m, Ago(19), "cnt-b");
        await _fixture.NewEventService().SweepAsync(CancellationToken.None);

        var query = _fixture.NewQueryService();
        var summaries = await query.GetLotRiskSummariesAsync();
        var lotSummary = summaries.Single(s => s.LotId == lot.Id);

        // ANO-08: se muestra el conteo (1 planta activa en riesgo); NO se genera un
        // evento de lote automático (no hay umbral aprobado para eso).
        Assert.Equal(1, lotSummary.ActivePlants);
        Assert.Equal(1, lotSummary.PlantsAtRisk);
        Assert.Equal(1, lotSummary.OpenCriticalEpisodes);
        Assert.NotNull(lotSummary.RiskReason);
    }

    // ---------------------------------------------------------------------------
    // ANO-05: consultas de la UI (timeline, filtros, contadores)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task TimelineAndFilters_ReturnTheRightEpisodes()
    {
        await using var context = _fixture.NewContext();
        var lotA = await AnomaliesSqlFixture.CreateActiveLotAsync(context, "Lote filtro A");
        var lotB = await AnomaliesSqlFixture.CreateActiveLotAsync(context, "Lote filtro B");
        var ph = await AnomaliesSqlFixture.GetSensorAsync(context, "pH");
        var ce = await AnomaliesSqlFixture.GetSensorAsync(context, "CE");

        // lote A: pH crítico.
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lotA, 3.0m, Ago(15), "fA-a");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lotA, 3.1m, Ago(14), "fA-b");
        // lote B: CE crítico.
        await AnomaliesSqlFixture.AddReadingAsync(context, ce, lotB, 0.4m, Ago(15), "fB-a");
        await AnomaliesSqlFixture.AddReadingAsync(context, ce, lotB, 0.5m, Ago(14), "fB-b");

        await _fixture.NewEventService().SweepAsync(CancellationToken.None);

        var query = _fixture.NewQueryService();

        var byLot = await query.GetTimelineAsync(new AnomalyFilter(null, null, lotA.Id, null, null, null));
        var single = Assert.Single(byLot);
        Assert.Equal(AnomalyContract.TypePhFueraDeBanda, single.Type);
        Assert.Equal(AnomalyContract.SeverityCritica, single.Severity);
        Assert.Equal("abierta", single.ContractStatus);
        Assert.NotNull(single.RuleDescription); // explicable
        Assert.Equal(2, single.ReadingCount);

        // Los conteos globales acumulan datos de otros tests de la colección: se acotan al lote propio.
        var criticalOnly = await query.GetTimelineAsync(new AnomalyFilter(AnomalyContract.SeverityCritica, null, null, null, null, null));
        Assert.Equal(2, criticalOnly.Count(e => e.LotId == lotA.Id || e.LotId == lotB.Id));

        var resolvedOnly = await query.GetTimelineAsync(new AnomalyFilter(null, null, null, AnomalyContract.StatusResuelta, null, null));
        Assert.DoesNotContain(resolvedOnly, e => e.LotId == lotA.Id || e.LotId == lotB.Id);

        var summary = await query.GetSummaryAsync();
        Assert.True(summary.OpenCritical >= 2); // mínimo: este test sumó 2 críticas
        Assert.True(summary.HasOperationalData);

        // El contrato del evento cubre lote/sensor/regla/valores/estado (plan 02).
        Assert.NotNull(single.LotName);
        Assert.Equal("ph-solucion", single.SensorName);
        Assert.Equal(6.0m, single.TargetValue);
        Assert.Equal(3.1m, single.ObservedValue);
        Assert.Equal(5.8m, single.OperationalMin);
        Assert.Equal(6.2m, single.OperationalMax);
    }

    [Fact]
    public async Task ManualResolve_And_Acknowledge_ChangeStateExplicitly()
    {
        await using var context = _fixture.NewContext();
        var lot = await AnomaliesSqlFixture.CreateActiveLotAsync(context, "Lote acciones");
        var ph = await AnomaliesSqlFixture.GetSensorAsync(context, "pH");

        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.0m, Ago(15), "act-a");
        await AnomaliesSqlFixture.AddReadingAsync(context, ph, lot, 3.1m, Ago(14), "act-b");
        var service = _fixture.NewEventService();
        await service.SweepAsync(CancellationToken.None);
        var ev = await context.AnomalyEvents.SingleAsync(e => e.LotId == lot.Id);

        // Reconocer no resuelve (el riesgo sigue activo).
        Assert.True(await service.AcknowledgeAsync(ev.Id));
        ev = await context.AnomalyEvents.AsNoTracking().SingleAsync(e => e.LotId == lot.Id);
        Assert.Equal(AnomalyContract.StatusReconocida, ev.Status);
        Assert.Equal(AnomalyContract.ContractAcknowledged, AnomalyContract.ToContractStatus(ev.Status));
        Assert.NotNull(ev.AcknowledgedAtUtc);

        // Cierre manual explícito (ANO-04).
        Assert.True(await service.ResolveManuallyAsync(ev.Id, "verificado en campo"));
        ev = await context.AnomalyEvents.AsNoTracking().SingleAsync(e => e.LotId == lot.Id);
        Assert.Equal(AnomalyContract.StatusResuelta, ev.Status);
        Assert.Equal("verificado en campo", ev.ResolutionReason);
        Assert.NotNull(ev.ResolvedAtUtc);

        // Idempotencia de acciones sobre resuelto.
        Assert.False(await service.ResolveManuallyAsync(ev.Id));
        Assert.False(await service.AcknowledgeAsync(ev.Id));
    }
}