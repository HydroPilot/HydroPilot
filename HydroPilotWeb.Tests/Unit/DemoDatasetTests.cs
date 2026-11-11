using System.IO.Compression;
using System.Security;
using System.Text;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Notifications;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.SimulationEnvironment;
using HydroPilotWeb.Services.SimulationEnvironment.Dataset;

namespace HydroPilotWeb.Tests.Unit;

/// <summary>Un .xlsx mínimo para las pruebas: hojas con celdas de texto (en línea) o número.</summary>
internal static class TestXlsx
{
    public static MemoryStream Build(Dictionary<string, Dictionary<string, object>> sheets)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Write(string path, string content)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
            var names = sheets.Keys.ToList();
            Write("xl/workbook.xml",
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" +
                string.Concat(names.Select((n, i) => $"<sheet name=\"{SecurityElement.Escape(n)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) +
                "</sheets></workbook>");
            Write("xl/_rels/workbook.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                string.Concat(names.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>")) +
                "</Relationships>");
            for (var i = 0; i < names.Count; i++)
            {
                var cells = sheets[names[i]].Select(c => c.Value switch
                {
                    string text => $"<c r=\"{c.Key}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(text)}</t></is></c>",
                    DateOnly date => $"<c r=\"{c.Key}\"><v>{date.ToDateTime(TimeOnly.MinValue).ToOADate()}</v></c>",
                    var number => $"<c r=\"{c.Key}\"><v>{Convert.ToDouble(number, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture)}</v></c>",
                });
                Write($"xl/worksheets/sheet{i + 1}.xml",
                    "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row>" +
                    string.Concat(cells) + "</row></sheetData></worksheet>");
            }
        }
        stream.Position = 0;
        return stream;
    }
}

/// <summary>Datasets de prueba con los circuitos del mundo de prueba (Circuito 1 y 2 densos de 10 × 20, Adultas 1 a 5 de 4 × 5).</summary>
internal static class DemoDatasets
{
    public static readonly DateOnly Today = DateOnly.FromDateTime(ShortHistory.Now);

    public static DemoGeneral General(DateOnly? today = null) => new(
        today ?? Today, ShiftDates: false, "Invernadero Principal", "ARS",
        30m, 280m, 25_000m, 25_000m, 250m, 3, 1.3m, 6.5m, 22.5m, 2,
        new Dictionary<string, int> { ["pH"] = 14, ["CE"] = 30, ["Temperatura"] = 90, ["Humedad"] = 90 }, null);

    public static List<DemoCircuit> Circuits() =>
    [
        new(4, "Circuito 1", CircuitKinds.Dense, 10, 20, 500m, 0.2m, null, null, null),
        new(5, "Circuito 2", CircuitKinds.Dense, 10, 20, 500m, 0.2m, null, null, null),
        .. Enumerable.Range(1, 5).Select(i => new DemoCircuit(5 + i, $"Adultas {i}", CircuitKinds.Adult, 4, 5, 500m, 0.2m, null, null, null)),
    ];

    /// <summary>El primer día (desde la siembra) en que el GDD estimado llega a <paramref name="gdd"/>.</summary>
    public static DateOnly DayAt(DateOnly sowing, decimal gdd)
    {
        var day = sowing.AddDays(1);
        while (DemoClimateModel.Gdd(DemoClimate.Default, sowing, day) < gdd)
            day = day.AddDays(1);
        return day;
    }

    public static DemoLot Lot(int row, string name, string circuit, DateOnly sowing, int? plants = null,
        DemoEvent? transplant = null, IReadOnlyList<string>? adults = null, DemoEvent? cut1 = null, DemoEvent? cut2 = null,
        DemoEvent? adultCut = null, DemoEvent? denseDiscard = null, string? denseReason = null, DemoEvent? adultDiscard = null,
        string? incident = null) =>
        new(row, name, circuit, sowing, plants, 70m, 90m, transplant, adults ?? [], cut1, cut2, adultCut, denseDiscard, denseReason,
            adultDiscard, null, incident);

    public static DemoDataset Dataset(IReadOnlyList<DemoLot> lots, IReadOnlyList<DemoMap>? maps = null, IReadOnlyList<DemoPrice>? prices = null) =>
        new(General(), Circuits(), lots, maps ?? [], prices ?? [], DemoClimate.Default, []);

