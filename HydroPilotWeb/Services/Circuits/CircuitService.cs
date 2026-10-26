using System.Security.Claims;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Control;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Services.Circuits;

/// <summary>
/// Circuito como lo muestra Ajustes &gt; Circuitos: el tanque, el lote en curso y las claves que tiene que usar la Pi del
/// invernadero para sus sensores y bombas.
/// </summary>
public sealed record CircuitRow(
    int Id,
    int GreenhouseId,
    string GreenhouseName,
    string Name,
    string NodeIdentifier,
    string Kind,
    int GridRows,
    int GridColumns,
    decimal ReservoirLiters,
    bool IsActive,
    string? CurrentLot,
    IReadOnlyList<string> SensorKeys,
    IReadOnlyList<string> PumpKeys);

/// <summary>Invernadero donde se puede crear un circuito (el que tiene Pi) y la Pi que lo maneja.</summary>
public sealed record CircuitGreenhouseChoice(int Id, string Name, string PiIdentifier);

/// <summary>
/// Un circuito como lo necesita la Pi: claves de sus sondas y bombas (tipo → clave), litros del tanque, plantas activas
/// del lote que tiene ahora y días desde su siembra (null sin lote).
/// </summary>
public sealed record DeviceCircuitConfig(
    string Name,
    string Kind,
    decimal Liters,
    string? PhKey,
    string? EcKey,
    IReadOnlyDictionary<string, string> Pumps,
    int Plants,
    int? LotDays);

/// <summary>Configuración de la Pi (GET /api/nodos/{nodo}/circuitos): sus circuitos y las claves de temperatura y humedad.</summary>
public sealed record DeviceConfigResponse(string NodeId, string? TemperatureKey, string? HumidityKey, IReadOnlyList<DeviceCircuitConfig> Circuits);

public sealed record CircuitResult(bool Succeeded, IReadOnlyList<string> Errors)
{
    public static CircuitResult Ok() => new(true, []);
    public static CircuitResult Fail(params string[] errors) => new(false, errors);
}

/// <summary>
/// Ajustes &gt; Circuitos: alta y edición de los circuitos de cada invernadero. Al crear uno se generan sus sensores
/// de pH y CE y sus 4 bombas (<see cref="CircuitSetup"/>). Todos los ven; solo Administrador y Productor los editan.
/// </summary>
public sealed class CircuitService
{
    public static readonly IReadOnlyList<string> EditorRoles = ["Administrador", "Productor"];

    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;
    private readonly ILogger<CircuitService> _logger;

    public CircuitService(IDbContextFactory<HydroPilotDbContext> dbFactory, ILogger<CircuitService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public static bool CanEdit(ClaimsPrincipal user) => EditorRoles.Any(user.IsInRole);

    public async Task<IReadOnlyList<CircuitRow>> ListAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var circuits = await context.Circuits.AsNoTracking()
            .OrderBy(c => c.Greenhouse!.Name).ThenBy(c => c.Name)
            .Select(c => new
            {
                c.Id,
                c.GreenhouseId,
                Greenhouse = c.Greenhouse!.Name,
                c.Name,
                Node = c.Node!.Identifier,
                c.Kind,
                c.GridRows,
                c.GridColumns,
                c.ReservoirLiters,
                c.IsActive,
                Lot = c.Assignments.Where(a => a.ValidUntilUtc == null).Select(a => a.Lot!.Name).FirstOrDefault(),
                Sensors = c.Sensors.Where(s => s.IsActive).OrderBy(s => s.Id).Select(s => s.TechnicalKey).ToList(),
                Pumps = c.Actuators.OrderBy(a => a.Id).Select(a => a.TechnicalKey).ToList(),
            })
            .ToListAsync(ct);
        return circuits
            .Select(c => new CircuitRow(c.Id, c.GreenhouseId, c.Greenhouse, c.Name, c.Node, c.Kind, c.GridRows, c.GridColumns,
                c.ReservoirLiters, c.IsActive, c.Lot, c.Sensors, c.Pumps))
            .ToList();
    }

