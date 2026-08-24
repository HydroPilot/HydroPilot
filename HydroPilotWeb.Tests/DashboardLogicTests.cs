using HydroPilotWeb.Services.Dashboard;

namespace HydroPilotWeb.Tests;

/// <summary>
/// Tests de la lógica pura del dashboard (plan 12, DASH-01/04): frescura de KPIs,
/// delta real contra la lectura anterior, normalización de claves de variable y
/// agrupación de la serie temporal. No requieren base de datos.
/// </summary>
public class DashboardLogicTests
{
    private static readonly DateTime Now = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    // ------------------------------------------------------------------
    // Frescura
    // ------------------------------------------------------------------

    [Fact]
    public void Freshness_WithoutReading_IsSinDatos()
    {
        Assert.Equal(DashboardFreshness.SinDatos, DashboardFreshness.ForValue(null, Now, Window));
    }

    [Fact]
    public void Freshness_WithinWindow_IsReciente()
    {
        var observed = Now.AddMinutes(-5);
        Assert.Equal(DashboardFreshness.Reciente, DashboardFreshness.ForValue(observed, Now, Window));
    }

    [Fact]
    public void Freshness_AtExactWindowBoundary_IsReciente()
    {
        Assert.Equal(DashboardFreshness.Reciente, DashboardFreshness.ForValue(Now.AddMinutes(-30), Now, Window));
    }

    [Fact]
    public void Freshness_OlderThanWindow_IsDesactualizado()
    {
        Assert.Equal(DashboardFreshness.Desactualizado, DashboardFreshness.ForValue(Now.AddMinutes(-31), Now, Window));
    }

    [Fact]
    public void Freshness_FutureTimestamp_IsReciente()
    {
        // Una lectura futura no es "desactualizada"; la calidad FUTURE la excluye
        // de los usables, pero la frescura nunca debe degradarla por antigüedad.
        Assert.Equal(DashboardFreshness.Reciente, DashboardFreshness.ForValue(Now.AddMinutes(2), Now, Window));
    }

    // ------------------------------------------------------------------
    // Delta real
    // ------------------------------------------------------------------

    [Fact]
    public void DeltaPercent_WithoutPrevious_IsNull()
    {
        Assert.Null(DashboardFreshness.DeltaPercent(6.1m, null));
        Assert.Null(DashboardFreshness.DeltaPercent(null, 5.9m));
        Assert.Null(DashboardFreshness.DeltaPercent(null, null));
    }

    [Fact]
    public void DeltaPercent_WhenPreviousIsZero_IsNull_NotInfinity()
    {
        // Un anterior en 0 no permite un % honesto: se devuelve null, no infinito.
        Assert.Null(DashboardFreshness.DeltaPercent(1.5m, 0m));
    }

    [Fact]
    public void DeltaPercent_Increase_IsPositive()
    {
        var pct = DashboardFreshness.DeltaPercent(6.1m, 5.9m);
        Assert.NotNull(pct);
        Assert.Equal(3.39, pct!.Value, precision: 2);
    }

    [Fact]
    public void DeltaPercent_Decrease_IsNegative()
    {
        var pct = DashboardFreshness.DeltaPercent(5.9m, 6.1m);
        Assert.NotNull(pct);
        Assert.Equal(-3.28, pct!.Value, precision: 2);
    }

    [Fact]
    public void DeltaPercent_Unchanged_IsZero()
    {
        Assert.Equal(0d, DashboardFreshness.DeltaPercent(5.9m, 5.9m));
    }

    [Fact]
    public void DeltaAbsolute_BasicCases()
    {
        Assert.Equal(0.2m, DashboardFreshness.DeltaAbsolute(6.1m, 5.9m));
        Assert.Null(DashboardFreshness.DeltaAbsolute(null, 5.9m));
        Assert.Null(DashboardFreshness.DeltaAbsolute(6.1m, null));
    }

    // ------------------------------------------------------------------
    // Claves canónicas de variable (el nombre/etiqueta vienen del catálogo)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("pH", DashboardVariableKeys.Ph)]
    [InlineData("ph", DashboardVariableKeys.Ph)]
    [InlineData("CE", DashboardVariableKeys.Ce)]
    [InlineData("EC", DashboardVariableKeys.Ce)]
    [InlineData("Conductividad", DashboardVariableKeys.Ce)]
    [InlineData("Temperatura", DashboardVariableKeys.Temperatura)]
    [InlineData("temp", DashboardVariableKeys.Temperatura)]
    [InlineData("Humedad", DashboardVariableKeys.Humedad)]
    [InlineData("hum", DashboardVariableKeys.Humedad)]
    [InlineData("Presión", "presión")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_VariableNames(string? name, string expected)
    {
        Assert.Equal(expected, DashboardVariableKeys.Normalize(name));
    }

    [Fact]
    public void DisplayOrder_CanonicalKeysAreFirst()
    {
        Assert.True(DashboardVariableKeys.DisplayOrder(DashboardVariableKeys.Ph) < 100);
        Assert.True(DashboardVariableKeys.DisplayOrder(DashboardVariableKeys.Ce) < 100);
        Assert.True(DashboardVariableKeys.DisplayOrder(DashboardVariableKeys.Temperatura) < 100);
        Assert.True(DashboardVariableKeys.DisplayOrder(DashboardVariableKeys.Humedad) < 100);
        Assert.Equal(100, DashboardVariableKeys.DisplayOrder("presión"));
    }

    // ------------------------------------------------------------------
    // Agrupación de la serie temporal
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(DashboardTimeRange.Hour1, 60, 60)]   // 3600 s / 60 → 60 s por bucket
    [InlineData(DashboardTimeRange.Day1, 60, 1440)]  // 86400 / 60 → 1440 s
    [InlineData(DashboardTimeRange.Days7, 60, 10080)]
    [InlineData(DashboardTimeRange.Days30, 60, 43200)]
    public void BucketSeconds_SplitsRangeIntoAtMostMaxPoints(DashboardTimeRange range, int maxPoints, int expectedSeconds)
    {
        Assert.Equal(expectedSeconds, DashboardSeriesAggregation.BucketSeconds(range, maxPoints));
    }

    [Fact]
    public void BucketSeconds_MaxPointsLowerThanOne_ClampsToOneBucketForWholeRange()
    {
        // maxPoints <= 0 → un único bucket que cubre todo el rango (nunca 0 s ni división rara).
        Assert.Equal(3600, DashboardSeriesAggregation.BucketSeconds(DashboardTimeRange.Hour1, 0));
        Assert.Equal(86400, DashboardSeriesAggregation.BucketSeconds(DashboardTimeRange.Day1, -5));
    }

    [Fact]
    public void Durations_MatchPlanRanges()
    {
        Assert.Equal(TimeSpan.FromHours(1), DashboardTimeRange.Hour1.ToDuration());
        Assert.Equal(TimeSpan.FromDays(1), DashboardTimeRange.Day1.ToDuration());
        Assert.Equal(TimeSpan.FromDays(7), DashboardTimeRange.Days7.ToDuration());
        Assert.Equal(TimeSpan.FromDays(30), DashboardTimeRange.Days30.ToDuration());
    }
}