    /// <summary>
    /// Un lote cerrado en el Circuito 1 (trasplanta 40, corta Baby Leaf en dos veces, descarta 20 y corta las adultas) y,
    /// después, uno en curso en el mismo circuito con su mapa: 40 trasplantadas activas, 60 Baby Leaf, 50 candidatas y 50
    /// en desarrollo.
    /// </summary>
    public static (DemoDataset Dataset, DemoLot Closed, DemoLot Open) ClosedAndOpen()
    {
        var sowing = Today.AddDays(-75);
        // Cortes en la segunda mitad de la ventana Baby Leaf (lo normal), con margen dentro de cada ventana.
        var transplant = DayAt(sowing, 275);
        var cut1 = DayAt(sowing, 365);
        var cut2 = DayAt(sowing, 400);
        var closed = Lot(3, "Lote A01", "Circuito 1", sowing, transplant: new DemoEvent(transplant, 40),
            cut1: new DemoEvent(cut1, 110), cut2: new DemoEvent(cut2, 30), denseDiscard: new DemoEvent(cut2, 20),
            adultCut: new DemoEvent(DayAt(sowing, 700), 37), adultDiscard: new DemoEvent(DayAt(sowing, 700), 3));

        // El en curso: sembrado cuando el Circuito 1 quedó libre, hoy a ~320 GDD (en la ventana Baby Leaf).
        var openSowing = Today.AddDays(-1);
        while (DemoClimateModel.Gdd(DemoClimate.Default, openSowing, Today) < 320)
            openSowing = openSowing.AddDays(-1);
        var open = Lot(4, "Lote A02", "Circuito 1", openSowing, transplant: new DemoEvent(DayAt(openSowing, 275), 40));
        var codes = new Dictionary<(int, int), string>();
        var n = 0;
        foreach (var (code, count) in new[] { (DemoMapCodes.Adult, 40), (DemoMapCodes.BabyLeaf, 60), (DemoMapCodes.Candidate, 50), (DemoMapCodes.Active, 50) })
        {
            for (var i = 0; i < count; i++, n++)
                codes[(n / 20 + 1, n % 20 + 1)] = code;
        }
        return (Dataset([closed, open], [new DemoMap(5, "Lote A02", codes)]), closed, open);
    }
}

