using Pulse.Models;
using ClosedXML.Excel;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using YamlDotNet.RepresentationModel;

namespace Pulse.Services;

/// <summary>
/// Reads flat-file data sources (CSV, Tab-delimited, Excel, JSON, XML, YAML, Fixed-width)
/// and returns the rows as <c>List&lt;Dictionary&lt;string, string&gt;&gt;</c> for agent consumption.
/// </summary>
public sealed class FlatFileDataService(
    LlmSettingsService llmSettings,
    ILogger<FlatFileDataService> logger)
{
    // ── Public API ────────────────────────────────────────────────────────────

    public FlatFileDataSource? GetSource(string id)
        => (llmSettings.Get().FlatFileSources ?? [])
           .FirstOrDefault(s => s.Id == id && s.IsEnabled);

    public IReadOnlyList<FlatFileDataSource> GetAllSources()
        => llmSettings.Get().FlatFileSources ?? [];

    public async Task<(List<Dictionary<string, string>> Rows, string? Error)> ReadAsync(
        string sourceId,
        int? maxRows  = null,
        int  offset   = 0,
        CancellationToken ct = default)
    {
        var source = GetSource(sourceId);
        if (source is null)
            return ([], $"Data source '{sourceId}' not found or is disabled.");

        if (!File.Exists(source.FilePath))
            return ([], $"File not found: {source.FilePath}");

        var limit = Math.Min(maxRows ?? source.MaxRows, source.MaxRows);

        try
        {
            var allRows = source.Format switch
            {
                FlatFileFormat.Csv          => await ReadDelimitedAsync(source, null,  ct),
                FlatFileFormat.TabDelimited => await ReadDelimitedAsync(source, '\t',  ct),
                FlatFileFormat.Excel        => ReadExcel(source),
                FlatFileFormat.Json         => await ReadJsonAsync(source, ct),
                FlatFileFormat.Xml          => ReadXml(source),
                FlatFileFormat.Yaml         => await ReadYamlAsync(source, ct),
                FlatFileFormat.FixedWidth   => await ReadFixedWidthAsync(source, ct),
                _                           => throw new NotSupportedException($"Format {source.Format} not supported")
            };

            var page = allRows.Skip(offset).Take(limit).ToList();

            logger.LogInformation(
                "FlatFile read: source={Id} format={Fmt} totalRows={Total} returned={Ret}",
                sourceId, source.Format, allRows.Count, page.Count);

            return (page, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read flat file source {Id}: {Path}", sourceId, source.FilePath);
            return ([], $"Read error: {ex.Message}");
        }
    }

    // ── CSV / Tab-delimited ───────────────────────────────────────────────────

    private static async Task<List<Dictionary<string, string>>> ReadDelimitedAsync(
        FlatFileDataSource src, char? forceDelimiter, CancellationToken ct)
    {
        var enc       = GetEncoding(src.Encoding);
        var content   = await File.ReadAllTextAsync(src.FilePath, enc, ct);
        var delimiter = forceDelimiter
                        ?? (src.Delimiter?.Length == 1 ? src.Delimiter[0] : ',');
        var rawRows   = ParseCsv(content, delimiter);
        return ToRowDicts(rawRows, src.HasHeaders);
    }

    // ── Excel ─────────────────────────────────────────────────────────────────

    private static List<Dictionary<string, string>> ReadExcel(FlatFileDataSource src)
    {
        using var wb = new XLWorkbook(src.FilePath);
        var ws = string.IsNullOrWhiteSpace(src.SheetName)
            ? wb.Worksheets.First()
            : wb.Worksheet(src.SheetName);

        var rawRows = new List<string[]>();
        foreach (var row in ws.RowsUsed())
        {
            var cells = Enumerable.Range(1, row.LastCellUsed()?.Address.ColumnNumber ?? 0)
                .Select(col => row.Cell(col).IsEmpty() ? "" : row.Cell(col).GetString())
                .ToArray();
            rawRows.Add(cells);
        }
        return ToRowDicts(rawRows, src.HasHeaders);
    }

    // ── JSON ──────────────────────────────────────────────────────────────────

    private static async Task<List<Dictionary<string, string>>> ReadJsonAsync(
        FlatFileDataSource src, CancellationToken ct)
    {
        var enc     = GetEncoding(src.Encoding);
        var content = await File.ReadAllTextAsync(src.FilePath, enc, ct);
        using var doc = JsonDocument.Parse(content);

        // Find the first JSON array — either root or first array-valued property
        JsonElement arrayEl;
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            arrayEl = doc.RootElement;
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            // Pick the first property whose value is an array
            arrayEl = doc.RootElement.EnumerateObject()
                .FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.Array).Value;
            if (arrayEl.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("JSON file does not contain a top-level array or an array-valued property.");
        }
        else
        {
            throw new InvalidDataException("JSON file must be an array or an object containing an array.");
        }

        var rows = new List<Dictionary<string, string>>();
        foreach (var el in arrayEl.EnumerateArray())
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                    row[prop.Name] = prop.Value.ToString();
            }
            else
            {
                row["value"] = el.ToString();
            }
            rows.Add(row);
        }
        return rows;
    }

    // ── XML ───────────────────────────────────────────────────────────────────

    private static List<Dictionary<string, string>> ReadXml(FlatFileDataSource src)
    {
        var doc = XDocument.Load(src.FilePath);

        // Find the most-repeated element name (these are the "rows")
        var root         = doc.Root ?? throw new InvalidDataException("Empty XML document.");
        var childGroups  = root.Elements()
            .GroupBy(e => e.Name.LocalName)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (childGroups.Count == 0)
            throw new InvalidDataException("XML file has no child elements under the root.");

        var rowElements = childGroups[0].ToList();

        var rows = new List<Dictionary<string, string>>();
        foreach (var el in rowElements)
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Attributes
            foreach (var attr in el.Attributes())
                row[attr.Name.LocalName] = attr.Value;

            // Child elements (leaf text nodes)
            foreach (var child in el.Elements())
            {
                if (!child.HasElements)
                    row[child.Name.LocalName] = child.Value;
            }

            // If the element itself is a leaf text node
            if (!el.HasElements && !el.HasAttributes && !string.IsNullOrWhiteSpace(el.Value))
                row["value"] = el.Value;

            rows.Add(row);
        }
        return rows;
    }

    // ── YAML ──────────────────────────────────────────────────────────────────

    private static async Task<List<Dictionary<string, string>>> ReadYamlAsync(
        FlatFileDataSource src, CancellationToken ct)
    {
        var enc     = GetEncoding(src.Encoding);
        var content = await File.ReadAllTextAsync(src.FilePath, enc, ct);

        var yaml   = new YamlStream();
        using var reader = new StringReader(content);
        yaml.Load(reader);

        if (yaml.Documents.Count == 0)
            return [];

        var root = yaml.Documents[0].RootNode;

        // Find the sequence to treat as rows
        YamlSequenceNode? seq = root switch
        {
            YamlSequenceNode s => s,
            YamlMappingNode m  => m.Children.Values.OfType<YamlSequenceNode>().FirstOrDefault(),
            _                  => null
        };

        if (seq is null)
            throw new InvalidDataException("YAML file must contain a sequence (list) at the root or as a top-level property.");

        var rows = new List<Dictionary<string, string>>();
        foreach (var node in seq)
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (node is YamlMappingNode mapping)
            {
                foreach (var kv in mapping.Children)
                    row[((YamlScalarNode)kv.Key).Value ?? ""] = kv.Value.ToString();
            }
            else
            {
                row["value"] = node.ToString();
            }
            rows.Add(row);
        }
        return rows;
    }

    // ── Fixed-width ───────────────────────────────────────────────────────────

    private static async Task<List<Dictionary<string, string>>> ReadFixedWidthAsync(
        FlatFileDataSource src, CancellationToken ct)
    {
        if (src.FixedWidthColumns.Count == 0)
            throw new InvalidDataException("Fixed-width source has no column definitions.");

        // Parse column specs: "Name:Start:Length"
        var cols = src.FixedWidthColumns
            .Select(spec =>
            {
                var parts = spec.Split(':');
                if (parts.Length != 3 || !int.TryParse(parts[1], out var start) || !int.TryParse(parts[2], out var len))
                    throw new InvalidDataException($"Invalid fixed-width column spec: '{spec}'. Expected Name:Start:Length.");
                return (Name: parts[0].Trim(), Start: start, Length: len);
            })
            .ToList();

        var enc   = GetEncoding(src.Encoding);
        var lines = await File.ReadAllLinesAsync(src.FilePath, enc, ct);

        var rows  = new List<Dictionary<string, string>>();
        bool skip = src.HasHeaders;
        foreach (var line in lines)
        {
            if (skip) { skip = false; continue; }
            if (string.IsNullOrWhiteSpace(line)) continue;

            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, start, length) in cols)
            {
                if (start >= line.Length) { row[name] = ""; continue; }
                var end   = Math.Min(start + length, line.Length);
                row[name] = line[start..end].Trim();
            }
            rows.Add(row);
        }
        return rows;
    }

    // ── CSV parser ────────────────────────────────────────────────────────────

    internal static List<string[]> ParseCsv(string content, char delimiter)
    {
        var rows   = new List<string[]>();
        var fields = new List<string>();
        var field  = new StringBuilder();
        bool inQ   = false;
        int i      = 0;

        while (i < content.Length)
        {
            char c = content[i];

            if (inQ)
            {
                if (c == '"')
                {
                    // Escaped quote?
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    { field.Append('"'); i += 2; continue; }
                    inQ = false; i++; continue;
                }
                field.Append(c);
            }
            else
            {
                if (c == '"')             { inQ = true; }
                else if (c == delimiter)  { fields.Add(field.ToString()); field.Clear(); }
                else if (c == '\r')
                {
                    fields.Add(field.ToString()); field.Clear();
                    rows.Add([.. fields]);  fields.Clear();
                    if (i + 1 < content.Length && content[i + 1] == '\n') i++;
                }
                else if (c == '\n')
                {
                    fields.Add(field.ToString()); field.Clear();
                    rows.Add([.. fields]);  fields.Clear();
                }
                else { field.Append(c); }
            }
            i++;
        }

        fields.Add(field.ToString());
        if (fields.Count > 0 && fields.Any(f => f.Length > 0))
            rows.Add([.. fields]);

        return rows;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<Dictionary<string, string>> ToRowDicts(List<string[]> raw, bool hasHeaders)
    {
        if (raw.Count == 0) return [];

        string[] headers;
        int dataStart;

        if (hasHeaders && raw.Count >= 1)
        {
            headers   = raw[0];
            dataStart = 1;
        }
        else
        {
            headers   = Enumerable.Range(1, raw.Max(r => r.Length))
                                  .Select(n => $"Column{n}").ToArray();
            dataStart = 0;
        }

        var rows = new List<Dictionary<string, string>>(raw.Count - dataStart);
        for (int r = dataStart; r < raw.Count; r++)
        {
            var cols = raw[r];
            var row  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < headers.Length; c++)
                row[headers[c]] = c < cols.Length ? cols[c].Trim() : "";
            rows.Add(row);
        }
        return rows;
    }

    private static Encoding GetEncoding(string name)
    {
        try   { return Encoding.GetEncoding(name); }
        catch { return Encoding.UTF8; }
    }
}
