using System.Security.Claims;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HydroPilotWeb.Tests.Integration;

/// <summary>
/// Alta de lotes desde la vista Lotes (LotCreationService) sobre SQL Server: grilla con una planta
/// por posición, sin m², y validaciones de nombre y rol.
/// </summary>
[Collection("forecast-sql")]
public class LotCreationSqlServerTests
{
    private readonly ForecastSqlFixture _fixture;

    public LotCreationSqlServerTests(ForecastSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private LotCreationService Service() => new(_fixture.Factory, NullLogger<LotCreationService>.Instance);

    private static ClaimsPrincipal UserWithRole(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, $"test-{role}"), new Claim(ClaimTypes.Role, role)], "test"));

    private async Task<NewLotInput> InputAsync(string name, int rows = 2, int columns = 3)
    {
        var options = await Service().GetOptionsAsync();
        return new NewLotInput(name, options.Crops[0].Id, options.Greenhouses[0].Id,
            DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2), rows, columns);
    }

    [Fact]
    public async Task Crea_El_Lote_Con_Una_Planta_En_Desarrollo_Por_Posicion()
    {
        var name = $"Alta {Guid.NewGuid():N}"[..20];

        var result = await Service().CreateAsync(await InputAsync(name), UserWithRole("Operador"));

        Assert.True(result.Succeeded, string.Join(" ", result.Errors));
        await using var context = _fixture.Factory.CreateDbContext();
        var lot = await context.Lots.Include(l => l.Status).SingleAsync(l => l.Id == result.LotId);
        Assert.Equal(name, lot.Name);
        Assert.Equal(LotStatusNames.Activo, lot.Status!.Name);
        Assert.Equal(2, lot.GridRows);
        Assert.Equal(3, lot.GridColumns);
        Assert.Equal(0m, lot.PlantedAreaM2); // sin m²: el lote se mide por plantas

        var plants = await context.Plants.Include(p => p.CommercialStage).Where(p => p.LotId == lot.Id).ToListAsync();
        Assert.Equal(6, plants.Count);
        Assert.Equal(6, plants.Select(p => (p.Row, p.Column)).Distinct().Count());
        Assert.All(plants, p =>
        {
            Assert.Equal(PlantOperationalState.Activa, p.OperationalState);
            Assert.Equal(CommercialStageNames.EnDesarrollo, p.CommercialStage!.Name);
            Assert.NotNull(p.PhenologicalStageId);
        });
    }

    [Fact]
    public async Task Rechaza_Nombre_Repetido_Y_Roles_Sin_Permiso()
    {
        var name = $"Repetido {Guid.NewGuid():N}"[..20];
        var input = await InputAsync(name, rows: 1, columns: 1);
        Assert.True((await Service().CreateAsync(input, UserWithRole("Productor"))).Succeeded);

        var repeated = await Service().CreateAsync(input with { Name = $"  {name.ToUpperInvariant()} " }, UserWithRole("Productor"));
        var support = await Service().CreateAsync(input with { Name = name + "b" }, UserWithRole("Soporte Técnico"));

        Assert.False(repeated.Succeeded);
        Assert.Contains("Ya hay un lote", repeated.Errors[0]);
        Assert.False(support.Succeeded);
    }

    [Fact]
    public async Task Sugiere_Un_Nombre_Libre()
    {
        var options = await Service().GetOptionsAsync();

        await using var context = _fixture.Factory.CreateDbContext();
        Assert.StartsWith("Lote ", options.SuggestedName);
        Assert.False(await context.Lots.AnyAsync(l => l.Name == options.SuggestedName));
    }
}
