using Pulse.Infrastructure;
using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace Pulse.Services;

/// <summary>
/// Exposes Excel generation as Semantic Kernel <see cref="KernelFunction"/>s so the
/// AI agent can produce downloadable .xlsx spreadsheets directly inside a conversation.
///
/// Two tools are provided:
///   • <c>generate_excel_from_data</c>  — JSON array → formatted Excel table.
///     Ideal for piping <c>db_execute_query</c> results straight into a spreadsheet.
///   • <c>generate_excel_from_markdown</c> — every pipe-table in Markdown → separate sheet.
///     Ideal after any analytical response that already contains tables.
/// </summary>
public sealed class ExcelGeneratorPlugin(
    IExcelService excelService,
    DownloadTokenStore tokenStore,
    IHttpContextAccessor httpContextAccessor,
    ILogger<ExcelGeneratorPlugin> logger)
{
    // ── Tool 1: JSON data → Excel ─────────────────────────────────────────────

    [KernelFunction("generate_excel_from_data")]
    [Description(
        "Generates a Microsoft Excel (.xlsx) spreadsheet from a JSON array of objects and returns a download URL. " +
        "Use this when the user asks to export data, download results as Excel, or save a table to a spreadsheet. " +
        "Pass the raw JSON output from db_execute_query directly as jsonData. " +
        "The URL is valid for 1 hour. Include it in your response as a Markdown link.")]
    public string GenerateExcelFromData(
        [Description("Descriptive title for the workbook (e.g. 'Q4 Sales Results').")]
        string title,
        [Description("Name for the data sheet — keep it short (max 31 characters).")]
        string sheetName,
        [Description(
            "JSON array of flat objects to populate the table, " +
            "e.g. [{\"id\":1,\"name\":\"Alice\",\"revenue\":9200},{\"id\":2,\"name\":\"Bob\",\"revenue\":7300}]. " +
            "Each object key becomes a column header; all values are typed automatically.")]
        string jsonData)
    {
        logger.LogInformation("Agent requested Excel (data): {Title}, sheet={Sheet}", title, sheetName);
        return Execute(
            () => excelService.GenerateDataExcel(title, sheetName, jsonData),
            title, ".xlsx");
    }

    // ── Tool 2: Markdown tables → Excel ──────────────────────────────────────

    [KernelFunction("generate_excel_from_markdown")]
    [Description(
        "Extracts every Markdown pipe-table from the provided content and creates one Excel sheet per table. " +
        "Use this when the response already contains | table | syntax and the user wants to download it as Excel. " +
        "The URL is valid for 1 hour. Include it in your response as a Markdown link.")]
    public string GenerateExcelFromMarkdown(
        [Description("Descriptive title for the workbook.")]
        string title,
        [Description(
            "Full Markdown text containing one or more pipe tables. " +
            "Tables must use standard | column | syntax with a header separator row.")]
        string markdownContent)
    {
        logger.LogInformation("Agent requested Excel (markdown): {Title} ({Chars} chars)",
            title, markdownContent.Length);
        return Execute(
            () => excelService.GenerateMarkdownTablesExcel(title, markdownContent),
            title, ".xlsx");
    }

    // ── Shared execution helper ───────────────────────────────────────────────

    private string Execute(Func<byte[]> generate, string title, string ext)
    {
        try
        {
            var bytes    = generate();
            var fileName = SanitizeFileName(title) + ext;
            var token    = tokenStore.Store(bytes, fileName);
            var url      = BuildUrl($"/api/excel/download/{token}");

            logger.LogInformation("Excel generated: {FileName} ({Bytes} bytes)", fileName, bytes.Length);
            return $"Excel spreadsheet ready. [Download {fileName}]({url})";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Excel generation failed for: {Title}", title);
            return $"Excel generation failed: {ex.Message}";
        }
    }

    private string BuildUrl(string path)
    {
        var ctx = httpContextAccessor.HttpContext;
        if (ctx is null) return path;
        return $"{ctx.Request.Scheme}://{ctx.Request.Host}{path}";
    }

    private static string SanitizeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(title.Select(c => invalid.Contains(c) ? '_' : c).ToArray())
            .Trim('_', ' ').Replace(' ', '_');
        return clean.Length > 80 ? clean[..80] : (clean.Length == 0 ? "export" : clean);
    }
}