/// <summary>Dataset de la demo: lector del Excel, controles y planificación (lógica pura).</summary>
public class DemoDatasetPlannerTests
{
    [Fact]
    public void Lee_el_Excel_por_titulos_de_columna()
    {
        var sowing = new DateOnly(2026, 10, 3);
        using var xlsx = TestXlsx.Build(new()
        {
            ["General"] = new() { ["A5"] = "Hoy del dataset", ["B5"] = new DateOnly(2026, 11, 5), ["A6"] = "Correr las fechas si se carga otro día", ["B6"] = "No",
                ["A12"] = "Peso por planta Baby Leaf (g)", ["B12"] = 30, ["A13"] = "Peso por lechuga adulta (g)", ["B13"] = 200 },
            ["Circuitos"] = new() { ["A3"] = "Nombre", ["B3"] = "Tipo", ["C3"] = "Filas", ["D3"] = "Columnas", ["E3"] = "Huecos", ["F3"] = "Litros del tanque",
                ["G3"] = "CE del agua de red (mS/cm)", ["H3"] = "Modo de control hoy", ["I3"] = "Última calibración pH", ["J3"] = "Última calibración CE",
                ["A4"] = "Circuito 1", ["B4"] = "Denso", ["C4"] = 10, ["D4"] = 20, ["F4"] = 60, ["G4"] = 0.2, ["H4"] = "Automático" },
            ["Lotes"] = LotsSheet(new()
            {
                ["A3"] = "Lote A03", ["D3"] = "Circuito 1", ["E3"] = sowing, ["F3"] = 200, ["G3"] = 0.7, ["H3"] = 0.9,
                ["I3"] = new DateOnly(2026, 10, 18), ["J3"] = 45, ["K3"] = "Adultas 1, Adultas 2",
                ["L3"] = new DateOnly(2026, 10, 22), ["M3"] = 130, ["R3"] = 25, ["S3"] = new DateOnly(2026, 10, 22), ["X3"] = "Ninguno",
            }),
            ["Mapas"] = new() { ["A5"] = "Lote", ["B5"] = "Lote A03", ["B6"] = 1, ["C6"] = 2, ["A7"] = 1, ["B7"] = "c-bl", ["C7"] = "AD", ["A8"] = 2, ["B8"] = "X" },
        });

        var result = DemoDatasetReader.Read(xlsx);

        Assert.Empty(result.Errors);
        var dataset = result.Dataset!;
        Assert.Equal(new DateOnly(2026, 11, 5), dataset.General.Today);
        Assert.False(dataset.General.ShiftDates);
        Assert.Equal(200m, dataset.General.AdultGrams);
        Assert.Equal(60m, dataset.Circuits.Single().Liters);
        var lot = dataset.Lots.Single();
        Assert.Equal(sowing, lot.Sowing);
        Assert.Equal(70m, lot.BabyLeafGoal);
        Assert.Equal(new DemoEvent(new DateOnly(2026, 10, 18), 45), lot.Transplant);
        Assert.Equal(new[] { "Adultas 1", "Adultas 2" }, lot.AdultCircuits);
        Assert.Equal(130, lot.Cut1!.Plants);
        Assert.Null(lot.Cut2);
        Assert.Equal(25, lot.DenseDiscard!.Plants);
        Assert.Null(lot.Incident);
        var map = dataset.Maps.Single();
        Assert.Equal(DemoMapCodes.CutBabyLeaf, map.Codes[(1, 1)]);
        Assert.Equal(DemoMapCodes.DiscardedDense, map.Codes[(2, 1)]);
        Assert.Equal(DemoClimate.Default.OutdoorMax, dataset.Climate.OutdoorMax);
    }

    [Fact]
    public void Una_fecha_sin_plantas_es_un_error_del_Excel()
    {
        using var xlsx = TestXlsx.Build(new()
        {
            ["General"] = new() { ["A5"] = "Hoy del dataset", ["B5"] = new DateOnly(2026, 11, 5) },
            ["Circuitos"] = new() { ["A3"] = "Nombre", ["B3"] = "Tipo", ["C3"] = "Filas", ["D3"] = "Columnas", ["F3"] = "Litros del tanque",
                ["G3"] = "CE del agua de red (mS/cm)", ["H3"] = "Modo de control hoy", ["I3"] = "Última calibración pH", ["J3"] = "Última calibración CE" },
            ["Lotes"] = LotsSheet(new() { ["A3"] = "Lote A03", ["D3"] = "Circuito 1", ["E3"] = new DateOnly(2026, 10, 3), ["L3"] = new DateOnly(2026, 10, 22) }),
        });

        var result = DemoDatasetReader.Read(xlsx);

        Assert.Null(result.Dataset);
        Assert.Contains(result.Errors, e => e.Contains("1er corte, falta la fecha o las plantas"));
    }

    /// <summary>Los dos renglones de títulos de la hoja Lotes (grupos y columnas) más las filas de datos.</summary>
    private static Dictionary<string, object> LotsSheet(Dictionary<string, object> data)
    {
        var sheet = new Dictionary<string, object>
        {
            ["A1"] = "Lote", ["D1"] = "Siembra y metas", ["I1"] = "Trasplante a Adultas", ["L1"] = "Cortes Baby Leaf", ["P1"] = "Corte de adultas",
            ["R1"] = "Descartes", ["X1"] = "Otros", ["Z1"] = "Resultado (se calcula)",
            ["A2"] = "Lote", ["B2"] = "Control", ["C2"] = "Estado", ["D2"] = "Circuito denso", ["E2"] = "Siembra", ["F2"] = "Plantas sembradas",
            ["G2"] = "Meta Baby Leaf", ["H2"] = "Meta activas", ["I2"] = "Fecha", ["J2"] = "Plantas", ["K2"] = "Circuitos de Adultas",
            ["L2"] = "Fecha 1er corte", ["M2"] = "Plantas", ["N2"] = "Fecha 2do corte", ["O2"] = "Plantas", ["P2"] = "Fecha", ["Q2"] = "Plantas",
            ["R2"] = "Plantas en el denso", ["S2"] = "Fecha", ["T2"] = "Motivo", ["U2"] = "Plantas en Adultas", ["V2"] = "Fecha", ["W2"] = "Motivo",
            ["X2"] = "Incidente", ["Y2"] = "Notas", ["Z2"] = "Plantas",
        };
        foreach (var (key, value) in data)
            sheet[key] = value;
        return sheet;
    }

