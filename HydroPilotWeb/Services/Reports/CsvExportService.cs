using System.Globalization;
using System.Text;
using HydroPilotWeb.Models;

namespace HydroPilotWeb.Services.Reports;

/// <summary>
/// Exportación CSV del módulo Reports (REP-04, plan 13).
///
/// Reglas:
/// - Recibe el MISMO DTO que muestra la pantalla: no vuelve a consultar con
///   reglas distintas (el CSV contiene las filas/agregados visibles).
/// - Encabezados estables (nombres fijos, no cambian entre ejecuciones).
/// - Unidades en el encabezado (entre paréntesis) y fechas UTC ISO-8601.
/// - Encoding UTF-8 con BOM (compatibilidad Excel).
/// - Separador decimal invariante ('.') y separador de campo ',' con comillas
///   dobles cuando el campo contiene comas, comillas o saltos.
/// - Nombre de archivo determinista a partir del tipo y el rango.
/// </summary>
public static class CsvExportService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ------------------------------------------------------------------
    // Telemetría: detalle de lecturas (misma página visible)
    // ------------------------------------------------------------------

    public static CsvExportResult TelemetryReadings(TelemetryReportResult result)
    {
        var headers = new[]
        {
            "Id", "ObservadoUtc (ISO-8601)", "Nodo", "Sensor", "Tipo de variable",
            "Valor", "Unidad", "Calidad", "Motivo de calidad", "Resultado de ingesta",
            "LoteId", "Lote",
        };
        var rows = result.Rows.Select(r => new[]
        {
            r.Id.ToString(Inv),
            Iso(r.ObservedAtUtc),
            r.NodeIdentifier,
            r.SensorName,
            r.SensorType,
            r.Value.ToString(Inv),
            r.Unit ?? string.Empty,
            r.Quality,
            r.QualityReason ?? string.Empty,
            r.IngestionResult,
            r.LotId?.ToString(Inv) ?? string.Empty,
            r.LotLabel ?? string.Empty,
        });
        return Build("hydropilot-informe-telemetria-detalle", result.Page, rows, headers);
    }

    /// <summary>Telemetría: estadísticas por sensor (los mismos agregados visibles).</summary>
    public static CsvExportResult TelemetryStats(TelemetryReportResult result)
    {
        var headers = new[]
        {
            "SensorId", "Sensor", "Tipo de variable", "Unidad", "NodoId", "Nodo",
            "Minimo", "Maximo", "Promedio", "Mediana", "MedianaCalculada",
            "LecturasUtilizables", "Sospechosas", "Invalidas",
            "DiasConDatos", "Cobertura%", "PrimeraObsUtc (ISO-8601)", "UltimaObsUtc (ISO-8601)",
            "UltimoValor", "UltimaCalidad",
        };
        var rows = result.Stats.Select(s => new[]
        {
            s.SensorId.ToString(Inv),
            s.SensorName,
            s.SensorType,
            s.Unit ?? string.Empty,
            s.NodeId.ToString(Inv),
            s.NodeIdentifier,
            s.Min?.ToString(Inv) ?? string.Empty,
            s.Max?.ToString(Inv) ?? string.Empty,
            s.Average?.ToString(Inv) ?? string.Empty,
            s.Median?.ToString(Inv) ?? string.Empty,
            s.MedianComputed ? "si" : "no",
            s.UsableCount.ToString(Inv),
            s.SuspectCount.ToString(Inv),
            s.InvalidCount.ToString(Inv),
            s.DaysWithData.ToString(Inv),
            s.CoveragePercent.ToString(Inv),
            s.FirstObservedAtUtc is { } f ? Iso(f) : string.Empty,
            s.LastObservedAtUtc is { } l ? Iso(l) : string.Empty,
            s.LastValue?.ToString(Inv) ?? string.Empty,
            s.LastQuality ?? string.Empty,
        });
        return Build("hydropilot-informe-telemetria-estadisticas", 0, rows, headers);
    }

    // ------------------------------------------------------------------
    // Ciclo de cultivo
    // ------------------------------------------------------------------

    public static CsvExportResult Cycle(CycleReportResult result)
    {
        var headers = new[]
        {
            "LoteId", "Lote", "Cultivo", "Estado", "Siembra (UTC)", "Superficie (m2)",
            "GDD acumulado", "GDD objetivo", "Cosecha estimada (UTC)", "Fuente de la estimacion",
            "Cosecha real (UTC)", "Rendimiento estimado (kg)", "Rendimiento real (kg)",
            "Error en dias", "Error de rendimiento (%)", "Tiene prediccion",
            "Prediccion generada (UTC)", "Modelo", "Lecturas temperatura", "Lecturas temperatura utilizables",
        };
        var rows = result.Rows.Select(r => new[]
        {
            r.LotId.ToString(Inv),
            r.LotName ?? string.Empty,
            r.CropTypeName,
            r.StatusName ?? string.Empty,
            DateOnlyIso(r.SowingDate),
            r.AreaM2.ToString(Inv),
            r.GddAccumulated.ToString(Inv),
            r.GddTarget.ToString(Inv),
            r.EstimatedHarvestDate is { } eh ? DateOnlyIso(eh) : string.Empty,
            r.EstimatedHarvestSource,
            r.ActualHarvestDate is { } ah ? DateOnlyIso(ah) : string.Empty,
            r.EstimatedYieldKg?.ToString(Inv) ?? string.Empty,
            r.ActualYieldKg?.ToString(Inv) ?? string.Empty,
            r.DaysError?.ToString(Inv) ?? string.Empty,
            r.YieldErrorPercent?.ToString(Inv) ?? string.Empty,
            r.HasPrediction ? "si" : "no",
            r.PredictionGeneratedAt is { } pg ? Iso(pg) : string.Empty,
            r.PredictionModelVersion ?? string.Empty,
            r.TemperatureReadings.ToString(Inv),
            r.UsableTemperatureReadings.ToString(Inv),
        });
        return Build("hydropilot-informe-ciclo", 0, rows, headers);
    }

    // ------------------------------------------------------------------
    // Forecast vs cosecha
    // ------------------------------------------------------------------

    public static CsvExportResult ForecastVsHarvest(ForecastVsHarvestResult result)
    {
        var headers = new[]
        {
            "LoteId", "Lote", "Cultivo", "Siembra (UTC)", "Cosecha real (UTC)", "Rendimiento real (kg)",
            "Cosecha estimada (UTC)", "Error en dias", "ErrorDiasCalculado",
            "Rendimiento estimado (kg)", "Error de rendimiento (%)",
            "Prediccion generada (UTC)", "Modelo",
        };
        var rows = result.Rows.Select(r => new[]
        {
            r.LotId.ToString(Inv),
            r.LotName ?? string.Empty,
            r.CropTypeName,
            DateOnlyIso(r.SowingDate),
            r.ActualHarvestDate is { } ah ? DateOnlyIso(ah) : string.Empty,
            r.ActualYieldKg?.ToString(Inv) ?? string.Empty,
            r.PredictedHarvestDate is { } ph ? DateOnlyIso(ph) : string.Empty,
            r.DaysError?.ToString(Inv) ?? string.Empty,
            r.DaysComputed ? "si" : "no",
            r.PredictedYieldKg?.ToString(Inv) ?? string.Empty,
            r.YieldErrorPercent?.ToString(Inv) ?? string.Empty,
            Iso(r.PredictedAt),
            r.ModelVersion ?? string.Empty,
        });
        return Build("hydropilot-informe-forecast-vs-cosecha", 0, rows, headers);
    }

    // ------------------------------------------------------------------
    // Estado de lote (mismos agregados de la leyenda visible: conteos)
    // ------------------------------------------------------------------

    public static CsvExportResult LotState(LotStateReportResult result)
    {
        var headers = new[]
        {
            "Concepto", "Valor", "Detalle",
        };
        var rows = new List<string[]>();
        var s = result.State;
        if (s is not null)
        {
            rows.Add(new[] { "LoteId", s.LotId.ToString(Inv), string.Empty });
            rows.Add(new[] { "Lote", s.Name ?? string.Empty, string.Empty });
            rows.Add(new[] { "Cultivo", s.CropTypeName, string.Empty });
            rows.Add(new[] { "Estado", s.StatusName ?? string.Empty, string.Empty });
            rows.Add(new[] { "Siembra (UTC)", DateOnlyIso(s.SowingDate), string.Empty });
            rows.Add(new[] { "Superficie (m2)", s.AreaM2.ToString(Inv), string.Empty });
            rows.Add(new[] { "GDD acumulado", s.GddAccumulated.ToString(Inv), string.Empty });
            rows.Add(new[] { "Etapa fenologica", s.PhenologicalStageName ?? string.Empty, string.Empty });
            rows.Add(new[] { "Etapa comercial predominante", s.CommercialStageName ?? "(sin plantas activas)",
                s.IsCommercialStageMixed ? "empate de conteos -> Mixto (regla de dominio)" : string.Empty });
            rows.Add(new[] { "pH actual", s.CurrentPh?.ToString(Inv) ?? string.Empty, string.Empty });
            rows.Add(new[] { "EC actual", s.CurrentEc?.ToString(Inv) ?? string.Empty,
                s.EcObjective is { } ec ? $"EC objetivo: {ec.ToString(Inv)}" : string.Empty });
            rows.Add(new[] { "Plantas activas", s.ActivePlants.ToString(Inv), string.Empty });
            rows.Add(new[] { "Plantas cosechadas", s.HarvestedPlants.ToString(Inv), string.Empty });
            rows.Add(new[] { "Plantas descartadas", s.DiscardedPlants.ToString(Inv), string.Empty });
            rows.Add(new[] { "Posiciones vacias", s.EmptyPositions.ToString(Inv),
                "una posicion vacia NO es una planta descartada" });
            rows.Add(new[] { "Posiciones configuradas", s.TotalConfiguredPositions.ToString(Inv), string.Empty });
            foreach (var c in s.CommercialCounts)
                rows.Add(new[] { $"Conteo por estado: {c.StageName}", c.Count.ToString(Inv), string.Empty });
            if (s.HarvestReadiness is { } h)
            {
                rows.Add(new[] { "Readiness Baby Leaf",
                    h.IsReady == true ? "listo" : h.IsReady == false ? "no listo" : "pendiente (objetivo sin definir)",
                    $"aptas {h.AptaPlantCount}/{h.TotalActivePlants} ({h.AptaPercent.ToString(Inv)}%), objetivo {h.TargetPercent?.ToString(Inv)}%" });
                foreach (var w in h.Warnings)
                    rows.Add(new[] { "Readiness advertencia", string.Empty, w });
            }
        }
        else
        {
            rows.Add(new[] { "Sin datos", "Lote no encontrado", string.Empty });
        }
        return Build("hydropilot-informe-estado-lote", 0, rows, headers);
    }

    // ------------------------------------------------------------------
    // Plantas
    // ------------------------------------------------------------------

    public static CsvExportResult Plants(PlantReportResult result)
    {
        var headers = new[]
        {
            "PlantaId", "LoteId", "Lote", "Fila", "Columna", "Estado operativo",
            "Etapa fenologica", "Etapa comercial", "BabyLeafScore", "GrowthRate",
            "Resultado ultima evaluacion", "Ultima evaluacion (UTC)", "Tiene imagen",
            "Cosecha (UTC)", "Descarte (UTC)", "Motivo descarte",
        };
        var rows = result.Rows.Select(p => new[]
        {
            p.PlantId.ToString(Inv),
            p.LotId.ToString(Inv),
            p.LotName ?? string.Empty,
            p.Row.ToString(Inv),
            p.Column.ToString(Inv),
            p.OperationalState.ToString(),
            p.PhenologicalStageName ?? string.Empty,
            p.CommercialStageName ?? string.Empty,
            p.BabyLeafScore?.ToString(Inv) ?? string.Empty,
            p.GrowthRate?.ToString(Inv) ?? string.Empty,
            p.LastEvaluationResult ?? string.Empty,
            p.LastEvaluationAtUtc is { } le ? Iso(le) : string.Empty,
            p.HasImage ? "si" : "no",
            p.HarvestDate is { } hd ? DateOnlyIso(hd) : string.Empty,
            p.DiscardDate is { } dd ? DateOnlyIso(dd) : string.Empty,
            p.DiscardReason ?? string.Empty,
        });
        return Build("hydropilot-informe-plantas", result.Page, rows, headers);
    }

    // ------------------------------------------------------------------
    // Cambios de estado
    // ------------------------------------------------------------------

    public static CsvExportResult StageChanges(StageChangeReportResult result)
    {
        var headers = new[]
        {
            "HistorialId", "PlantaId", "LoteId", "Lote", "Fila", "Columna",
            "Etapa anterior", "Etapa nueva", "Estado operativo anterior", "Estado operativo nuevo",
            "Origen", "Motivo", "Cambio (UTC)",
        };
        var rows = result.Rows.Select(h => new[]
        {
            h.HistoryId.ToString(Inv),
            h.PlantId.ToString(Inv),
            h.LotId.ToString(Inv),
            h.LotName ?? string.Empty,
            h.Row.ToString(Inv),
            h.Column.ToString(Inv),
            h.PreviousCommercialStage ?? string.Empty,
            h.NewCommercialStage ?? string.Empty,
            h.PreviousOperationalState ?? string.Empty,
            h.NewOperationalState ?? string.Empty,
            h.Source,
            h.Reason ?? string.Empty,
            Iso(h.ChangedAtUtc),
        });
        return Build("hydropilot-informe-cambios-estado", 0, rows, headers);
    }

    // ------------------------------------------------------------------
    // Construcción
    // ------------------------------------------------------------------

    private static CsvExportResult Build(string baseName, int page, IEnumerable<string[]> rows, string[] headers)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(Escape)));
        foreach (var row in rows)
            sb.AppendLine(string.Join(",", row.Select(Escape)));

        // BOM UTF-8 explícito: Encoding.GetBytes no emite el preámbulo; se busca
        // compatibilidad con Excel (REP-04).
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var content = utf8.GetBytes(sb.ToString());
        var bytes = new byte[utf8.GetPreamble().Length + content.Length];
        Buffer.BlockCopy(utf8.GetPreamble(), 0, bytes, 0, utf8.GetPreamble().Length);
        Buffer.BlockCopy(content, 0, bytes, utf8.GetPreamble().Length, content.Length);

        var fileName = $"{baseName}{(page > 0 ? $"-pagina-{page:000}" : string.Empty)}-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv";
        return new CsvExportResult(fileName, bytes);
    }

    private static string Escape(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) >= 0)
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv);

    private static string DateOnlyIso(DateOnly d) => d.ToString("yyyy-MM-dd", Inv);
}