using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace HydroPilotWeb.Services.SimulationEnvironment.Dataset;

/// <summary>Valor de una celda: el texto que muestra y, si es un número (o una fecha de Excel), el número.</summary>
public sealed record XlsxCell(string Text, double? Number);

/// <summary>Una hoja: sus celdas por (fila, columna), las dos desde 1.</summary>
public sealed class XlsxSheet(string name, Dictionary<(int Row, int Column), XlsxCell> cells)
{
    public string Name { get; } = name;

    public int LastRow { get; } = cells.Count == 0 ? 0 : cells.Keys.Max(k => k.Row);

    public int LastColumn { get; } = cells.Count == 0 ? 0 : cells.Keys.Max(k => k.Column);

    public XlsxCell? this[int row, int column] => cells.GetValueOrDefault((row, column));

    /// <summary>Texto sin espacios de más; null si la celda está vacía.</summary>
    public string? Text(int row, int column) =>
        this[row, column]?.Text.Trim() is { Length: > 0 } text ? text : null;
}

/// <summary>
/// Lector mínimo de .xlsx (Open XML, sin dependencias): el valor de cada celda de cada hoja. En las fórmulas toma el
/// último resultado que guardó Excel, que es lo que se ve en la planilla.
/// </summary>
public sealed class XlsxWorkbook
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    private readonly Dictionary<string, XlsxSheet> _sheets;

    private XlsxWorkbook(Dictionary<string, XlsxSheet> sheets) => _sheets = sheets;

    public IReadOnlyCollection<string> SheetNames => _sheets.Keys;

    public XlsxSheet? Sheet(string name) => _sheets.GetValueOrDefault(name);

    /// <exception cref="InvalidDataException">El archivo no es un .xlsx.</exception>
    public static XlsxWorkbook Open(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var workbook = Load(zip, "xl/workbook.xml") ?? throw new InvalidDataException("No es un archivo de Excel (.xlsx).");
        var rels = Load(zip, "xl/_rels/workbook.xml.rels");
        var targets = rels?.Root?.Elements(PackageRelationships + "Relationship")
                          .ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!)
                      ?? new Dictionary<string, string>();
        var shared = SharedStrings(zip);

        var sheets = new Dictionary<string, XlsxSheet>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in workbook.Root!.Element(Main + "sheets")?.Elements(Main + "sheet") ?? [])
        {
            var name = (string?)sheet.Attribute("name");
            var id = (string?)sheet.Attribute(Relationships + "id");
            if (name is null || id is null || !targets.TryGetValue(id, out var target))
                continue;
            var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            if (Load(zip, path) is { } xml)
                sheets[name] = new XlsxSheet(name, Cells(xml, shared));
        }
        return new XlsxWorkbook(sheets);
    }

    private static XDocument? Load(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null)
            return null;
        using var s = entry.Open();
        return XDocument.Load(s);
    }

    private static List<string> SharedStrings(ZipArchive zip)
    {
        var xml = Load(zip, "xl/sharedStrings.xml");
        if (xml?.Root is null)
            return [];
        // Un texto puede venir en partes con formato (<r><t>…</t></r>); la guía fonética (<rPh>) no cuenta.
        return xml.Root.Elements(Main + "si")
            .Select(si => string.Concat(si.Descendants(Main + "t").Where(t => t.Parent?.Name != Main + "rPh").Select(t => t.Value)))
            .ToList();
    }

    private static Dictionary<(int, int), XlsxCell> Cells(XDocument xml, List<string> shared)
    {
        var cells = new Dictionary<(int, int), XlsxCell>();
        foreach (var c in xml.Descendants(Main + "c"))
        {
            var reference = (string?)c.Attribute("r");
            if (reference is null)
                continue;
            var type = (string?)c.Attribute("t");
            var raw = c.Element(Main + "v")?.Value;
            XlsxCell? cell = type switch
            {
                "s" when int.TryParse(raw, out var index) && index >= 0 && index < shared.Count => new XlsxCell(shared[index], null),
                "inlineStr" => new XlsxCell(string.Concat(c.Element(Main + "is")?.Descendants(Main + "t").Select(t => t.Value) ?? []), null),
                "str" or "e" => raw is null ? null : new XlsxCell(raw, null),
                "b" => raw is null ? null : new XlsxCell(raw == "1" ? "TRUE" : "FALSE", null),
                _ when raw is not null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) =>
                    new XlsxCell(raw, number),
                _ => null,
            };
            if (cell is not null && cell.Text.Length > 0)
                cells[Position(reference)] = cell;
        }
        return cells;
    }

    /// <summary>"AB12" → (12, 28).</summary>
    public static (int Row, int Column) Position(string reference)
    {
        var column = 0;
        var i = 0;
        for (; i < reference.Length && char.IsLetter(reference[i]); i++)
            column = column * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
        return (int.Parse(reference.AsSpan(i), CultureInfo.InvariantCulture), column);
    }
}