    [Fact]
    public void El_dataset_de_ejemplo_cierra_y_asigna_los_circuitos_de_Adultas()
    {
        var (dataset, closed, open) = DemoDatasets.ClosedAndOpen();

        var plan = DemoDatasetPlanner.Plan(dataset, DemoDatasets.Today);

        Assert.Empty(plan.Errors);
        var scripts = plan.Script!.Lots;
        Assert.Equal(new[] { "Adultas 1", "Adultas 2" }, scripts.Single(s => s.Lot.Row == closed.Row).AdultCircuits); // 40 plantas en circuitos de 20
        // Cuando trasplanta el segundo, las adultas del primero ya salieron: vuelve a empezar por Adultas 1.
        Assert.Equal(new[] { "Adultas 1", "Adultas 2" }, scripts.Single(s => s.Lot.Row == open.Row).AdultCircuits);
    }

    [Fact]
    public void Controla_cuentas_turnos_del_denso_y_lugar_en_Adultas()
    {
        var today = DemoDatasets.Today;
        var sowing = today.AddDays(-60);
        var cut = DemoDatasets.DayAt(sowing, 320);
        var lots = new List<DemoLot>
        {
            // Salen más del denso que las sembradas.
            DemoDatasets.Lot(3, "Cuentas", "Circuito 2", sowing, cut1: new DemoEvent(cut, 210)),
            // Se siembra en el Circuito 1 un día después de que lo libera el anterior (faltan los 2 de limpieza).
            DemoDatasets.Lot(4, "Primero", "Circuito 1", sowing, cut1: new DemoEvent(cut, 200)),
            DemoDatasets.Lot(5, "Segundo", "Circuito 1", cut.AddDays(1), plants: 120,
                transplant: new DemoEvent(DemoDatasets.DayAt(cut.AddDays(1), 258), 120)),
        };

        var plan = DemoDatasetPlanner.Plan(DemoDatasets.Dataset(lots), today);

        Assert.Null(plan.Script);
        Assert.Contains(plan.Errors, e => e.Contains("(Cuentas · ") && e.Contains("salen del denso 210"));
        Assert.Contains(plan.Errors, e => e.Contains("(Segundo · ") && e.Contains("días de limpieza"));
        // 120 trasplantadas y 100 huecos en Adultas (5 circuitos de 20).
        Assert.Contains(plan.Errors, e => e.Contains("(Segundo · ") && e.Contains("hay 100 huecos"));
    }

    [Fact]
    public void Un_corte_fuera_de_la_ventana_y_un_mapa_que_no_coincide_son_errores()
    {
        var (dataset, closed, _) = DemoDatasets.ClosedAndOpen();
        var early = closed with { Cut1 = new DemoEvent(closed.Sowing.AddDays(5), 110) };
        var map = dataset.Maps[0];
        var wrongMap = map with { Codes = map.Codes.Where(c => c.Key != (10, 20)).ToDictionary() };
        dataset = dataset with { Lots = [early, dataset.Lots[1]], Maps = [wrongMap] };

        var plan = DemoDatasetPlanner.Plan(dataset, DemoDatasets.Today);

        Assert.Contains(plan.Errors, e => e.Contains("(Lote A01 · ") && e.Contains("fuera de la ventana"));
        Assert.Contains(plan.Errors, e => e.StartsWith("Mapas") && e.Contains("total de plantas, 199 en el mapa y 200 en Lotes"));
    }

