using System.Globalization;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Control;
using HydroPilotWeb.Models.Notifications;

namespace HydroPilotWeb.Services.SimulationEnvironment.Dataset;

public sealed record DemoDatasetReadResult(DemoDataset? Dataset, IReadOnlyList<string> Errors);

/// <summary>
/// Lee el Excel del dataset de la demo. Las columnas se buscan por su título (y en Lotes por el grupo de arriba), no por
/// su posición: agregar columnas o filas a la planilla no rompe la lectura. Lo calculado (gris) no se lee: se vuelve a
/// controlar en <see cref="DemoDatasetPlanner"/>.
/// </summary>
public static class DemoDatasetReader
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-AR");

    public static readonly IReadOnlyList<string> Roles = ["Administrador", "Productor", "Operador", "Soporte Técnico"];

    /// <summary>Incidentes de la hoja Listas → los del generador.</summary>
    internal static readonly IReadOnlyDictionary<string, Incident> Incidents = new Dictionary<string, Incident>(StringComparer.OrdinalIgnoreCase)
    {
        ["Ola de calor y Pythium"] = Incident.Pythium,
        ["Calor con CE alta"] = Incident.HeatTipburn,
        ["Pi sin conexión"] = Incident.PiOffline,
        ["Sonda de pH descalibrada"] = Incident.PhProbeDrift,
        ["Bomba de Bajar pH tapada"] = Incident.PhDownClogged,
        ["Bidón de nutrientes vacío"] = Incident.NutrientEmpty,
    };

    public static DemoDatasetReadResult Read(Stream stream)
    {
        XlsxWorkbook workbook;
        try
        {
            workbook = XlsxWorkbook.Open(stream);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            return new(null, ["No se pudo leer el archivo: tiene que ser el Excel del dataset (.xlsx)."]);
        }

        var errors = new List<string>();
        var general = workbook.Sheet("General") is { } g ? ReadGeneral(g, errors) : null;
        if (general is null && !errors.Any())
            errors.Add("Falta la hoja General.");
        var circuits = workbook.Sheet("Circuitos") is { } c ? ReadCircuits(c, errors) : Missing<DemoCircuit>("Circuitos", errors);
        var lots = workbook.Sheet("Lotes") is { } l ? ReadLots(l, errors) : Missing<DemoLot>("Lotes", errors);
        var maps = workbook.Sheet("Mapas") is { } m ? ReadMaps(m, errors) : [];
        var prices = workbook.Sheet("Precios") is { } p ? ReadPrices(p, errors) : [];
        var climate = workbook.Sheet("Clima") is { } k ? ReadClimate(k, errors) : DemoClimate.Default;
        var users = workbook.Sheet("Usuarios") is { } u ? ReadUsers(u, errors) : [];

        return errors.Count > 0 || general is null
            ? new(null, errors)
            : new(new DemoDataset(general, circuits, lots, maps, prices, climate, users), []);
    }

    private static List<T> Missing<T>(string sheet, List<string> errors)
    {
        errors.Add($"Falta la hoja {sheet}.");
        return [];
    }

    // ------------------------------------------------------------------
    // General: etiqueta en la columna A, valor en la B
    // ------------------------------------------------------------------

    private static DemoGeneral? ReadGeneral(XlsxSheet sheet, List<string> errors)
    {
        var values = new Dictionary<string, XlsxCell?>(StringComparer.OrdinalIgnoreCase);
        for (var r = 1; r <= sheet.LastRow; r++)
        {
            if (sheet.Text(r, 1) is { } label)
                values.TryAdd(label, sheet[r, 2]);
        }
        XlsxCell? Value(string label) => values.GetValueOrDefault(label);
        var where = "General";

        var today = DateValue(Value("Hoy del dataset"));
        if (today is null)
        {
            errors.Add("General: falta \"Hoy del dataset\" (una fecha).");
            return null;
        }
        var shift = Value("Correr las fechas si se carga otro día")?.Text.Trim();
        var defaults = TransplantParameters.Defaults;
        var checks = new Dictionary<string, int>();
        foreach (var (type, days) in ParameterDefaults.SensorCheckDays)
            checks[type] = PositiveInt(Value(type), $"{where}: chequeo de {type}", errors) ?? days;

        return new DemoGeneral(
            today.Value,
            shift is null || !shift.StartsWith("N", StringComparison.OrdinalIgnoreCase),
            DateValue(Value("Desde cuándo el control es automático")),
            Value("Invernadero")?.Text.Trim() is { Length: > 0 } name ? name : null,
            Value("Moneda")?.Text.Trim() is { Length: > 0 } currency ? currency.ToUpperInvariant() : "ARS",
            Positive(Value("Peso por planta Baby Leaf (g)"), $"{where}: peso Baby Leaf", errors) ?? ParameterDefaults.BabyLeafGramsPerPlant,
            Positive(Value("Peso por lechuga adulta (g)"), $"{where}: peso adulta", errors) ?? ParameterDefaults.ConventionalGramsPerPlant,
            Positive(Value("Solución A, macro ($ por litro)"), $"{where}: Solución A", errors) ?? ParameterDefaults.SolutionAPricePerLiter,
            Positive(Value("Solución B, micro ($ por litro)"), $"{where}: Solución B", errors) ?? ParameterDefaults.SolutionBPricePerLiter,
            Positive(Value("GDD de decisión"), $"{where}: GDD de decisión", errors) ?? defaults.DecisionGdd,
            NonNegativeInt(Value("Días de estrés"), $"{where}: días de estrés", errors) ?? defaults.StressDays,
            Positive(Value("Tamaño máximo Baby Leaf (× el ideal)"), $"{where}: tamaño máximo", errors) ?? defaults.MaxSizeFactor,
            Positive(Value("Separación entre huecos del denso (cm)"), $"{where}: separación del denso", errors) ?? defaults.DenseSpacingCm,
            Positive(Value("Separación entre huecos de Adultas (cm)"), $"{where}: separación de Adultas", errors) ?? defaults.AdultSpacingCm,
            NonNegativeInt(Value("Días de limpieza del denso"), $"{where}: días de limpieza", errors) ?? defaults.DenseCleaningDays,
            checks,
            DateValue(Value("Última calibración de temperatura y humedad")));
    }

    // ------------------------------------------------------------------
    // Tablas con títulos
    // ------------------------------------------------------------------

    /// <summary>Fila de títulos: la primera (de las 12 de arriba) cuya columna A dice <paramref name="first"/>.</summary>
    private static int HeaderRow(XlsxSheet sheet, string first, string? second = null, int from = 1, int to = 12)
    {
        for (var r = from; r <= Math.Min(to, sheet.LastRow); r++)
        {
            if (string.Equals(sheet.Text(r, 1), first, StringComparison.OrdinalIgnoreCase)
                && (second is null || string.Equals(sheet.Text(r, 2), second, StringComparison.OrdinalIgnoreCase)))
                return r;
        }
        return 0;
    }

    /// <summary>Columna cuyo título empieza con <paramref name="title"/> (0 si no está).</summary>
    private static int Column(XlsxSheet sheet, int headerRow, string title)
    {
        for (var c = 1; c <= sheet.LastColumn; c++)
        {
            if (sheet.Text(headerRow, c) is { } text && text.StartsWith(title, StringComparison.OrdinalIgnoreCase))
                return c;
        }
        return 0;
    }

    private static Dictionary<string, int> Columns(XlsxSheet sheet, int headerRow, string where, List<string> errors, params string[] titles)
    {
        var columns = new Dictionary<string, int>();
        foreach (var title in titles)
        {
            var c = Column(sheet, headerRow, title);
            if (c == 0)
                errors.Add($"{where}: falta la columna \"{title}\".");
            columns[title] = c;
        }
        return columns;
    }

    private static List<DemoCircuit> ReadCircuits(XlsxSheet sheet, List<string> errors)
    {
        var header = HeaderRow(sheet, "Nombre");
        if (header == 0)
        {
            errors.Add("Circuitos: no encuentro la fila de títulos (Nombre, Tipo, …).");
            return [];
        }
        var col = Columns(sheet, header, "Circuitos", errors, "Nombre", "Tipo", "Filas", "Columnas", "Litros", "CE del agua",
            "Modo", "Última calibración pH", "Última calibración CE");
        if (col.ContainsValue(0))
            return [];

        var circuits = new List<DemoCircuit>();
        for (var r = header + 1; r <= sheet.LastRow; r++)
        {
            if (sheet.Text(r, col["Nombre"]) is not { } name)
                continue;
            var where = $"Circuitos, fila {r}";
            var kind = sheet.Text(r, col["Tipo"]) switch
            {
                { } t when t.StartsWith("Den", StringComparison.OrdinalIgnoreCase) => CircuitKinds.Dense,
                { } t when t.StartsWith("Adu", StringComparison.OrdinalIgnoreCase) => CircuitKinds.Adult,
                _ => null,
            };
            if (kind is null)
                errors.Add($"{where}: el tipo tiene que ser Denso o Adultas.");
            var rows = PositiveInt(sheet[r, col["Filas"]], $"{where}: filas", errors);
            var columns = PositiveInt(sheet[r, col["Columnas"]], $"{where}: columnas", errors);
            if (rows is null or > Circuit.MaxGridSide || columns is null or > Circuit.MaxGridSide)
                errors.Add($"{where}: filas y columnas de 1 a {Circuit.MaxGridSide}.");
            var mode = sheet.Text(r, col["Modo"]) switch
            {
                { } m when m.StartsWith("Auto", StringComparison.OrdinalIgnoreCase) => ControlContract.ModeAuto,
                { } m when m.StartsWith("Man", StringComparison.OrdinalIgnoreCase) => ControlContract.ModeManual,
                _ => null,
            };
            circuits.Add(new DemoCircuit(r, name, kind ?? CircuitKinds.Dense, rows ?? 1, columns ?? 1,
                Positive(sheet[r, col["Litros"]], $"{where}: litros", errors) ?? ControlContract.DefaultReservoirLiters,
                NonNegative(sheet[r, col["CE del agua"]], $"{where}: CE del agua", errors) ?? ControlContract.DefaultSourceWaterEc,
                mode,
                DateValue(sheet[r, col["Última calibración pH"]], $"{where}: calibración de pH", errors),
                DateValue(sheet[r, col["Última calibración CE"]], $"{where}: calibración de CE", errors)));
        }
        return circuits;
    }

    // ------------------------------------------------------------------
    // Lotes: grupo (fila 1, celdas combinadas) + título (fila 2); los títulos repetidos del grupo llevan "#2"
    // ------------------------------------------------------------------

    private static List<DemoLot> ReadLots(XlsxSheet sheet, List<string> errors)
    {
        var keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string? group = null;
        for (var c = 1; c <= sheet.LastColumn; c++)
        {
            group = sheet.Text(1, c) ?? group;
            if (sheet.Text(2, c) is not { } title)
                continue;
            var key = $"{group}|{title}";
            var n = 1;
            while (keys.ContainsKey(n == 1 ? key : $"{key}#{n}"))
                n++;
            keys[n == 1 ? key : $"{key}#{n}"] = c;
        }
        int Col(string key)
        {
            if (keys.TryGetValue(key, out var c))
                return c;
            errors.Add($"Lotes: falta la columna \"{key.Replace("|", " / ").Replace("#2", " (la segunda)")}\".");
            return 0;
        }

        var name = Col("Lote|Lote");
        var circuit = Col("Siembra y metas|Circuito denso");
        var sowing = Col("Siembra y metas|Siembra");
        var plants = Col("Siembra y metas|Plantas sembradas");
        var blGoal = Col("Siembra y metas|Meta Baby Leaf");
        var activeGoal = Col("Siembra y metas|Meta activas");
        var trDate = Col("Trasplante a Adultas|Fecha");
        var trPlants = Col("Trasplante a Adultas|Plantas");
        var trCircuits = Col("Trasplante a Adultas|Circuitos de Adultas");
        var c1Date = Col("Cortes Baby Leaf|Fecha 1er corte");
        var c1Plants = Col("Cortes Baby Leaf|Plantas");
        var c2Date = Col("Cortes Baby Leaf|Fecha 2do corte");
        var c2Plants = Col("Cortes Baby Leaf|Plantas#2");
        var adDate = Col("Corte de adultas|Fecha");
        var adPlants = Col("Corte de adultas|Plantas");
        var ddPlants = Col("Descartes|Plantas en el denso");
        var ddDate = Col("Descartes|Fecha");
        var ddReason = Col("Descartes|Motivo");
        var daPlants = Col("Descartes|Plantas en Adultas");
        var daDate = Col("Descartes|Fecha#2");
        var daReason = Col("Descartes|Motivo#2");
        var incident = Col("Otros|Incidente");
        if (new[] { name, circuit, sowing, plants, blGoal, activeGoal, trDate, trPlants, trCircuits, c1Date, c1Plants, c2Date, c2Plants,
                adDate, adPlants, ddPlants, ddDate, ddReason, daPlants, daDate, daReason, incident }.Contains(0))
            return [];

        var lots = new List<DemoLot>();
        for (var r = 3; r <= sheet.LastRow; r++)
        {
            if (sheet.Text(r, name) is not { } lotName)
                continue;
            var where = $"Lotes, fila {r} ({lotName})";
            var sown = DateValue(sheet[r, sowing], $"{where}: siembra", errors);
            if (sown is null)
                errors.Add($"{where}: falta la siembra.");
            DemoEvent? Event(int dateCol, int plantsCol, string label)
            {
                var date = DateValue(sheet[r, dateCol], $"{where}: fecha de {label}", errors);
                var count = NonNegativeInt(sheet[r, plantsCol], $"{where}: plantas de {label}", errors);
                if (date is null && count is null or 0)
                    return null;
                if (date is null || count is null or 0)
                {
                    errors.Add($"{where}: {label}, falta la fecha o las plantas.");
                    return null;
                }
                return new DemoEvent(date.Value, count.Value);
            }
            var incidentText = sheet.Text(r, incident);
            if (incidentText is not null && !incidentText.Equals("Ninguno", StringComparison.OrdinalIgnoreCase)
                && !Incidents.ContainsKey(incidentText))
                errors.Add($"{where}: el incidente \"{incidentText}\" no está en la lista (hoja Listas).");

            lots.Add(new DemoLot(
                r,
                lotName,
                sheet.Text(r, circuit) ?? string.Empty,
                sown ?? default,
                NonNegativeInt(sheet[r, plants], $"{where}: plantas sembradas", errors),
                Percent(sheet[r, blGoal], $"{where}: meta Baby Leaf", errors),
                Percent(sheet[r, activeGoal], $"{where}: meta activas", errors),
                Event(trDate, trPlants, "trasplante"),
                (sheet.Text(r, trCircuits) ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Event(c1Date, c1Plants, "1er corte"),
                Event(c2Date, c2Plants, "2do corte"),
                Event(adDate, adPlants, "corte de adultas"),
                Event(ddDate, ddPlants, "descarte en el denso"),
                sheet.Text(r, ddReason),
                Event(daDate, daPlants, "descarte en Adultas"),
                sheet.Text(r, daReason),
                incidentText is null || incidentText.Equals("Ninguno", StringComparison.OrdinalIgnoreCase) ? null : incidentText));
        }
        return lots;
    }

    // ------------------------------------------------------------------
    // Mapas: un bloque por cada "Lote" de la columna A; números de columna abajo y números de fila a la izquierda
    // ------------------------------------------------------------------

    private static List<DemoMap> ReadMaps(XlsxSheet sheet, List<string> errors)
    {
        var maps = new List<DemoMap>();
        for (var r = 1; r <= sheet.LastRow; r++)
        {
            if (!string.Equals(sheet.Text(r, 1), "Lote", StringComparison.OrdinalIgnoreCase))
                continue;
            var columns = new Dictionary<int, int>();
            for (var c = 2; c <= sheet.LastColumn && Integer(sheet[r + 1, c]) is { } n; c++)
                columns[c] = n;
            var codes = new Dictionary<(int, int), string>();
            var row = r + 2;
            for (; row <= sheet.LastRow && Integer(sheet[row, 1]) is { } gridRow; row++)
            {
                foreach (var (c, gridColumn) in columns)
                {
                    if (sheet.Text(row, c) is not { } text)
                        continue;
                    var code = text.ToUpperInvariant();
                    if (!DemoMapCodes.All.Contains(code))
                        errors.Add($"Mapas, fila {row}: el código \"{text}\" no está en la lista (hoja Listas).");
                    codes[(gridRow, gridColumn)] = code;
                }
            }
            var lot = sheet.Text(r, 2);
            if (lot is null)
            {
                if (codes.Count > 0)
                    errors.Add($"Mapas, fila {r}: hay un mapa cargado sin el nombre del lote.");
                continue;
            }
            maps.Add(new DemoMap(r, lot, codes));
            r = row - 1;
        }
        return maps;
    }

    private static List<DemoPrice> ReadPrices(XlsxSheet sheet, List<string> errors)
    {
        var header = HeaderRow(sheet, "Desde");
        if (header == 0)
            return [];
        var col = Columns(sheet, header, "Precios", errors, "Desde", "Baby Leaf", "Adulta");
        if (col.ContainsValue(0))
            return [];
        var prices = new List<DemoPrice>();
        for (var r = header + 1; r <= sheet.LastRow; r++)
        {
            var where = $"Precios, fila {r}";
            if (DateValue(sheet[r, col["Desde"]], $"{where}: desde", errors) is not { } from)
                continue;
            var bl = Positive(sheet[r, col["Baby Leaf"]], $"{where}: precio Baby Leaf", errors);
            var adult = Positive(sheet[r, col["Adulta"]], $"{where}: precio adulta", errors);
            if (bl is null || adult is null)
            {
                errors.Add($"{where}: faltan los dos precios.");
                continue;
            }
            prices.Add(new DemoPrice(r, from, bl.Value, adult.Value));
        }
        return prices.OrderBy(p => p.From).ToList();
    }

    private static DemoClimate ReadClimate(XlsxSheet sheet, List<string> errors)
    {
        var defaults = DemoClimate.Default;
        var max = defaults.OutdoorMax.ToArray();
        var min = defaults.OutdoorMin.ToArray();
        var months = HeaderRow(sheet, "Mes");
        if (months > 0)
        {
            var maxCol = Column(sheet, months, "Máx. afuera");
            var minCol = Column(sheet, months, "Mín. afuera");
            for (var i = 0; i < 12 && maxCol > 0 && minCol > 0; i++)
            {
                var where = $"Clima, fila {months + 1 + i}";
                max[i] = (double?)Number(sheet[months + 1 + i, maxCol], $"{where}: máxima", errors) ?? max[i];
                min[i] = (double?)Number(sheet[months + 1 + i, minCol], $"{where}: mínima", errors) ?? min[i];
            }
        }

        var events = new List<DemoClimateEvent>();
        var header = HeaderRow(sheet, "Desde", "Hasta", to: sheet.LastRow);
        if (header > 0)
        {
            var col = Columns(sheet, header, "Clima", errors, "Desde", "Hasta", "Tipo", "°C");
            for (var r = header + 1; r <= sheet.LastRow && !col.ContainsValue(0); r++)
            {
                var where = $"Clima, fila {r}";
                if (DateValue(sheet[r, col["Desde"]], $"{where}: desde", errors) is not { } from)
                    continue;
                var to = DateValue(sheet[r, col["Hasta"]], $"{where}: hasta", errors);
                var delta = Number(sheet[r, col["°C"]], $"{where}: °C", errors);
                if (to is null || delta is null || to < from)
                {
                    errors.Add($"{where}: el evento necesita desde, hasta (después) y los °C.");
                    continue;
                }
                events.Add(new DemoClimateEvent(from, to.Value, sheet.Text(r, col["Tipo"]) ?? (delta > 0 ? "Ola de calor" : "Frente frío"),
                    (double)delta.Value));
            }
        }
        return new DemoClimate(max, min, events);
    }

    private static List<DemoUser> ReadUsers(XlsxSheet sheet, List<string> errors)
    {
        var header = HeaderRow(sheet, "Nombre");
        if (header == 0)
            return [];
        var col = Columns(sheet, header, "Usuarios", errors, "Nombre", "Apellido", "Correo", "Rol", "Recibe correos", "Desde qué gravedad");
        if (col.ContainsValue(0))
            return [];
        var users = new List<DemoUser>();
        for (var r = header + 1; r <= sheet.LastRow; r++)
        {
            if (sheet.Text(r, col["Correo"]) is not { } email)
                continue;
            var where = $"Usuarios, fila {r}";
            if (!email.Contains('@'))
                errors.Add($"{where}: el correo no es válido.");
            var role = sheet.Text(r, col["Rol"]);
            var knownRole = Roles.FirstOrDefault(x => x.Equals(role, StringComparison.OrdinalIgnoreCase));
            if (knownRole is null)
                errors.Add($"{where}: el rol tiene que ser {string.Join(", ", Roles)}.");
            var severity = sheet.Text(r, col["Desde qué gravedad"]) switch
            {
                { } s when s.StartsWith("Cr", StringComparison.OrdinalIgnoreCase) => NotificationSeverities.Critica,
                { } s when s.StartsWith("Me", StringComparison.OrdinalIgnoreCase) => NotificationSeverities.Media,
                _ => NotificationSeverities.Alta,
            };
            users.Add(new DemoUser(r, sheet.Text(r, col["Nombre"]) ?? email, sheet.Text(r, col["Apellido"]) ?? string.Empty, email,
                knownRole ?? "Operador", sheet.Text(r, col["Recibe correos"])?.StartsWith("S", StringComparison.OrdinalIgnoreCase) == true,
                severity));
        }
        return users;
    }

    // ------------------------------------------------------------------
    // Valores
    // ------------------------------------------------------------------

    private static int? Integer(XlsxCell? cell) =>
        cell?.Number is { } n && Math.Abs(n - Math.Round(n)) < 1e-9 ? (int)Math.Round(n) : null;

    private static decimal? Number(XlsxCell? cell, string label, List<string> errors)
    {
        if (cell is null)
            return null;
        if (cell.Number is { } n)
            return (decimal)n;
        if (decimal.TryParse(cell.Text, NumberStyles.Number, Es, out var parsed) || decimal.TryParse(cell.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed))
            return parsed;
        errors.Add($"{label}: \"{cell.Text}\" no es un número.");
        return null;
    }

    private static decimal? Positive(XlsxCell? cell, string label, List<string> errors)
    {
        var value = Number(cell, label, errors);
        if (value is <= 0)
        {
            errors.Add($"{label}: tiene que ser mayor que 0.");
            return null;
        }
        return value;
    }

    private static decimal? NonNegative(XlsxCell? cell, string label, List<string> errors)
    {
        var value = Number(cell, label, errors);
        if (value is < 0)
        {
            errors.Add($"{label}: no puede ser negativo.");
            return null;
        }
        return value;
    }

    private static int? NonNegativeInt(XlsxCell? cell, string label, List<string> errors)
    {
        var value = NonNegative(cell, label, errors);
        if (value is null)
            return null;
        if (value != Math.Round(value.Value))
        {
            errors.Add($"{label}: tiene que ser un número entero.");
            return null;
        }
        return (int)value.Value;
    }

    private static int? PositiveInt(XlsxCell? cell, string label, List<string> errors)
    {
        var value = NonNegativeInt(cell, label, errors);
        if (value == 0)
        {
            errors.Add($"{label}: tiene que ser mayor que 0.");
            return null;
        }
        return value;
    }

    /// <summary>Porcentaje 0–100 (en la planilla va como 70 % = 0,7; si alguien escribió 70 sin %, también vale).</summary>
    private static decimal? Percent(XlsxCell? cell, string label, List<string> errors)
    {
        var value = Number(cell, label, errors);
        if (value is null)
            return null;
        var percent = value <= 1 ? value.Value * 100 : value.Value;
        if (percent is < 0 or > 100)
        {
            errors.Add($"{label}: tiene que estar entre 0 % y 100 %.");
            return null;
        }
        return Math.Round(percent, 2);
    }

    private static DateOnly? DateValue(XlsxCell? cell, string? label = null, List<string>? errors = null)
    {
        if (cell is null)
            return null;
        if (cell.Number is { } serial && serial is > 0 and < 2_958_466)
            return DateOnly.FromDateTime(DateTime.FromOADate(serial));
        if (DateOnly.TryParseExact(cell.Text.Trim(), ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        if (label is not null)
            errors?.Add($"{label}: \"{cell.Text}\" no es una fecha.");
        return null;
    }
}
