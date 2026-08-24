using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Services.Admin;

public sealed record DatabaseSummaryDto(
    int UsersCount,
    int GreenhousesCount,
    int NodesCount,
    int SensorsCount,
    int LotsCount,
    int ActiveLotsCount,
    int ClosedLotsCount,
    int PlantsCount,
    int TelemetryBatchesCount,
    int SensorReadingsCount,
    int PredictionsCount,
    int AnomalyEventsCount,
    int OptimizationRecommendationsCount,
    string GreenhouseTimeZone
);

public sealed record MaintenanceActionResult(
    bool Success,
    string Message,
    int RecordsAffected = 0
);

public class AdminMaintenanceService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly ILogger<AdminMaintenanceService> _logger;

    public AdminMaintenanceService(
        IDbContextFactory<HydroPilotDbContext> dbFactory,
        ILogger<AdminMaintenanceService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>Obtiene un resumen de conteos de todas las entidades clave en la base de datos.</summary>
    public async Task<DatabaseSummaryDto> GetDatabaseSummaryAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var users = await context.Users.CountAsync(ct);
        var greenhouses = await context.Greenhouses.CountAsync(ct);
        var nodes = await context.IotNodes.CountAsync(ct);
        var sensors = await context.Sensors.CountAsync(ct);
        var lots = await context.Lots.CountAsync(ct);
        var activeLots = await context.Lots.CountAsync(l => l.Status != null && l.Status.Name == "ACTIVO", ct);
        var closedLots = await context.Lots.CountAsync(l => l.Status != null && l.Status.Name == "COSECHADO", ct);
        var plants = await context.Plants.CountAsync(ct);
        var batches = await context.TelemetryBatches.CountAsync(ct);
        var readings = await context.SensorReadings.CountAsync(ct);
        var predictions = await context.Predictions.CountAsync(ct);
        var anomalies = await context.AnomalyEvents.CountAsync(ct);
        var recommendations = await context.OptimizationRecommendations.CountAsync(ct);

        var firstGh = await context.Greenhouses.AsNoTracking().FirstOrDefaultAsync(ct);
        var timeZone = firstGh?.TimeZoneId ?? "No configurada (UTC)";

        return new DatabaseSummaryDto(
            users,
            greenhouses,
            nodes,
            sensors,
            lots,
            activeLots,
            closedLots,
            plants,
            batches,
            readings,
            predictions,
            anomalies,
            recommendations,
            timeZone
        );
    }

    /// <summary>
    /// Limpia todos los datos transaccionales y de prueba (lotes, plantas, lecturas, predicciones,
    /// anomalías, recomendaciones) preservando usuarios, catálogos, nodos, sensores y configuraciones.
    /// </summary>
    public async Task<MaintenanceActionResult> ClearTransactionalDataAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        const string sql = @"
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

BEGIN TRANSACTION;

IF OBJECT_ID('Greenhouses', 'U') IS NOT NULL
    UPDATE Greenhouses SET TimeZoneId = 'America/Argentina/Buenos_Aires' WHERE TimeZoneId IS NULL OR TimeZoneId = '';

IF OBJECT_ID('RecommendationActions', 'U') IS NOT NULL DELETE FROM RecommendationActions;
IF OBJECT_ID('RecommendationDetails', 'U') IS NOT NULL DELETE FROM RecommendationDetails;
IF OBJECT_ID('OptimizationRecommendations', 'U') IS NOT NULL DELETE FROM OptimizationRecommendations;
IF OBJECT_ID('AnomalyEvents', 'U') IS NOT NULL DELETE FROM AnomalyEvents;

IF OBJECT_ID('PlantStageHistories', 'U') IS NOT NULL DELETE FROM PlantStageHistories;
IF OBJECT_ID('BabyLeafEvaluations', 'U') IS NOT NULL DELETE FROM BabyLeafEvaluations;
IF OBJECT_ID('PlantImageAnalyses', 'U') IS NOT NULL DELETE FROM PlantImageAnalyses;
IF OBJECT_ID('PlantImages', 'U') IS NOT NULL DELETE FROM PlantImages;
IF OBJECT_ID('Plants', 'U') IS NOT NULL DELETE FROM Plants;

