using System.Security.Claims;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Control;
using HydroPilotWeb.Services.Circuits;
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
        var greenhouseId = options.Greenhouses[0].Id;
        return new NewLotInput(name, options.Crops[0].Id, greenhouseId,
            DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2), rows, columns, CircuitId: await NewCircuitAsync(greenhouseId));
    }

    /// <summary>Circuito libre nuevo en la Pi del invernadero (cada prueba planta en el suyo).</summary>
    private async Task<int> NewCircuitAsync(int greenhouseId)
    {
        await using var context = _fixture.Factory.CreateDbContext();
        var nodeId = await context.IotNodes.Where(n => n.GreenhouseId == greenhouseId).Select(n => n.Id).FirstAsync();
        var circuit = await CircuitSetup.CreateAsync(context, greenhouseId, nodeId, $"Circuito {Guid.NewGuid():N}"[..20], 500m, default);
        return circuit.Id;
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

        // Plantado en su circuito desde la siembra: el circuito queda ocupado.
        var assignment = await context.CircuitAssignments.SingleAsync(a => a.LotId == lot.Id);
        Assert.Equal(lot.CircuitId, assignment.CircuitId);
        Assert.Null(assignment.ValidUntilUtc);
        Assert.Equal(lot.SowingDate.ToDateTime(TimeOnly.MinValue), assignment.ValidFromUtc);

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
    public async Task Un_Circuito_Con_Lote_En_Curso_No_Acepta_Otro()
    {
        var input = await InputAsync($"Ocupa {Guid.NewGuid():N}"[..20], rows: 1, columns: 1);
        Assert.True((await Service().CreateAsync(input, UserWithRole("Productor"))).Succeeded);

        var second = await Service().CreateAsync(input with { Name = $"Otro {Guid.NewGuid():N}"[..20] }, UserWithRole("Productor"));

        Assert.False(second.Succeeded);
        Assert.Contains("ya tiene un lote en curso", second.Errors[0]);
        var options = await Service().GetOptionsAsync();
        Assert.False(options.Circuits!.Single(c => c.Id == input.CircuitId).IsFree);
    }

    [Fact]
    public async Task Un_Circuito_Nuevo_Va_A_La_Pi_Del_Invernadero()
    {
        var circuits = new CircuitService(_fixture.Factory, NullLogger<CircuitService>.Instance);
        var greenhouse = (await circuits.GreenhousesAsync())[0];
        var name = $"Nuevo {Guid.NewGuid():N}"[..20];

        var result = await circuits.CreateAsync(greenhouse.Id, name, 300m, UserWithRole("Productor"));

        Assert.True(result.Succeeded, string.Join(" ", result.Errors));
        await using var context = _fixture.Factory.CreateDbContext();
        var pi = await CircuitSetup.PiOfAsync(context, greenhouse.Id, default);
        Assert.Equal(pi!.Identifier, greenhouse.PiIdentifier);
        var circuit = await context.Circuits.Include(c => c.Sensors).Include(c => c.Actuators).SingleAsync(c => c.Name == name);
        Assert.Equal(pi.Id, circuit.NodeId);
        Assert.Equal(300m, circuit.ReservoirLiters);
        Assert.Equal(2, circuit.Sensors.Count);
        Assert.Equal(4, circuit.Actuators.Count);
        Assert.All(circuit.Sensors, s => Assert.Equal(pi.Id, s.NodeId));
        Assert.All(circuit.Actuators, a => Assert.Equal(pi.Id, a.NodeId));
    }

    [Fact]
    public async Task Un_Circuito_Sin_Sondas_Recibe_Las_Del_Sufijo_De_Sus_Bombas()
    {
        await using var context = _fixture.Factory.CreateDbContext();
        var greenhouseId = (await Service().GetOptionsAsync()).Greenhouses[0].Id;
        var pi = await CircuitSetup.PiOfAsync(context, greenhouseId, default);
        // Como el circuito que la migración trae de una Pi que nunca tuvo sensores: solo sus bombas.
        var circuit = new Circuit { GreenhouseId = greenhouseId, NodeId = pi!.Id, Name = $"Sin sondas {Guid.NewGuid():N}"[..20], CreatedAt = DateTime.UtcNow };
        context.Circuits.Add(circuit);
        await context.SaveChangesAsync();
        var suffix = $"-x{circuit.Id}";
        context.Actuators.AddRange(ControlContract.DefaultActuators.Select(p => new Actuator
        {
            NodeId = pi.Id, CircuitId = circuit.Id, Kind = p.Kind, TechnicalKey = p.Kind + suffix, Name = p.Name, Unit = p.Unit,
            MaxPerCommand = p.MaxPerCommand, MaxPerDay = p.MaxPerDay, IsActive = true, CreatedAt = DateTime.UtcNow,
        }));
        await context.SaveChangesAsync();

        await CircuitSetup.EnsureProbesAsync(context, default);
        await CircuitSetup.EnsureProbesAsync(context, default); // ya tiene: no agrega otras

        var keys = await context.Sensors.Where(s => s.CircuitId == circuit.Id).OrderBy(s => s.TechnicalKey).Select(s => s.TechnicalKey).ToListAsync();
        Assert.Equal(["ec-solucion" + suffix, "ph-solucion" + suffix], keys);
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