    [Fact]
    public void Corre_las_fechas_para_que_hoy_sea_el_dia_de_la_carga()
    {
        var (dataset, closed, _) = DemoDatasets.ClosedAndOpen();
        dataset = dataset with { General = dataset.General with { ShiftDates = true } };

        var plan = DemoDatasetPlanner.Plan(dataset, DemoDatasets.Today.AddDays(10));

        var moved = plan.Script!.Dataset;
        Assert.Equal(DemoDatasets.Today.AddDays(10), moved.General.Today);
        Assert.Equal(closed.Sowing.AddDays(10), moved.Lots[0].Sowing);
        Assert.Equal(closed.Cut1!.Date.AddDays(10), moved.Lots[0].Cut1!.Date);
        Assert.Contains(plan.Warnings, w => w.Contains("se corrieron 10"));
    }

    [Fact]
    public void Un_incidente_de_calor_sin_ola_de_calor_la_agrega()
    {
        var sowing = DemoDatasets.Today.AddDays(-40);
        var lot = DemoDatasets.Lot(3, "Pythium", "Circuito 1", sowing, denseDiscard: new DemoEvent(DemoDatasets.DayAt(sowing, 300), 200),
            incident: "Ola de calor y Pythium");

        var plan = DemoDatasetPlanner.Plan(DemoDatasets.Dataset([lot]), DemoDatasets.Today);

        Assert.Empty(plan.Errors);
        Assert.Single(plan.Script!.Dataset.Climate.Events);
        Assert.Contains(plan.Warnings, w => w.Contains("ola de calor"));
    }
}

/// <summary>El historial generado con un dataset: sale como dice el Excel.</summary>
public class DemoDatasetHistoryTests
{
    private static readonly Lazy<(DemoScript Script, HistoryData Data)> History = new(() =>
    {
        var (dataset, _, _) = DemoDatasets.ClosedAndOpen();
        var plan = DemoDatasetPlanner.Plan(dataset, DemoDatasets.Today);
        Assert.Empty(plan.Errors);
        var data = HistoryGenerator.Generate(SimulationWorlds.Seeded(), new SimulationEnvironmentOptions(), ShortHistory.Now, script: plan.Script);
        return (plan.Script!, data);
    });

    [Fact]
    public void Cada_planta_termina_donde_dice_el_Excel()
    {
        var (script, data) = History.Value;

        var issues = DemoDatasetVerifier.Verify(script, data, SimulationWorlds.Seeded().CommercialStageIds);

        Assert.Empty(issues);
        var closed = data.Lots.Single(l => l.Name!.StartsWith("Lote A01 · "));
        Assert.Equal(1 + 1, data.Lots.Count);
        Assert.Equal(2, closed.StatusId); // Cosechado
        Assert.Equal(Math.Round((140 * 30m + 37 * 280m) / 1000m, 2), closed.ActualYieldKg);
        Assert.Null(data.Lots.Single(l => l.Name!.StartsWith("Lote A02 · ")).ActualHarvestDate);
    }

    [Fact]
    public void Las_cortadas_como_Baby_Leaf_lo_eran_y_las_trasplantadas_eran_las_de_score_mas_bajo()
    {
        var (_, data) = History.Value;
        var closed = data.Lots.Single(l => l.Name!.StartsWith("Lote A01 · "));
        var plants = data.Plants.Where(p => p.LotId == closed.Id).ToList();
        var transplantDay = plants.First(p => p.TransplantedOn is not null).TransplantedOn!.Value;

        Assert.All(data.ScriptedEvents.Where(e => e.Event.Contains("Baby Leaf")), e => Assert.Equal(0, e.NotReady));

        decimal ScoreOn(Plant plant, DateOnly day) => data.Evaluations
            .Where(e => e.PlantId == plant.Id && DateOnly.FromDateTime(e.EvaluatedAtUtc.AddHours(-3)) == day)
            .Select(e => e.BabyLeafScore).Single();
        var transplanted = plants.Where(p => p.TransplantedOn is not null).Select(p => ScoreOn(p, transplantDay)).ToList();
        var stayed = plants.Where(p => p.TransplantedOn is null && p.OperationalState == PlantOperationalState.Cosechada)
            .Select(p => ScoreOn(p, transplantDay)).ToList();
        Assert.True(transplanted.Max() < stayed.Min(), $"trasplantadas hasta {transplanted.Max()}, las que quedaron desde {stayed.Min()}");
    }

