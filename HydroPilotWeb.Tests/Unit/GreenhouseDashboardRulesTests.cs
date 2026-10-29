using HydroPilotWeb.Components.Dashboard;
using HydroPilotWeb.Models.Notifications;
using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Dashboard;
using HydroPilotWeb.Services.Lotes;
using Xunit;

namespace HydroPilotWeb.Tests.Unit;

public class GreenhouseDashboardRulesTests
{
    [Theory]
    [InlineData(123569, "$123k")]      // hacia abajo: no promete de más
    [InlineData(999999, "$999k")]
    [InlineData(1250000, "$1,2M")]
    [InlineData(1299999, "$1,2M")]
    [InlineData(950, "$950")]
    [InlineData(0, "$0")]
    [InlineData(-4500, "-$4k")]
    public void Importe_Corto(double value, string expected)
    {
        Assert.Equal(expected, DashboardFormat.CompactMoney((decimal)value, "ARS"));
    }

    [Fact]
    public void Importe_Corto_En_Otra_Moneda()
    {
        Assert.Equal("USD 12k", DashboardFormat.CompactMoney(12_400m, "USD"));
    }

    [Theory]
    [InlineData(NotificationTypes.DesbalanceQuimico, 7, "optimization?lote=7")]
    [InlineData(NotificationTypes.FallaDosificacion, 7, "optimization?lote=7")]
    [InlineData(NotificationTypes.CosechaInminente, 7, "lotes?lote=7")]
    [InlineData(NotificationTypes.Info, 7, "lotes?lote=7")]
    [InlineData(NotificationTypes.DesbalanceQuimico, null, "notifications")]
    public void Cada_Alerta_Lleva_A_Su_Vista(string type, int? lotId, string link)
    {
        Assert.Equal(link, GreenhouseDashboardRules.AlertLink(type, lotId));
    }

    [Fact]
    public void Error_De_Sensor_Lleva_A_Optimizacion_Con_Su_Circuito_Y_El_Nodo_Desconectado_A_Optimizacion()
    {
        Assert.Equal("optimization?circuito=3", GreenhouseDashboardRules.AlertLink(NotificationTypes.ErrorSensor, 7, 3));
        Assert.Equal("optimization", GreenhouseDashboardRules.AlertLink(NotificationTypes.ErrorSensor, null));
        Assert.Equal("optimization", GreenhouseDashboardRules.AlertLink(NotificationTypes.NodoDesconectado, null));
        Assert.Equal("optimization?lote=7", GreenhouseDashboardRules.AlertLink(NotificationTypes.NodoDesconectado, 7));
    }

    [Fact]
    public void La_Huella_De_Nodo_Desconectado_Lleva_El_Nodo_Y_El_Corte()
    {
        var fingerprint = NodeConnectionAlerts.Fingerprint(12, new DateTime(2026, 10, 22, 22, 50, 1));
        Assert.Equal("node-offline-12-20261022225001", fingerprint);
        Assert.Equal(12, NodeConnectionAlerts.NodeId(fingerprint));
        Assert.StartsWith(NodeConnectionAlerts.Prefix(12), fingerprint);
        Assert.False(fingerprint.StartsWith(NodeConnectionAlerts.Prefix(1)));   // "node-offline-1-" no toma al nodo 12
        Assert.Null(NodeConnectionAlerts.NodeId("control-pause-3-pH-20261022"));
        Assert.Null(NodeConnectionAlerts.NodeId(null));
    }

    private static CycleMilestone At(int day) => new(0m, new DateOnly(2026, 11, day), true, day > 20);

    private static CycleTimeline Timeline(CyclePhase phase) => new(
        new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 25), 7, phase, 0m,
        At(1), At(4), At(10), At(22), At(28));

    [Fact]
    public void En_Desarrollo_Muestra_Los_Dos_Cortes()
    {
        var harvests = GreenhouseDashboardRules.Harvests(5, "Lote B18", Timeline(CyclePhase.Desarrollo)).ToList();

        Assert.Equal([HarvestDestination.BabyLeaf, HarvestDestination.Convencional], harvests.Select(h => h.Destination));
        Assert.Equal(new DateOnly(2026, 11, 4), harvests[0].Date);
        Assert.False(harvests[0].Approximate);
        Assert.True(harvests[1].Approximate);                 // más allá del pronóstico: ≈
        Assert.All(harvests, h => Assert.Equal("lotes?lote=5", h.Link));
    }

    [Fact]
    public void Cerrada_La_Ventana_Baby_Leaf_Queda_Solo_El_Convencional()
    {
        var harvests = GreenhouseDashboardRules.Harvests(5, "Lote B18", Timeline(CyclePhase.VentanaConvencional)).ToList();
        Assert.Equal(HarvestDestination.Convencional, Assert.Single(harvests).Destination);
    }

    [Fact]
    public void En_Sobremadurez_No_Hay_Proximos_Cortes()
    {
        Assert.Empty(GreenhouseDashboardRules.Harvests(5, "Lote B18", Timeline(CyclePhase.Sobremadurez)));
    }

    [Fact]
    public void Proximo_Chequeo_Es_La_Ultima_Calibracion_Mas_El_Intervalo()
    {
        var today = new DateOnly(2026, 10, 25);
        Assert.Equal(new DateOnly(2026, 10, 24), GreenhouseDashboardRules.NextCheck(new DateOnly(2026, 10, 10), 14, today)); // vencido ayer
        Assert.Equal(today, GreenhouseDashboardRules.NextCheck(null, 90, today));                                         // sin registro: hoy
    }

    [Fact]
    public void En_El_Dashboard_Van_Los_Vencidos_Y_Los_Que_Vencen_En_7_Dias_O_Menos()
    {
        var today = new DateOnly(2026, 10, 28);
        Assert.True(GreenhouseDashboardRules.DueSoon(today.AddDays(-3), today));
        Assert.True(GreenhouseDashboardRules.DueSoon(today, today));
        Assert.True(GreenhouseDashboardRules.DueSoon(today.AddDays(7), today));
        Assert.False(GreenhouseDashboardRules.DueSoon(today.AddDays(8), today));
    }

    [Fact]
    public void Total_Solo_Si_Estan_Todos_Los_Lotes()
    {
        Assert.Equal(300m, GreenhouseDashboardRules.Total([100m, 200m]));
        Assert.Null(GreenhouseDashboardRules.Total([100m, null]));
        Assert.Equal(0m, GreenhouseDashboardRules.Total([]));
    }

    [Fact]
    public void Importe_De_Un_Lote_Ignora_Destinos_Sin_Plantas()
    {
        var withoutPrice = new YieldScenario(new DestinationYield(3, 0.36m, 5274m), new DestinationYield(0, 0m, null));
        Assert.Equal(5274m, GreenhouseDashboardRules.Revenue(withoutPrice));

        var missing = new YieldScenario(new DestinationYield(3, 0.36m, 5274m), new DestinationYield(2, 0.56m, null));
        Assert.Null(GreenhouseDashboardRules.Revenue(missing));
    }
}
