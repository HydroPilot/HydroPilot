using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Anomalies;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Resolución de bandas operativas (ANO-01): pH desde CropType, CE desde la etapa
/// fenológica; temperatura/humedad pendientes salvo baseline explícito en el catálogo.
/// </summary>
public class AnomalyBandResolverTests
{
    private static CropType CropWithPh(decimal? min = 5.8m, decimal? max = 6.2m) => new()
    {
        Name = "Lechuga Baby Leaf",
        OptimalPhMin = min,
        OptimalPhMax = max,
        OptimalPhTarget = 6.0m,
        OptimalEcMin = 1.2m,
        OptimalEcMax = 1.8m,
    };

    private static PhenologicalStage Stage => new()
    {
        Name = "Crecimiento vegetativo",
        EcMin = 1.2m,
        EcObjective = 1.5m,
        EcMax = 1.8m,
    };

    [Fact]
    public void Ph_BandComesFromCropConfiguration()
    {
        var band = AnomalyBandResolver.ResolveOperative(CropWithPh(), null, AnomalyContract.SensorTypePh, null);
        Assert.NotNull(band);
        Assert.Equal(5.8m, band.OperationalMin);
        Assert.Equal(6.2m, band.OperationalMax);
        Assert.Equal(6.0m, band.Target);
        Assert.Equal(0m, band.PhysicalMin);  // rango físico desde el catálogo de IoT (referencia)
        Assert.Equal(14m, band.PhysicalMax);
    }

    [Fact]
    public void Ph_WithoutApprovedRange_IsPending()
    {
        var band = AnomalyBandResolver.ResolveOperative(CropWithPh(null, null), null, AnomalyContract.SensorTypePh, null);
        Assert.Null(band);
    }

    [Fact]
    public void Ce_BandComesFromAppliedPhenologicalStage()
    {
        var band = AnomalyBandResolver.ResolveOperative(CropWithPh(), Stage, AnomalyContract.SensorTypeCe, null);
        Assert.NotNull(band);
        Assert.Equal(1.2m, band.OperationalMin);
        Assert.Equal(1.8m, band.OperationalMax);
        Assert.Equal(1.5m, band.Target);
    }

    [Fact]
    public void Ce_WithoutStage_IsPending()
    {
        var band = AnomalyBandResolver.ResolveOperative(CropWithPh(), null, AnomalyContract.SensorTypeCe, null);
        Assert.Null(band);
    }

    [Fact]
    public void Temperature_WithoutBaseline_IsPending()
    {
        var band = AnomalyBandResolver.ResolveOperative(CropWithPh(), null, AnomalyContract.SensorTypeTemperatura, null);
        Assert.Null(band);
    }

    [Fact]
    public void Temperature_WithExplicitBaseline_UsesCatalogRow()
    {
        var row = new AnomalyRuleCatalog
        {
            Code = AnomalyContract.TypeTemperaturaFueraDeBanda,
            SensorTypeName = AnomalyContract.SensorTypeTemperatura,
            OperationalMin = 12m,
            OperationalMax = 32m,
            TargetValue = 24m,
        };
        var band = AnomalyBandResolver.ResolveOperative(CropWithPh(), null, AnomalyContract.SensorTypeTemperatura, row);
        Assert.NotNull(band);
        Assert.Equal(12m, band.OperationalMin);
        Assert.Equal(32m, band.OperationalMax);
        Assert.Equal(24m, band.Target);
    }

    [Fact]
    public void Temperature_WithOnlyOneBoundary_StillEvaluates()
    {
        var row = new AnomalyRuleCatalog
        {
            Code = AnomalyContract.TypeTemperaturaFueraDeBanda,
            SensorTypeName = AnomalyContract.SensorTypeTemperatura,
            OperationalMax = 35m,
        };
        var band = AnomalyBandResolver.ResolveOperative(CropWithPh(), null, AnomalyContract.SensorTypeTemperatura, row);
        Assert.NotNull(band);
        Assert.Null(band.OperationalMin);
        Assert.Equal(35m, band.OperationalMax);
    }

    [Fact]
    public void Registry_DefinesTheFourStableRuleTypes()
    {
        var codes = AnomalyRuleRegistry.Templates.Select(t => t.Code).ToHashSet();
        Assert.Equal(
            new[]
            {
                AnomalyContract.TypePhFueraDeBanda,
                AnomalyContract.TypeCeFueraDeBanda,
                AnomalyContract.TypeTemperaturaFueraDeBanda,
                AnomalyContract.TypeHumedadFueraDeBanda,
            }.ToHashSet(),
            codes);
    }

    [Theory]
    [InlineData(AnomalyContract.StatusSeguimiento, AnomalyContract.ContractOpen)]
    [InlineData(AnomalyContract.StatusAbierta, AnomalyContract.ContractOpen)]
    [InlineData(AnomalyContract.StatusReconocida, AnomalyContract.ContractAcknowledged)]
    [InlineData(AnomalyContract.StatusResuelta, AnomalyContract.ContractResolved)]
    public void ContractStatus_MapsInternalStatesToSharedContract(string internalStatus, string expected)
    {
        Assert.Equal(expected, AnomalyContract.ToContractStatus(internalStatus));
    }
}