    [Fact]
    public void Los_movimientos_caen_en_su_ventana_de_GDD()
    {
        var (_, data) = History.Value;

        Assert.All(data.ScriptedEvents, e =>
        {
            var (min, max) = e.Event switch
            {
                "Trasplante" => (250m, 450m),
                "1er corte Baby Leaf" or "2do corte Baby Leaf" => (250m, 450m),
                "Corte de adultas" => (650m, 750m),
                _ => (0m, 800m),
            };
            Assert.InRange(e.Gdd, min, max);
        });
        Assert.Contains(data.ScriptedEvents, e => e.Event == "Trasplante" && e.Lot.StartsWith("Lote A02 · ") && e.Plants == 40);
    }

    [Fact]
    public void Con_los_umbrales_de_la_vision_tambien_queda_como_el_Excel()
    {
        // Desde 2026-11-09 la configuración trae candidata 45 y Baby Leaf 70 (antes 60 y 80).
        var (dataset, _, _) = DemoDatasets.ClosedAndOpen();
        var plan = DemoDatasetPlanner.Plan(dataset, DemoDatasets.Today);
        var world = SimulationWorlds.Seeded();
        world = world with { Config = new BabyLeafConfig
        {
            Id = 1, CropTypeId = 1, Name = "Baby Leaf Butterhead v1", GddMin = 250, GddMax = 450, ScoreMinCandidate = 45, ScoreMinReady = 70,
        } };

        var data = HistoryGenerator.Generate(world, new SimulationEnvironmentOptions(), ShortHistory.Now, script: plan.Script);

        Assert.Empty(DemoDatasetVerifier.Verify(plan.Script!, data, world.CommercialStageIds));
        Assert.Equal(plan.Script!.Lots.Single(l => l.Lot.Name.StartsWith("Lote A01 · ")).Lot.Cut1!.Date, AppPeak(data, "Lote A01")!.Date);
    }

    [Fact]
    public void La_cosecha_optima_del_lote_cerrado_es_el_dia_de_su_primer_corte()
    {
        var (script, data) = History.Value;
        var cut1 = script.Lots.Single(l => l.Lot.Name.StartsWith("Lote A01 · ")).Lot.Cut1!;

        var peak = AppPeak(data, "Lote A01")!;

        Assert.Equal(cut1.Date, peak.Date);
        Assert.True(peak.Gdd >= 350, $"pico a los {peak.Gdd} GDD: antes de la mitad de la ventana");
        Assert.InRange(data.Lots.Single(l => l.Name!.StartsWith("Lote A01 · ")).BabyLeafPeakGdd!.Value, 350m, 450m);
    }

    /// <summary>El pico de la curva de aptas como lo arma la app (BabyLeafOptimumService): por día, las evaluadas Baby Leaf.</summary>
    private static AptCurvePoint? AppPeak(HistoryData data, string lotName)
    {
        var lotId = data.Lots.Single(l => l.Name!.StartsWith(lotName + " · ")).Id;
        var plants = data.Plants.Where(p => p.LotId == lotId).Select(p => p.Id).ToHashSet();
        var curve = data.Evaluations.Where(e => plants.Contains(e.PlantId))
            .GroupBy(e => DateOnly.FromDateTime(e.EvaluatedAtUtc))
            .Select(g => new AptCurvePoint(g.Key, Math.Round(g.Average(e => e.GddAtEvaluation), 1),
                g.Count(e => e.Result == CommercialStageNames.BabyLeafApta), g.Count()))
            .OrderBy(p => p.Date)
            .ToList();
        return BabyLeafOptimumRules.Peak(curve);
    }

