using ClosedXML.Excel;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Text;
using System.Text.Json;

namespace Pulse.Services;

/// <summary>
/// Generates Microsoft Excel (.xlsx) workbooks using ClosedXML.
///
/// Three generation modes:
///   1. Chat export     — structured conversation table.
///   2. JSON data       — any JSON array (e.g. db_execute_query output) → auto-formatted table.
///   3. Markdown tables — every pipe-table in the Markdown becomes a separate sheet.
/// </summary>
public sealed class ExcelService : IExcelService
{
    // ── Brand colours ─────────────────────────────────────────────────────────
    private static readonly XLColor ColBlue    = XLColor.FromHtml("#0D6EFD");
    private static readonly XLColor ColMuted   = XLColor.FromHtml("#6C757D");
    private static readonly XLColor ColRowAlt  = XLColor.FromHtml("#EBF3FF");
    private static readonly XLColor ColBorder  = XLColor.FromHtml("#DEE2E6");
    private static readonly XLColor ColUserBg  = XLColor.FromHtml("#E7F1FF");
    private static readonly XLColor ColAiBg    = XLColor.FromHtml("#F8F9FA");

    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    // ── Public API ────────────────────────────────────────────────────────────

    public byte[] GenerateChatExcel(string sessionId, IReadOnlyList<DisplayMessage> messages)
    {
        using var wb = new XLWorkbook();
        SetDocProps(wb, $"Pulse Chat — {sessionId[..Math.Min(12, sessionId.Length)]}");

        var ws = wb.AddWorksheet("Conversation");

        // ── Headers ───────────────────────────────────────────────────────────
        var headers = new[] { "No.", "Timestamp", "Role", "Message" };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];

        StyleHeaderRow(ws.Row(1), headers.Length);

        // ── Data rows ─────────────────────────────────────────────────────────
        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            var row = i + 2;
            var isUser = msg.Role == "user";

            ws.Cell(row, 1).Value = i + 1;
            ws.Cell(row, 2).Value = msg.Timestamp.ToLocalTime();
            ws.Cell(row, 2).Style.NumberFormat.Format = "yyyy-mm-dd hh:mm";
            ws.Cell(row, 3).Value = isUser ? "You" : "AI Agent";
            ws.Cell(row, 4).Value = msg.Content;

