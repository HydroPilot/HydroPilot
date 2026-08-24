using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;

namespace HydroPilotWeb.Services.Lotes;

/// <summary>
/// Flujo diario del lote (plan 09 / LOT-07), ejecutable bajo demanda o por un
/// worker futuro. Nunca se ejecuta desde Razor: los componentes solo leen
/// agregados (LotAggregateService) o invocan este servicio (demo/admin).
///
/// Pasos: GDD del lote → etapa fenológica → evaluación por planta ACTIVA
/// (riesgo → ventana Baby Leaf + score → convencional) → agregados →
/// predominancia (Mixto) → readiness híbrido de cosecha.
/// Las plantas cosechadas/descartadas NO cambian: su estado es histórico.
/// El GDD NO se persiste aquí (campo AccumulatedGdd es del módulo forecasting).
/// </summary>
public class LotDailyFlowService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly GddService _gddService;
    private readonly PlantEvaluationService _plantEvaluationService;
    private readonly LotAggregateService _aggregateService;
    private readonly IPlantRiskProvider _riskProvider;

    public LotDailyFlowService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        GddService gddService,
        PlantEvaluationService plantEvaluationService,
        LotAggregateService aggregateService,
        IPlantRiskProvider riskProvider)
    {
        _dbFactory = dbFactory;
        _gddService = gddService;
        _plantEvaluationService = plantEvaluationService;
        _aggregateService = aggregateService;
        _riskProvider = riskProvider;
    }

    public async Task<LotDailyFlowResult?> RunDailyAsync(
        int lotId,
        DateOnly? asOfDate = null,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();

        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var lot = await context.Lots
            .Include(l => l.CropType)
            .Include(l => l.Status)
            .FirstOrDefaultAsync(l => l.Id == lotId, ct);
        if (lot is null)
            return null;

        // 1. GDD del lote (fuente canónica: GddService, propiedad de forecasting).
        var lotGdd = await _gddService.GetAccumulatedGddAsync(lot, asOfDate, ct);
        if (lotGdd == 0)
            warnings.Add("GDD acumulado 0: sin lecturas de temperatura en el período o siembra reciente.");

        // 2-3. Etapa fenológica predominante (y aplicada) y umbrales desde catálogos.
        var thresholds = await PlantEvaluationService.ResolveThresholdsAsync(context, lot.CropTypeId, ct);
        if (thresholds is null)
        {
            warnings.Add("Faltan catálogos activos (etapas fenológicas y/o configuración Baby Leaf): no se evalúan estados comerciales.");
        }

        var phenoStage = await PlantEvaluationService.ResolvePhenologicalStageAsync(context, lot.CropTypeId, lotGdd, ct)
                         ?? lot.PredominantPhenologicalStage;
        if (phenoStage is null)
        {
            warnings.Add("Sin etapa fenológica activa para el cultivo: no se puede aplicar receta de EC.");
        }

        if (phenoStage is not null)
            lot.AppliedPhenologicalStageId = phenoStage.Id;

        // 4-6. Riesgos por planta (contrato con anomalies; NoPlantRiskProvider mientras no exista).
        var risks = await _riskProvider.GetActiveRisksAsync(lotId, ct);

        // 7-9. Evaluar SOLO plantas activas (las demás quedan congeladas).
        var plants = await context.Plants
            .Include(p => p.CommercialStage)
            .Where(p => p.LotId == lotId)
            .ToListAsync(ct);

        var evaluated = 0;
        var changed = 0;
        foreach (var plant in plants.Where(p => p.OperationalState == PlantOperationalState.Activa))
        {
            evaluated++;
            risks.TryGetValue(plant.Id, out var risk);
            var didChange = thresholds is not null && phenoStage is not null
                ? await _plantEvaluationService.EvaluateActivePlantAsync(
                    context, plant, lot.CropTypeId, phenoStage.Id, lotGdd, thresholds, risk, ct)
                : false;
            if (didChange)
                changed++;
        }

        // 10-11. Agregados y predominancia (con Mixto) sobre activas.
        if (phenoStage is not null)
            lot.PredominantPhenologicalStageId = phenoStage.Id;

        // Resolver nombres por Id (las navegaciones de los plants cargados en
        // memoria quedaron obsoletas tras actualizar CommercialStageId).
        var stageNames = await context.CommercialStages
            .Where(s => s.CropTypeId == lot.CropTypeId)
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var activeCounts = new Dictionary<string, int>();
        foreach (var plant in plants.Where(p => p.OperationalState == PlantOperationalState.Activa))
        {
            var name = plant.CommercialStageId is { } stageId
                       && stageNames.TryGetValue(stageId, out var resolved)
                ? resolved
                : "(sin estado)";
            activeCounts[name] = activeCounts.GetValueOrDefault(name) + 1;
        }
        var (predominantName, isMixed) = Predominance.Resolve(activeCounts);

        var predominantStage = !isMixed && predominantName is not null
            ? await context.CommercialStages
                .Where(s => s.CropTypeId == lot.CropTypeId && s.Name == predominantName)
                .FirstOrDefaultAsync(ct)
            : null;

        lot.PredominantCommercialStageId = predominantStage?.Id;
        lot.IsCommercialStageMixed = isMixed;

        // 12. Readiness híbrido (GDD en ventana + porcentaje aptas configurable).
        var readiness = await _aggregateService.ComputeReadinessAsync(lot, lotGdd, activeCounts, plants, ct);

        await context.SaveChangesAsync(ct);

        return new LotDailyFlowResult(
            LotId: lot.Id,
            GddAccumulated: Math.Round(lotGdd, 2),
            AppliedPhenologicalStageId: lot.AppliedPhenologicalStageId,
            PlantsEvaluated: evaluated,
            PlantsChanged: changed,
            PredominantCommercialStageName: isMixed ? CommercialStageNames.Mixto : predominantName,
            IsCommercialStageMixed: isMixed,
            HarvestReadiness: readiness,
            Warnings: warnings);
    }
}