    [Fact]
    public void El_lote_en_curso_queda_con_los_estados_de_su_mapa()
    {
        var (_, data) = History.Value;
        var open = data.Lots.Single(l => l.Name!.StartsWith("Lote A02 · "));
        var plants = data.Plants.Where(p => p.LotId == open.Id).ToList();
        var stages = SimulationWorlds.Seeded().CommercialStageIds;

        Assert.Equal(60, plants.Count(p => p.TransplantedOn is null && p.CommercialStageId == stages[CommercialStageNames.BabyLeafApta]));
        Assert.Equal(50, plants.Count(p => p.TransplantedOn is null && p.CommercialStageId == stages[CommercialStageNames.CandidataBabyLeaf]));
        Assert.Equal(50, plants.Count(p => p.TransplantedOn is null && p.CommercialStageId == stages[CommercialStageNames.Activa]));
        Assert.Equal(40, plants.Count(p => p.TransplantedOn is not null && p.OperationalState == PlantOperationalState.Activa));
        // Todavía en la ventana Baby Leaf: las trasplantadas son Activa, no Adulta.
        Assert.All(plants.Where(p => p.TransplantedOn is not null), p => Assert.Equal(stages[CommercialStageNames.Activa], p.CommercialStageId));
        Assert.Equal(200, plants.Count);   // una planta por hueco
    }

    [Fact]
    public void Las_trasplantadas_pasan_de_Activa_a_Adulta_de_a_poco_antes_de_su_corte()
    {
        var (script, data) = History.Value;
        var lot = script.Lots.Single(l => l.Lot.Name.StartsWith("Lote A01 · ")).Lot;
        var lotId = data.Lots.Single(l => l.Name!.StartsWith("Lote A01 · ")).Id;
        var stages = SimulationWorlds.Seeded().CommercialStageIds;
        var adults = data.Plants.Where(p => p.LotId == lotId && p.TransplantedOn is not null).Select(p => p.Id).ToHashSet();
        var cut = data.Plants.Where(p => adults.Contains(p.Id) && p.OperationalState == PlantOperationalState.Cosechada).Select(p => p.Id).ToHashSet();

        // Primero Activa al trasplantar; después, en el flujo diario, Adulta, cada una su día (todas antes del corte).
        var becameAdult = data.StageHistories
            .Where(h => cut.Contains(h.PlantId) && h.NewCommercialStageId == stages[CommercialStageNames.CosechaConvencional] && h.Source == "daily-flow")
            .GroupBy(h => h.PlantId)
            .Select(g => DateOnly.FromDateTime(g.Min(h => h.ChangedAtUtc)))
            .ToList();
        Assert.All(data.StageHistories.Where(h => adults.Contains(h.PlantId) && h.Source == "transplant"),
            h => Assert.Equal(stages[CommercialStageNames.Activa], h.NewCommercialStageId));
        var cutAdults = lot.AdultCut!.Plants;
        Assert.Equal(cutAdults, becameAdult.Count);
        Assert.True(becameAdult.Distinct().Count() > 2, "todas pasaron a Adulta el mismo día");
        Assert.All(becameAdult, day => Assert.True(day <= lot.AdultCut.Date));
        Assert.Contains(data.ScriptedEvents, e => e.Event == "Corte de adultas" && e.Plants == cutAdults && e.NotReady == 0);
    }
}

/// <summary>El dataset de la demo del repositorio (HydroPilotWeb/SimulationData): lo usan el arranque y "Regenerar historial".</summary>
public class RepositoryDatasetTests
{
    [Fact]
    public void El_dataset_del_repositorio_se_lee_y_cierra()
    {
        using var file = File.OpenRead(FindRepositoryFile(Path.Combine("HydroPilotWeb", "SimulationData", "dataset-demo.xlsx")));

        var read = DemoDatasetReader.Read(file);

        Assert.Empty(read.Errors);
        var plan = DemoDatasetPlanner.Plan(read.Dataset!, read.Dataset!.General.Today);
        Assert.Empty(plan.Errors);
        Assert.Empty(plan.Warnings);
    }

    /// <summary>
    /// Cargado días después, con las fechas corridas, cierra igual: el clima viaja con las fechas (desde 2026-11-11; antes
    /// a los 6 días algunos trasplantes y cortes quedaban fuera de su ventana de GDD).
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(45)]
    public void El_dataset_del_repositorio_cierra_cargado_otro_dia(int days)
    {
        using var file = File.OpenRead(FindRepositoryFile(Path.Combine("HydroPilotWeb", "SimulationData", "dataset-demo.xlsx")));
        var dataset = DemoDatasetReader.Read(file).Dataset!;

        var plan = DemoDatasetPlanner.Plan(dataset, dataset.General.Today.AddDays(days));

        Assert.Empty(plan.Errors);
        Assert.Equal(days, plan.Script!.Dataset.Climate.ShiftDays);
    }

    private static string FindRepositoryFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException($"No está {relative} en el repositorio.");
    }
}