    /// <summary>Invernaderos con Pi: una por invernadero, que maneja todos sus circuitos.</summary>
    public async Task<IReadOnlyList<CircuitGreenhouseChoice>> GreenhousesAsync(CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var nodes = await context.IotNodes.AsNoTracking()
            .OrderBy(n => n.Id)
            .Select(n => new { n.GreenhouseId, Greenhouse = n.Greenhouse!.Name, n.Identifier })
            .ToListAsync(ct);
        return nodes
            .GroupBy(n => n.GreenhouseId)
            .Select(g => g.First())
            .OrderBy(n => n.Greenhouse)
            .Select(n => new CircuitGreenhouseChoice(n.GreenhouseId, n.Greenhouse, n.Identifier))
            .ToList();
    }

    /// <summary>"Circuito N" libre del invernadero, para sugerir el nombre del alta.</summary>
    public async Task<string> SuggestNameAsync(int greenhouseId, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        return await CircuitSetup.NextNameAsync(context, greenhouseId, ct);
    }

    /// <summary>Alta: el circuito en la Pi del invernadero, con sus sensores de pH y CE y sus 4 bombas.</summary>
    public async Task<CircuitResult> CreateAsync(int greenhouseId, string? name, decimal liters, ClaimsPrincipal user,
        string kind = CircuitKinds.Dense, int gridRows = Circuit.DefaultGridRows, int gridColumns = Circuit.DefaultGridColumns,
        CancellationToken ct = default)
    {
        if (!CanEdit(user))
            return CircuitResult.Fail("Solo Administrador y Productor pueden editar los circuitos.");
        name = name?.Trim();
        var errors = Validate(name, liters, kind, gridRows, gridColumns);
        if (errors.Count > 0)
            return new CircuitResult(false, errors);

        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var node = await CircuitSetup.PiOfAsync(context, greenhouseId, ct);
        if (node is null)
            return CircuitResult.Fail("El invernadero no tiene una Pi que maneje el circuito.");
        if (await context.Circuits.AnyAsync(c => c.GreenhouseId == node.GreenhouseId && c.Name == name, ct))
            return CircuitResult.Fail($"Ya hay un circuito llamado \"{name}\" en el invernadero.");

        var circuit = await CircuitSetup.CreateAsync(context, node.GreenhouseId, node.Id, name!, Math.Round(liters, 1), ct,
            kind, gridRows, gridColumns);
        _logger.LogInformation("Circuito {CircuitId} \"{Name}\" creado en {Node} por {User}.", circuit.Id, circuit.Name, node.Identifier, user.Identity?.Name);
        return CircuitResult.Ok();
    }

