using System.Text;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.Reports;

namespace HydroPilotWeb.Tests.Unit;

/// <summary>
/// Tests del módulo Reports sin base de datos: política de rangos (REP-03) y
/// exportador CSV (REP-04). Los tests de integración (SQL) viven en
/// ReportSqlServerTests.
/// </summary>
public class ReportCsvAndRangeTests
{
    // ------------------------------------------------------------------
    // REP-03: política de rangos (truncado con advertencia explícita)
    // ------------------------------------------------------------------

    [Fact]
    public void Rango_Valido_No_Se_Trunca()
    {
        var (from, to, days, warning) = ReportRangePolicy.Normalize(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal("2026-01-01", from.ToString("yyyy-MM-dd"));
        Assert.Equal("2026-01-10", to.ToString("yyyy-MM-dd"));
        Assert.Equal(10, days);
        Assert.Null(warning);
    }

    [Fact]
    public void Rango_Muy_Grande_Se_Trunca_Y_Advierte()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var (_, to, days, warning) = ReportRangePolicy.Normalize(
            from,
            from.AddDays(200));

        Assert.Equal(from.AddDays(ReportLimits.MaxRangeDays), to);
        Assert.Equal(ReportLimits.MaxRangeDays + 1, days);
        Assert.NotNull(warning);
        Assert.Contains(ReportLimits.MaxRangeDays.ToString(), warning);
        Assert.Contains("truncó", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rango_Invertido_Lanza_Error_Claro()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReportRangePolicy.Normalize(
                new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    // ------------------------------------------------------------------
    // REP-04: exportación CSV
    // ------------------------------------------------------------------

    [Fact]
    public void Csv_Detalle_Contiene_Las_Mismas_Filas_Visibles()
    {
        var result = NewTelemetryResult(
            [
                new TelemetryReadingRowDto(1, new DateTime(2026, 1, 2, 10, 30, 0, DateTimeKind.Utc),
                    "rpi-01", "ph-solucion", "pH", 6.12m, "pH", "VALID", null, "aceptada", 7, "Lote A"),
                new TelemetryReadingRowDto(2, new DateTime(2026, 1, 2, 10, 31, 0, DateTimeKind.Utc),
                    "rpi-01", "ec-solucion", "CE", 1.5m, "mS/cm", "SUSPECT", "fuera_de_banda", "aceptada", null, null),
            ]);

        var csv = CsvExportService.TelemetryReadings(result);

        // Encabezados estables + filas del DTO visible.
        var text = Encoding.UTF8.GetString(csv.Content).TrimStart($"\uFEFF");
        Assert.StartsWith("Id,ObservadoUtc (ISO-8601)", text);
        Assert.Contains("2026-01-02T10:30:00Z", text);
        Assert.Contains("6.12", text);        // decimal invariante con '.'
        Assert.Contains("2026-01-02T10:31:00Z", text);
        // Nombre determinista con el tipo y la página visible.
        Assert.Equal("hydropilot-informe-telemetria-detalle-pagina-001-", csv.FileName[.."hydropilot-informe-telemetria-detalle-pagina-001-".Length]);
    }

    [Fact]
    public void Csv_Escapa_Comas_Comillas_Y_Saltos()
    {
        var result = NewTelemetryResult(
            [
                new TelemetryReadingRowDto(1, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                    "nodo,1", "sensor \"x\"", "Tipo", 1m, "u", "VALID", "motivo, con coma", "aceptada", null, null),
            ]);

        var csv = CsvExportService.TelemetryReadings(result);
        var text = Encoding.UTF8.GetString(csv.Content).TrimStart($"\uFEFF");

        Assert.Contains("\"nodo,1\"", text);
        Assert.Contains("\"sensor \"\"x\"\"\"", text);
        Assert.Contains("\"motivo, con coma\"", text);
    }

    [Fact]
    public void Csv_Tiene_BOM_Utf8_Para_Excel()
    {
        var csv = CsvExportService.Cycle(NewCycleResult());
        Assert.True(csv.Content.Length >= 3);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, csv.Content.Take(3).ToArray());
    }

    [Fact]
    public void Csv_Ciclo_Usa_Unidades_Y_Fechas_Utc_Iso()
    {
        var csv = CsvExportService.Cycle(NewCycleResult());
        var text = Encoding.UTF8.GetString(csv.Content).TrimStart($"\uFEFF");

        Assert.Contains("Superficie (m2)", text);
        Assert.Contains("GDD acumulado", text);
        Assert.Contains("Rendimiento real (kg)", text);
        Assert.Contains("2026-01-05", text); // siembra ISO
    }

    [Fact]
    public void Csv_Estado_Lote_Separa_Activas_Cosechadas_Descartadas_Y_Vacias()
    {
        // REP-09: "Posición vacía" ≠ "Descartada" también en la exportación.
        var result = new LotStateReportResult(
            Parameters: new ReportAppliedParameters("2026-01-01 00:00:00 UTC", []),
            State: new LotStateDto(
                LotId: 1, Name: "Lote X", CropTypeName: "Lechuga", SowingDate: new DateOnly(2026, 1, 5),
                AreaM2: 10m, GddAccumulated: 300m, StatusName: "ACTIVO",
                PhenologicalStageName: "Crecimiento vegetativo", PhenologicalStageOrder: 2,
                CommercialStageName: CommercialStageNames.Mixto, IsCommercialStageMixed: true,
                CurrentPh: 6.0m, CurrentEc: 1.5m, EcObjective: 1.5m,
                GridRows: 2, GridColumns: 2, TotalConfiguredPositions: 4,
                TotalPlants: 3, ActivePlants: 1, HarvestedPlants: 1, DiscardedPlants: 1, EmptyPositions: 1,
                CommercialCounts: [new StageCountDto("En desarrollo", 1)],
                Cells: [], HarvestReadiness: null, Warnings: []),
            Warnings: []);

        var csv = CsvExportService.LotState(result);
        var text = Encoding.UTF8.GetString(csv.Content).TrimStart($"\uFEFF");

        Assert.Contains("Plantas activas,1", text);
        Assert.Contains("Plantas cosechadas,1", text);
        Assert.Contains("Plantas descartadas,1", text);
        Assert.Contains("Posiciones vacias,1", text);
        Assert.Contains("una posicion vacia NO es una planta descartada", text);
        Assert.Contains("Mixto", text);
        Assert.Contains("empate de conteos -> Mixto (regla de dominio)", text);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static TelemetryReportResult NewTelemetryResult(IReadOnlyList<TelemetryReadingRowDto> rows) =>
        new(
            Parameters: new ReportAppliedParameters("2026-01-01 00:00:00 UTC", []),
            RangeDays: 2,
            TotalReadings: rows.Count,
            UsableReadings: rows.Count,
            NonUsableReadings: 0,
            MockReadings: 0,
            Stats: [],
            TotalRows: rows.Count,
            Rows: rows,
            Page: 1,
            PageSize: ReportLimits.DefaultPageSize,
            IncludeNonUsable: false,
            Warnings: []);

    private static CycleReportResult NewCycleResult() =>
        new(
            Parameters: new ReportAppliedParameters("2026-01-01 00:00:00 UTC", []),
            Rows:
            [
                new CycleReportRowDto(
                    LotId: 1, LotName: "Lote A", CropTypeName: "Lechuga Baby Leaf", StatusName: "COSECHADO",
                    SowingDate: new DateOnly(2026, 1, 5), AreaM2: 4.5m,
                    GddAccumulated: 300m, GddTarget: 300m,
                    EstimatedHarvestDate: new DateOnly(2026, 2, 5), EstimatedHarvestSource: "predicción",
                    ActualHarvestDate: new DateOnly(2026, 2, 8),
                    EstimatedYieldKg: 12m, ActualYieldKg: 11m,
                    DaysError: 3, YieldErrorPercent: 9.1m,
                    HasPrediction: true, PredictionGeneratedAt: new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc),
                    PredictionModelVersion: "gdd-v1",
                    TemperatureReadings: 120, UsableTemperatureReadings: 118,
                    Warnings: []),
            ],
            Warnings: []);
}