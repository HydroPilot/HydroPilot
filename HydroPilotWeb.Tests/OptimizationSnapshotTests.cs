using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Optimization;
using HydroPilotWeb.Services.Optimization;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Snapshot de optimización (OPT-01/OPT-04): el hash canónico es reproducible
/// (mismo snapshot ⇒ mismo hash), depende del contenido (una lectura nueva, un
/// cambio de GDD o de precio cambia el hash) y NO depende del orden de los datos
/// ni del momento de generación. Repetir el cálculo con el mismo snapshot no
/// duplica recomendaciones (idempotencia por hash).
/// </summary>
public class OptimizationSnapshotTests
{
    private static readonly DateOnly SowingDate = new(2026, 8, 1);

    private static OptimizationSnapshot Snapshot(IReadOnlyList<SnapshotReading>? readings = null,
        decimal gdd = 320m, decimal? ecObjective = 1.5m, string? stageName = "Crecimiento vegetativo",
        IReadOnlyList<SnapshotPriceCost>? priceCosts = null) => new(
        OptimizationContract.RuleVersion,
        LotId: 1, LotName: "Lote 1", CropTypeName: "Lechuga Baby Leaf", AreaM2: 4.5m,
        StatusName: "ACTIVO", SowingDate,
        readings ?? [Ph("6.0", 5), Ec("1.5", 4)],
        CurrentPh: 6.0m, CurrentEc: 1.5m, PhUsableCount: 1, EcUsableCount: 1,
        PhAgeMinutes: 5, EcAgeMinutes: 4,
        gdd, 300m, stageName, ecObjective,
        new DateOnly(2026, 8, 25), 4,
        YieldBaseKgM2: 3.0m, AccuracyCycles: 2,
        DataSourceSummary: "sensor 14/14 días", SourceKind: "Observed",
        ActivePlants: 10, RiskPlantCount: 0, AptaPlantCount: 7, AptaPercent: 70m,
        IsCommercialStageMixed: false,
        priceCosts ?? [new SnapshotPriceCost("baby_leaf", "price_per_kg", 2600m, "ARS", "demo", new DateOnly(2026, 7, 1), null)]);

    private static SnapshotReading Ph(string value, int minutesAgo) =>
        new("pH", TelemetryContract.QualityValid, decimal.Parse(value), DateTime.UtcNow.AddMinutes(-minutesAgo));

    private static SnapshotReading Ec(string value, int minutesAgo) =>
        new("CE", TelemetryContract.QualityValid, decimal.Parse(value), DateTime.UtcNow.AddMinutes(-minutesAgo));

    [Fact]
    public void Mismo_Snapshot_Mismo_Hash_Reproducible()
    {
        var a = Snapshot();
        var b = Snapshot();

        Assert.Equal(OptimizationSnapshotBuilder.ComputeHash(a), OptimizationSnapshotBuilder.ComputeHash(b));
    }

    [Fact]
    public void Una_Lectura_Nueva_Cambia_El_Hash()
    {
        var baseSnapshot = Snapshot([Ph("6.0", 5), Ec("1.5", 4)]);
        var newer = Snapshot([Ph("6.0", 5), Ph("6.1", 2), Ec("1.5", 4)]);

        Assert.NotEqual(
            OptimizationSnapshotBuilder.ComputeHash(baseSnapshot),
            OptimizationSnapshotBuilder.ComputeHash(newer));
    }

    [Fact]
    public void Cambio_De_GDD_Cambia_El_Hash()
    {
        Assert.NotEqual(
            OptimizationSnapshotBuilder.ComputeHash(Snapshot(gdd: 320m)),
            OptimizationSnapshotBuilder.ComputeHash(Snapshot(gdd: 355m)));
    }

    [Fact]
    public void Cambio_De_Objetivo_De_EC_Por_Etapa_Cambia_El_Hash()
    {
        Assert.NotEqual(
            OptimizationSnapshotBuilder.ComputeHash(Snapshot(ecObjective: 1.5m)),
            OptimizationSnapshotBuilder.ComputeHash(Snapshot(ecObjective: 1.7m)));
    }

    [Fact]
    public void Cambio_De_Precio_Del_Catalogo_Cambia_El_Hash()
    {
        var otherCatalog = new List<SnapshotPriceCost>
        {
            new("baby_leaf", "price_per_kg", 3000m, "ARS", "demo", new DateOnly(2026, 7, 1), null),
        };

        Assert.NotEqual(
            OptimizationSnapshotBuilder.ComputeHash(Snapshot()),
            OptimizationSnapshotBuilder.ComputeHash(Snapshot(priceCosts: otherCatalog)));
    }

    [Fact]
    public void Orden_De_Lecturas_No_Afecta_El_Hash()
    {
        var shuffled = Snapshot([Ec("1.5", 4), Ph("6.0", 5)]); // orden invertido
        var ordered = Snapshot([Ph("6.0", 5), Ec("1.5", 4)]);

        Assert.Equal(
            OptimizationSnapshotBuilder.ComputeHash(shuffled),
            OptimizationSnapshotBuilder.ComputeHash(ordered));
    }

    [Fact]
    public void El_Hash_No_Depende_De_La_Fecha_De_Calculo()
    {
        // OptimizationSnapshot no incluye GeneratedAtUtc: el mismo estado de
        // entradas calculado hoy o mañana produce el mismo hash (reproducible).
        var snapshot = Snapshot();
        var hash1 = OptimizationSnapshotBuilder.ComputeHash(snapshot);
        var hash2 = OptimizationSnapshotBuilder.ComputeHash(snapshot with { });
        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length);
    }

    [Fact]
    public void Json_Canonico_Es_Estable_Entre_Corridas()
    {
        Assert.Equal(
            OptimizationSnapshotBuilder.ToCanonicalJson(Snapshot()),
            OptimizationSnapshotBuilder.ToCanonicalJson(Snapshot()));
    }
}