/// <summary>
/// Un lote en su día de corte (en curso, sin trasplantar, con más de la mitad del denso como Baby Leaf) y dos plantas que
/// la cámara vio con un patógeno: el de la demo (Lote C01 y las de riesgo de A27).
/// </summary>
public class DemoDatasetCutDayTests
{
    private static readonly Lazy<(HistoryData Data, int LotId)> History = new(() =>
    {
        var today = DemoDatasets.Today;
        var sowing = today.AddDays(-1);
        while (DemoClimateModel.Gdd(DemoClimate.Default, sowing, today) < 390)
            sowing = sowing.AddDays(-1);
        var lot = DemoDatasets.Lot(3, "Lote C01", "Circuito 1", sowing);
        var codes = new Dictionary<(int, int), string>();
        var n = 0;
        foreach (var (code, count) in new[] { (DemoMapCodes.BabyLeaf, 150), (DemoMapCodes.Candidate, 20), (DemoMapCodes.Active, 28), (DemoMapCodes.Risk, 2) })
        {
            for (var i = 0; i < count; i++, n++)
                codes[(n / 20 + 1, n % 20 + 1)] = code;
        }
        var plan = DemoDatasetPlanner.Plan(DemoDatasets.Dataset([lot], [new DemoMap(5, "Lote C01", codes)]), today);
        Assert.Empty(plan.Errors);
        var data = HistoryGenerator.Generate(SimulationWorlds.Seeded(), new SimulationEnvironmentOptions(), ShortHistory.Now, script: plan.Script);
        return (data, data.Lots.Single().Id);
    });

    [Fact]
    public void Hoy_el_score_general_deja_de_mejorar_y_es_el_dia_de_cortar()
    {
        var (data, lotId) = History.Value;
        var plants = data.Plants.Where(p => p.LotId == lotId).Select(p => p.Id).ToHashSet();
        // La curva como la arma la app (BabyLeafOptimumService.CurveAsync; acá no hay trasplantadas).
        var curve = data.Evaluations.Where(e => plants.Contains(e.PlantId))
            .GroupBy(e => DateOnly.FromDateTime(e.EvaluatedAtUtc))
            .OrderBy(g => g.Key)
            .Select(g => new AptCurvePoint(g.Key, Math.Round(g.Average(e => e.GddAtEvaluation), 1),
                g.Count(e => e.Result == CommercialStageNames.BabyLeafApta), g.Count(), Math.Round(g.Average(e => e.BabyLeafScore), 2)))
            .ToList();

        Assert.Equal(DemoDatasets.Today, BabyLeafOptimumRules.CutDay(curve)?.Date);
        // La vista calcula el GDD de hoy con las lecturas y puede dar un poco menos que el de la evaluación: igual es hoy.
        var accumulated = curve[^1].Gdd - 0.5m;
        var optimum = BabyLeafOptimumRules.Resolve(curve, 380m, accumulated, 250m, 450m);
        Assert.True(optimum.PeakReached);
        Assert.True(optimum.Gdd <= accumulated);
    }

    [Fact]
    public void Dos_plantas_en_riesgo_por_lo_que_vio_la_camara_sin_poner_en_riesgo_al_lote()
    {
        var (data, lotId) = History.Value;
        var stages = SimulationWorlds.Seeded().CommercialStageIds;
        var active = data.Plants.Where(p => p.LotId == lotId && p.OperationalState == PlantOperationalState.Activa).ToList();

        Assert.Equal(2, active.Count(p => p.CommercialStageId == stages[CommercialStageNames.RiesgoFueraDeVentana]));
        Assert.Equal(150, active.Count(p => p.CommercialStageId == stages[CommercialStageNames.BabyLeafApta]));
        Assert.Equal(2, data.Anomalies.Count(a => a.LotId == lotId && a.Type == "PATOGENO_FOLIAR_VISUAL" && a.Status == AnomalyContract.StatusAbierta));
        Assert.Equal(2, data.Alerts.Count(a => a.LotId == lotId && a.Type == NotificationTypes.AlertaSanitaria));
    }
}
