using System.Security.Claims;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Dashboard;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.Sensors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HydroPilotWeb.Tests.Integration;

/// <summary>
/// Dashboard del invernadero sobre SQL Server: últimos accedidos, chequeos de sensores (intervalos y
/// "Calibrado hoy") y que el resumen salga con la misma lógica de Lotes.
/// </summary>
[Collection("forecast-sql")]
public class GreenhouseDashboardSqlServerTests
{
    private readonly ForecastSqlFixture _fixture;

    public GreenhouseDashboardSqlServerTests(ForecastSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private SystemParametersService Parameters() => new(
        _fixture.Factory, new SettingsService(_fixture.Factory), NullLogger<SystemParametersService>.Instance);

    private RecentVisitService Visits() => new(_fixture.Factory, NullLogger<RecentVisitService>.Instance);

    private LotOverviewService Lots() => new(
        _fixture.Factory, _fixture.Aggregate, _fixture.Forecast, _fixture.Gdd, Parameters(), NullLogger<LotOverviewService>.Instance);

    private SensorService Sensors() => new(_fixture.Factory, Parameters(), NullLogger<SensorService>.Instance);

    private GreenhouseDashboardService Dashboard() => new(
        _fixture.Factory, Lots(), Visits(), Sensors(), NullLogger<GreenhouseDashboardService>.Instance);

    private static ClaimsPrincipal Principal(int userId, string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Role, role), new Claim("user_id", userId.ToString())], "test"));

    private async Task<User> NewUserAsync()
    {
        await using var context = _fixture.Factory.CreateDbContext();
        var user = new User { GoogleSub = $"test-{Guid.NewGuid():N}", Email = $"{Guid.NewGuid():N}@example.com", GivenName = "Dash", Role = "Operador" };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Ultimos_Accedidos_Una_Fila_Por_Vista_Y_Lote_Mas_Recientes_Primero()
    {
        var user = await NewUserAsync();
        var principal = Principal(user.Id, "Operador");
        Lot first, second;
        await using (var context = _fixture.Factory.CreateDbContext())
        {
            first = await ForecastTestData.CreateActiveLotAsync(context, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3));
            second = await ForecastTestData.CreateActiveLotAsync(context, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2));
        }

        var visits = Visits();
        await visits.RecordAsync(principal, RecentVisitKinds.Lotes, first.Id);
        await visits.RecordAsync(principal, RecentVisitKinds.Lotes, second.Id);
        await visits.RecordAsync(principal, RecentVisitKinds.Optimization, second.Id, circuitId: 1);
        await visits.RecordAsync(principal, RecentVisitKinds.Lotes, first.Id); // vuelve al primero: sube, no se duplica

        var recent = await visits.GetAsync(principal);
        Assert.Equal(3, recent.Count);
        Assert.Equal((RecentVisitKinds.Lotes, (int?)first.Id), (recent[0].Kind, recent[0].LotId));
        Assert.Equal((RecentVisitKinds.Optimization, (int?)second.Id), (recent[1].Kind, recent[1].LotId));
        Assert.Equal(first.Name, recent[0].LotName);

        // Otro usuario no ve las visitas de este.
        Assert.Empty(await visits.GetAsync(Principal((await NewUserAsync()).Id, "Operador")));
    }

    [Fact]
    public async Task Ultimos_Accedidos_Guarda_Solo_Los_Ultimos()
    {
        var user = await NewUserAsync();
        var principal = Principal(user.Id, "Operador");
        var visits = Visits();
        for (var node = 1; node <= RecentVisitService.MaxPerUser + 5; node++)
            await visits.RecordAsync(principal, RecentVisitKinds.Optimization, lotId: null, circuitId: node);

        await using var context = _fixture.Factory.CreateDbContext();
        Assert.Equal(RecentVisitService.MaxPerUser, await context.UserRecentVisits.CountAsync(v => v.UserId == user.Id));
    }

    [Fact]
    public async Task Intervalos_De_Chequeo_Se_Guardan_Por_Tipo_Y_Solo_Los_Editan_Admin_Y_Productor()
    {
        var parameters = Parameters();
        var before = await parameters.GetSensorCheckIntervalsAsync();
        Assert.NotEmpty(before);
        var type = before[0].SensorType;

        try
        {
            var denied = await parameters.UpdateSensorCheckIntervalsAsync([new SensorCheckInterval(type, 10)], Principal(1, "Operador"));
            Assert.False(denied.Succeeded);

            var invalid = await parameters.UpdateSensorCheckIntervalsAsync([new SensorCheckInterval(type, 0)], Principal(1, "Productor"));
            Assert.False(invalid.Succeeded);

            Assert.True((await parameters.UpdateSensorCheckIntervalsAsync([new SensorCheckInterval(type, 10)], Principal(1, "Productor"))).Succeeded);
            Assert.Equal(10, (await parameters.GetSensorCheckIntervalsAsync()).Single(i => i.SensorType == type).Days);

            Assert.True((await parameters.UpdateSensorCheckIntervalsAsync([new SensorCheckInterval(type, null)], Principal(1, "Administrador"))).Succeeded);
            Assert.Null((await parameters.GetSensorCheckIntervalsAsync()).Single(i => i.SensorType == type).Days);
        }
        finally
        {
            await parameters.UpdateSensorCheckIntervalsAsync(before, Principal(1, "Administrador"));
        }
    }

    [Fact]
    public async Task Calibrado_Hoy_Mueve_El_Proximo_Chequeo()
    {
        var parameters = Parameters();
        var before = await parameters.GetSensorCheckIntervalsAsync();
        int sensorId;
        string type;
        await using (var context = _fixture.Factory.CreateDbContext())
        {
            var sensor = await context.Sensors.Include(s => s.SensorType).FirstAsync(s => s.IsActive);
            sensorId = sensor.Id;
            type = sensor.SensorType!.Name;
        }

        try
        {
            await parameters.UpdateSensorCheckIntervalsAsync([new SensorCheckInterval(type, 14)], Principal(1, "Administrador"));
            var sensors = Sensors();

            Assert.False(await sensors.RegisterCalibrationAsync(sensorId, Principal(1, "no-asignado")));
            Assert.True(await sensors.RegisterCalibrationAsync(sensorId, Principal(1, "Operador")));

            var list = await sensors.ListAsync();
            var row = list.Sensors.Single(s => s.SensorId == sensorId);
            Assert.Equal(list.Today, row.LastCalibration);
            Assert.Equal(list.Today.AddDays(14), row.DueDate);
            Assert.True(list.HasIntervals);

            // El dashboard solo trae los vencidos y los que vencen en 7 días o menos: a 14 días no está; a 7, sí.
            var data = await Dashboard().GetAsync(Principal(1, "Operador"));
            Assert.DoesNotContain(data.SensorChecks, c => c.SensorId == sensorId);
            Assert.True(data.HasSensorIntervals);

            await parameters.UpdateSensorCheckIntervalsAsync([new SensorCheckInterval(type, 7)], Principal(1, "Administrador"));
            data = await Dashboard().GetAsync(Principal(1, "Operador"));
            Assert.Equal(data.Today.AddDays(7), data.SensorChecks.Single(c => c.SensorId == sensorId).DueDate);
        }
        finally
        {
            await parameters.UpdateSensorCheckIntervalsAsync(before, Principal(1, "Administrador"));
        }
    }

    [Fact]
    public async Task Resumen_Suma_Los_Lotes_Activos_Con_La_Logica_De_Lotes()
    {
        await using (var context = _fixture.Factory.CreateDbContext())
            await ForecastTestData.CreateActiveLotAsync(context, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-4));

        var data = await Dashboard().GetAsync(Principal(1, "Operador"));

        await using var check = _fixture.Factory.CreateDbContext();
        var active = await check.Lots.CountAsync(l => l.Status!.Name != "COSECHADO" && l.Status.Name != "DESCARTADO");
        Assert.Equal(active, data.ActiveLots);
        Assert.Equal(6, data.Stages.Count);
        Assert.Equal(["En desarrollo", "Baby Leaf", "Candidata", "Adulta", "Descartada", "Riesgo"], data.Stages.Select(s => s.Label));
        Assert.True(data.ExpectedBabyLeafPlants <= data.TotalPlants);
        Assert.Equal(data.Harvests.OrderBy(h => h.Date).Select(h => h.Date), data.Harvests.Select(h => h.Date));
    }
}