            // Role-based row tint
            var bg = isUser ? ColUserBg : ColAiBg;
            ws.Range(row, 1, row, 4).Style.Fill.BackgroundColor = bg;
        }

        // ── Table + layout ────────────────────────────────────────────────────
        var dataRows = messages.Count;
        if (dataRows > 0)
        {
            var tbl = ws.Range(1, 1, dataRows + 1, 4).CreateTable("ChatTable");
            tbl.Theme = XLTableTheme.TableStyleMedium2;
            tbl.ShowTotalsRow = false;
        }

        ws.Column(1).Width = 6;
        ws.Column(2).Width = 19;
        ws.Column(3).Width = 12;
        ws.Column(4).Width = 80;
        ws.Column(4).Style.Alignment.WrapText = true;
        ws.SheetView.FreezeRows(1);

        return ToBytes(wb);
    }

    public byte[] GenerateDataExcel(string title, string sheetName, string jsonData)
    {
        using var wb = new XLWorkbook();
        SetDocProps(wb, title);

        JsonElement root;
        try { root = JsonDocument.Parse(jsonData).RootElement; }
        catch
        {
            var ws2 = wb.AddWorksheet(SanitizeSheet(sheetName));
            ws2.Cell(1, 1).Value = "Invalid JSON data.";
            return ToBytes(wb);
        }

        var rows = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToList()
            : [];

        var ws = wb.AddWorksheet(SanitizeSheet(sheetName));

        if (rows.Count == 0)
        {
            ws.Cell(1, 1).Value = "No data returned.";
            return ToBytes(wb);
        }

        // Collect all column names (union of all row keys for schema-flexible JSON)
        var columns = rows
            .SelectMany(r => r.EnumerateObject().Select(p => p.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // ── Header row ────────────────────────────────────────────────────────
        for (var c = 0; c < columns.Count; c++)
            ws.Cell(1, c + 1).Value = columns[c];

        StyleHeaderRow(ws.Row(1), columns.Count);

        // ── Data rows ─────────────────────────────────────────────────────────
        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < columns.Count; c++)
            {
                var cell = ws.Cell(r + 2, c + 1);
                if (rows[r].TryGetProperty(columns[c], out var val))
                    SetCellValue(cell, val);
            }

            if (r % 2 == 1)
                ws.Range(r + 2, 1, r + 2, columns.Count)
                  .Style.Fill.BackgroundColor = ColRowAlt;
        }

        // ── Table + layout ────────────────────────────────────────────────────
        var tbl = ws.Range(1, 1, rows.Count + 1, columns.Count).CreateTable("DataTable");
        tbl.Theme = XLTableTheme.TableStyleMedium2;
        tbl.ShowTotalsRow = false;

        AutoFitColumns(ws, columns.Count, 8, 50);
        ws.SheetView.FreezeRows(1);

        // ── Summary cell above table (title + count) ──────────────────────────
        // Prepend a title row before the table is impractical after creation,
        // so we embed the metadata in a separate "Info" sheet instead.
        AddInfoSheet(wb, title, sheetName, rows.Count, columns.Count);

        return ToBytes(wb);
    }

    public byte[] GenerateMarkdownTablesExcel(string title, string markdownContent)
    {
        using var wb = new XLWorkbook();
        SetDocProps(wb, title);

        var doc    = Markdown.Parse(markdownContent, Pipeline);
        var tables = doc.Descendants<Table>().ToList();

        if (tables.Count == 0)
        {
            var ws = wb.AddWorksheet("Content");
            ws.Cell(1, 1).Value = title;
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontColor = ColBlue;
            ws.Cell(2, 1).Value = "No Markdown tables found in the provided content.";
            ws.Column(1).Width = 60;
            return ToBytes(wb);
        }

        for (var idx = 0; idx < tables.Count; idx++)
        {
            var sheetName = tables.Count == 1 ? "Data" : $"Table {idx + 1}";
            RenderMarkdownTable(wb, sheetName, tables[idx]);
        }

        AddInfoSheet(wb, title, $"{tables.Count} table(s)", -1, -1);
        return ToBytes(wb);
    }

    // ── Markdown table → sheet ────────────────────────────────────────────────

    private static void RenderMarkdownTable(XLWorkbook wb, string sheetName, Table mdTable)
    {
        var ws = wb.AddWorksheet(SanitizeSheet(sheetName));

        var allRows   = mdTable.OfType<TableRow>().ToList();
        var headerRow = allRows.FirstOrDefault(r => r.IsHeader);
        var dataRows  = allRows.Where(r => !r.IsHeader).ToList();
        var colCount  = allRows.Max(r => r.Count);

        var excelRow = 1;

        // ── Header ────────────────────────────────────────────────────────────
        if (headerRow is not null)
        {
            var cells = headerRow.OfType<TableCell>().ToList();
            for (var c = 0; c < cells.Count; c++)
                ws.Cell(excelRow, c + 1).Value = ExtractCellText(cells[c]);
            StyleHeaderRow(ws.Row(excelRow), cells.Count);
            excelRow++;
        }

        // ── Data rows ─────────────────────────────────────────────────────────
        var dataRowIdx = 0;
        foreach (var row in dataRows)
        {
            var cells = row.OfType<TableCell>().ToList();
            for (var c = 0; c < cells.Count; c++)
            {
                var text = ExtractCellText(cells[c]);
                SetCellValueFromString(ws.Cell(excelRow, c + 1), text);
            }
            if (dataRowIdx % 2 == 1)
                ws.Range(excelRow, 1, excelRow, colCount)
                  .Style.Fill.BackgroundColor = ColRowAlt;
            excelRow++;
            dataRowIdx++;
        }

        // ── Table + layout ────────────────────────────────────────────────────
        if (excelRow > 2)
        {
            var tbl = ws.Range(1, 1, excelRow - 1, colCount).CreateTable($"T_{SanitizeSheet(sheetName)}");
            tbl.Theme = XLTableTheme.TableStyleMedium2;
        }

        AutoFitColumns(ws, colCount, 8, 40);
        ws.SheetView.FreezeRows(1);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void StyleHeaderRow(IXLRow row, int colCount)
    {
        var range = row.Worksheet.Range(row.RowNumber(), 1, row.RowNumber(), colCount);
        range.Style.Font.Bold          = true;
        range.Style.Font.FontColor     = XLColor.White;
        range.Style.Fill.BackgroundColor = ColBlue;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        row.Height = 18;
    }

    private static void SetCellValue(IXLCell cell, JsonElement val)
    {
        switch (val.ValueKind)
        {
            case JsonValueKind.String:
                var s = val.GetString() ?? "";
                // Try to parse as date
                if (DateTime.TryParse(s, out var dt))
                {
                    cell.Value = dt;
                    cell.Style.NumberFormat.Format = "yyyy-mm-dd hh:mm:ss";
                }
                else cell.Value = s;
                break;

            case JsonValueKind.Number:
                cell.Value = val.TryGetInt64(out var i)  ? (double)i
                           : val.TryGetDouble(out var d) ? d
                           : 0d;
                break;

            case JsonValueKind.True:  cell.Value = true;  break;
            case JsonValueKind.False: cell.Value = false; break;
            case JsonValueKind.Null:  break;              // leave blank
            default: cell.Value = val.GetRawText(); break;
        }
    }

    private static void SetCellValueFromString(IXLCell cell, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (long.TryParse(text, out var l))   { cell.Value = (double)l;               return; }
        if (double.TryParse(text, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
                                              { cell.Value = d;                        return; }
        if (DateTime.TryParse(text, out var dt))
                                              { cell.Value = dt;
                                                cell.Style.NumberFormat.Format = "yyyy-mm-dd"; return; }
        cell.Value = text;
    }

    private static void AutoFitColumns(IXLWorksheet ws, int colCount, int min, int max)
    {
        for (var c = 1; c <= colCount; c++)
        {
            ws.Column(c).AdjustToContents();
            var w = ws.Column(c).Width;
            ws.Column(c).Width = Math.Clamp(w, min, max);
        }
    }

    private static void AddInfoSheet(XLWorkbook wb, string title, string sheetHint,
        int rows, int cols)
    {
        var ws = wb.AddWorksheet("Info");
        ws.TabColor = ColBlue;

        ws.Cell(1, 1).Value = "Pulse AI Agent";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(1, 1).Style.Font.FontColor = ColBlue;

        ws.Cell(2, 1).Value = title;
        ws.Cell(2, 1).Style.Font.Bold = true;
        ws.Cell(2, 1).Style.Font.FontSize = 12;

        ws.Cell(3, 1).Value = $"Generated: {DateTime.UtcNow.ToLocalTime():MMMM d, yyyy  h:mm tt}";
        ws.Cell(3, 1).Style.Font.FontColor = ColMuted;

        if (rows >= 0 && cols >= 0)
        {
            ws.Cell(4, 1).Value = $"Sheet: {sheetHint}   •   {rows} row(s)   •   {cols} column(s)";
            ws.Cell(4, 1).Style.Font.FontColor = ColMuted;
        }
        else
        {
            ws.Cell(4, 1).Value = sheetHint;
            ws.Cell(4, 1).Style.Font.FontColor = ColMuted;
        }

        ws.Column(1).Width = 60;

        // Move Info sheet to last position
        ws.Position = wb.Worksheets.Count;
    }

    private static string ExtractCellText(TableCell cell)
    {
        var sb = new StringBuilder();
        foreach (var block in cell)
            if (block is ParagraphBlock para)
                AppendInlineText(sb, para.Inline);
        return sb.ToString().Trim();
    }

    private static void AppendInlineText(StringBuilder sb, ContainerInline? container)
    {
        if (container is null) return;
        foreach (var inline in container)
        {
            if (inline is LiteralInline lit)
                sb.Append(lit.Content.ToString());
            else if (inline is ContainerInline nested)
                AppendInlineText(sb, nested);
        }
    }

    private static void SetDocProps(XLWorkbook wb, string title)
    {
        wb.Properties.Author  = "Pulse AI Agent";
        wb.Properties.Title   = title;
        wb.Properties.Company = "Pulse";
    }

    private static string SanitizeSheet(string name)
    {
        // Excel sheet names: max 31 chars, no: \ / ? * [ ]
        var clean = new string(name
            .Select(c => c is '\\' or '/' or '?' or '*' or '[' or ']' or ':' ? '_' : c)
            .ToArray()).Trim();
        return clean.Length > 31 ? clean[..31] : (clean.Length == 0 ? "Sheet1" : clean);
    }

    private static byte[] ToBytes(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}

// ── Markdig AST extension ─────────────────────────────────────────────────────
internal static class MarkdigExtensions
{
    /// <summary>Depth-first enumeration of all descendants of type <typeparamref name="T"/>.</summary>
    internal static IEnumerable<T> Descendants<T>(this MarkdownObject obj) where T : MarkdownObject
    {
        if (obj is T self) yield return self;
        if (obj is not ContainerBlock container) yield break;
        foreach (var child in container)
        foreach (var desc in child.Descendants<T>())
            yield return desc;
    }
}
