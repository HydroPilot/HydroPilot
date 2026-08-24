using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HydroPilotWeb.Tests;

[Collection("anomalies-sql")]
public class AdminMaintenanceTests
{
    private readonly AnomaliesSqlFixture _fixture;

    public AdminMaintenanceTests(AnomaliesSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetDatabaseSummary_ReturnsAccurateCounts()
    {
        var service = new AdminMaintenanceService(_fixture.Factory, NullLogger<AdminMaintenanceService>.Instance);
        var summary = await service.GetDatabaseSummaryAsync();

        Assert.NotNull(summary);
        Assert.True(summary.GreenhousesCount >= 1);
        Assert.True(summary.NodesCount >= 1);
        Assert.True(summary.SensorsCount >= 1);
    }

    [Fact]
    public async Task RecreateDemoLot_And_ClearTransactionalData_CycleWorks()
    {
        var service = new AdminMaintenanceService(_fixture.Factory, NullLogger<AdminMaintenanceService>.Instance);

        // 1. Recrear lote demo
        var recreateResult = await service.RecreateDemoLotAsync();
        Assert.True(recreateResult.Success);

        await using (var context = _fixture.NewContext())
        {
            var demoLot = await context.Lots.Include(l => l.Plants).FirstOrDefaultAsync(l => l.Name == "Lote Demo 01");
            Assert.NotNull(demoLot);
            Assert.True(demoLot.Plants.Count > 0);
            var assignment = await context.NodeLotAssignments.FirstOrDefaultAsync(a => a.LotId == demoLot.Id);
            Assert.NotNull(assignment);
        }

        var summaryAfterRecreate = await service.GetDatabaseSummaryAsync();
        Assert.True(summaryAfterRecreate.LotsCount >= 1);
        Assert.True(summaryAfterRecreate.PlantsCount > 0);

        // 2. Limpiar datos transaccionales
        var clearResult = await service.ClearTransactionalDataAsync();
        Assert.True(clearResult.Success);

        var summaryAfterClear = await service.GetDatabaseSummaryAsync();
        Assert.Equal(0, summaryAfterClear.LotsCount);
        Assert.Equal(0, summaryAfterClear.PlantsCount);
        Assert.Equal(0, summaryAfterClear.SensorReadingsCount);
        Assert.True(summaryAfterClear.GreenhousesCount >= 1);
        Assert.True(summaryAfterClear.SensorsCount >= 1);
    }
}