    /// <summary>
    /// Cambia nombre, litros del tanque, si está activo, el tipo y la grilla. Con un lote en curso no se desactiva ni se
    /// cambia el tipo o la grilla (las plantas ocupan sus huecos).
    /// </summary>
    public async Task<CircuitResult> UpdateAsync(int circuitId, string? name, decimal liters, bool isActive, ClaimsPrincipal user,
        string? kind = null, int? gridRows = null, int? gridColumns = null, CancellationToken ct = default)
    {
        if (!CanEdit(user))
            return CircuitResult.Fail("Solo Administrador y Productor pueden editar los circuitos.");
        name = name?.Trim();

        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var circuit = await context.Circuits.FirstOrDefaultAsync(c => c.Id == circuitId, ct);
        if (circuit is null)
            return CircuitResult.Fail("El circuito no existe.");
        kind ??= circuit.Kind;
        gridRows ??= circuit.GridRows;
        gridColumns ??= circuit.GridColumns;
        var errors = Validate(name, liters, kind, gridRows.Value, gridColumns.Value);
        if (errors.Count > 0)
            return new CircuitResult(false, errors);
        if (await context.Circuits.AnyAsync(c => c.Id != circuitId && c.GreenhouseId == circuit.GreenhouseId && c.Name == name, ct))
            return CircuitResult.Fail($"Ya hay un circuito llamado \"{name}\" en el invernadero.");
        var busy = await context.CircuitAssignments.AnyAsync(a => a.CircuitId == circuitId && a.ValidUntilUtc == null, ct);
        if (!isActive && busy)
            return CircuitResult.Fail("No se puede desactivar un circuito con un lote en curso.");
        if (busy && (kind != circuit.Kind || gridRows != circuit.GridRows || gridColumns != circuit.GridColumns))
            return CircuitResult.Fail("Con un lote en curso no se puede cambiar el tipo ni la grilla del circuito.");

        circuit.Name = name!;
        circuit.ReservoirLiters = Math.Round(liters, 1);
        circuit.IsActive = isActive;
        circuit.Kind = kind;
        circuit.GridRows = gridRows.Value;
        circuit.GridColumns = gridColumns.Value;
        if (!isActive)
            circuit.ControlMode = ControlContract.ModeManual;
        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            return CircuitResult.Fail($"Ya hay un circuito llamado \"{name}\" en el invernadero.");
        }
        _logger.LogInformation("Circuito {CircuitId} actualizado por {User}: {Name}, {Liters} L, activo {Active}.",
            circuitId, user.Identity?.Name, name, liters, isActive);
        return CircuitResult.Ok();
    }

    /// <summary>
    /// Lo que la Pi necesita saber de sus circuitos (la Pi virtual arma un tanque por circuito con esto); null si el nodo
    /// no existe.
    /// </summary>
    public async Task<DeviceConfigResponse?> DeviceConfigAsync(string nodeIdentifier, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var node = await context.IotNodes.AsNoTracking().FirstOrDefaultAsync(n => n.Identifier == nodeIdentifier, ct);
        if (node is null)
            return null;

        var today = DateOnly.FromDateTime(nowUtc);
        var circuits = await context.Circuits.AsNoTracking()
            .Where(c => c.NodeId == node.Id && c.IsActive)
            .OrderBy(c => c.Id)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Kind,
                c.ReservoirLiters,
                Ph = c.Sensors.Where(s => s.IsActive && s.SensorType!.Name == AnomalyContract.SensorTypePh).Select(s => s.TechnicalKey).FirstOrDefault(),
                Ec = c.Sensors.Where(s => s.IsActive && s.SensorType!.Name == AnomalyContract.SensorTypeCe).Select(s => s.TechnicalKey).FirstOrDefault(),
                Pumps = c.Actuators.Where(a => a.IsActive).Select(a => new { a.Kind, a.TechnicalKey }).ToList(),
                Lot = c.Assignments.Where(a => a.ValidUntilUtc == null).Select(a => new { a.LotId, a.Lot!.SowingDate, DenseCircuit = a.Lot.CircuitId }).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var result = new List<DeviceCircuitConfig>();
        foreach (var c in circuits)
        {
            var plants = c.Lot is { } lot
                ? await context.Plants.CountAsync(p => p.LotId == lot.LotId && p.OperationalState == PlantOperationalState.Activa
                                                       && (p.CircuitId == c.Id || (p.CircuitId == null && lot.DenseCircuit == c.Id)), ct)
                : 0;
            result.Add(new DeviceCircuitConfig(c.Name, c.Kind, c.ReservoirLiters, c.Ph, c.Ec,
                c.Pumps.GroupBy(p => p.Kind).ToDictionary(g => g.Key, g => g.First().TechnicalKey), plants,
                c.Lot is { } current ? today.DayNumber - current.SowingDate.DayNumber : null));
        }

        var ambient = await context.Sensors.AsNoTracking()
            .Where(s => s.NodeId == node.Id && s.CircuitId == null && s.IsActive)
            .Select(s => new { Type = s.SensorType!.Name, s.TechnicalKey })
            .ToListAsync(ct);
        return new DeviceConfigResponse(
            node.Identifier,
            ambient.FirstOrDefault(s => s.Type == AnomalyContract.SensorTypeTemperatura)?.TechnicalKey,
            ambient.FirstOrDefault(s => s.Type == AnomalyContract.SensorTypeHumedad)?.TechnicalKey,
            result);
    }

    private static List<string> Validate(string? name, decimal liters, string kind, int gridRows, int gridColumns)
    {
        var errors = new List<string>();
        if (!CircuitKinds.All.Contains(kind))
            errors.Add("Elegí si el circuito es denso o de Adultas.");
        if (gridRows is < 1 or > Circuit.MaxGridSide || gridColumns is < 1 or > Circuit.MaxGridSide)
            errors.Add($"Filas y columnas tienen que ir de 1 a {Circuit.MaxGridSide}.");
        if (string.IsNullOrWhiteSpace(name))
            errors.Add("Poné un nombre para el circuito.");
        else if (name.Length > Circuit.NameMaxLength)
            errors.Add($"El nombre puede tener hasta {Circuit.NameMaxLength} caracteres.");
        if (liters <= 0m || liters > ControlContract.MaxReservoirLiters)
            errors.Add($"El tanque tiene que tener entre 1 y {ControlContract.MaxReservoirLiters:0} litros.");
        return errors;
    }
}