IF OBJECT_ID('NodeLotAssignments', 'U') IS NOT NULL DELETE FROM NodeLotAssignments;
IF OBJECT_ID('Predictions', 'U') IS NOT NULL DELETE FROM Predictions;
IF OBJECT_ID('SensorReadings', 'U') IS NOT NULL DELETE FROM SensorReadings;
IF OBJECT_ID('TelemetryRejections', 'U') IS NOT NULL DELETE FROM TelemetryRejections;
IF OBJECT_ID('TelemetryBatches', 'U') IS NOT NULL DELETE FROM TelemetryBatches;

IF OBJECT_ID('Lots', 'U') IS NOT NULL DELETE FROM Lots;

IF OBJECT_ID('DailyWeatherForecasts', 'U') IS NOT NULL DELETE FROM DailyWeatherForecasts;
IF OBJECT_ID('WeatherRecords', 'U') IS NOT NULL DELETE FROM WeatherRecords;

COMMIT TRANSACTION;
";

        try
        {
            var rowsAffected = await context.Database.ExecuteSqlRawAsync(sql, ct);
            _logger.LogInformation("Limpieza de datos transaccionales ejecutada con éxito. Filas afectadas: {Rows}", rowsAffected);
            return new MaintenanceActionResult(true, "Datos transaccionales y de prueba eliminados con éxito. Se preservaron usuarios, catálogos e infraestructura.", rowsAffected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al ejecutar la limpieza de datos transaccionales");
            return new MaintenanceActionResult(false, $"Error al ejecutar la limpieza: {ex.Message}");
        }
    }

    /// <summary>
    /// Recrea el 'Lote Demo 01' activo con su grilla de 60 plantas, evaluaciones de prueba
    /// y asignación del nodo rpi-inv-01.
    /// </summary>
    public async Task<MaintenanceActionResult> RecreateDemoLotAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        try
        {
            var existingDemo = await context.Lots
                .Include(l => l.Plants)
                .FirstOrDefaultAsync(l => l.Name == "Lote Demo 01", ct);

            if (existingDemo != null)
            {
                // Limpiar entidades dependientes del lote demo anterior
                var demoId = existingDemo.Id;
                var plantIds = await context.Plants.Where(p => p.LotId == demoId).Select(p => p.Id).ToListAsync(ct);

                var evaluations = await context.BabyLeafEvaluations.Where(e => plantIds.Contains(e.PlantId)).ToListAsync(ct);
                context.BabyLeafEvaluations.RemoveRange(evaluations);

                var histories = await context.PlantStageHistories.Where(h => plantIds.Contains(h.PlantId)).ToListAsync(ct);
                context.PlantStageHistories.RemoveRange(histories);

                var plants = await context.Plants.Where(p => p.LotId == demoId).ToListAsync(ct);
                context.Plants.RemoveRange(plants);

                var assignments = await context.NodeLotAssignments.Where(a => a.LotId == demoId).ToListAsync(ct);
                context.NodeLotAssignments.RemoveRange(assignments);

                var predictions = await context.Predictions.Where(p => p.LotId == demoId).ToListAsync(ct);
                context.Predictions.RemoveRange(predictions);

                var anomalies = await context.AnomalyEvents.Where(a => a.LotId == demoId).ToListAsync(ct);
                context.AnomalyEvents.RemoveRange(anomalies);

                var recommendations = await context.OptimizationRecommendations.Where(r => r.LotId == demoId).ToListAsync(ct);
                context.OptimizationRecommendations.RemoveRange(recommendations);

                context.Lots.Remove(existingDemo);
                await context.SaveChangesAsync(ct);
            }

            // Crear el lote demo usando la lógica centralizada de DbInitializer
            DbInitializer.SeedDemoLot(context);

            return new MaintenanceActionResult(true, "Lote Demo 01 recreado correctamente con 60 posiciones de cultivo y asignación al nodo rpi-inv-01.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al recrear el lote demo");
            return new MaintenanceActionResult(false, $"Error al recrear el lote demo: {ex.Message}");
        }
    }
}
