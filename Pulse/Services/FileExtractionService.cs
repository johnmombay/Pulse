using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text;
using UglyToad.PdfPig;

namespace Pulse.Services;

/// <summary>
/// Extracts plain text from uploaded files.
/// Supported formats: PDF, DOCX, XLSX/XLS, and any UTF-8 text file.
/// </summary>
public sealed class FileExtractionService(ILogger<FileExtractionService> logger)
{
    private const int MaxChars = 200_000;   // ~200 KB of text — enough context without flooding the model

    public async Task<string> ExtractAsync(IFormFile file, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();

        try
        {
            var text = ext switch
            {
                ".pdf"               => await ExtractPdfAsync(file, ct),
                ".docx"              => await ExtractDocxAsync(file, ct),
                ".xlsx" or ".xls"    => await ExtractExcelAsync(file, ct),
                _                    => await ExtractTextAsync(file, ct),
            };

            if (text.Length > MaxChars)
            {
                text = text[..MaxChars] +
                       $"\n\n[Content truncated at {MaxChars:N0} characters — file is larger than the context limit.]";
            }

            logger.LogInformation(
                "Extracted {Chars} chars from {Name} ({Ext})",
                text.Length, file.FileName, ext);

            return text;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "File extraction failed for {Name}", file.FileName);
            return $"[Extraction failed: {ex.Message}]";
        }
    }

    // ── PDF ───────────────────────────────────────────────────────────────────

    private static async Task<string> ExtractPdfAsync(IFormFile file, CancellationToken ct)
    {
        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);

        using var doc = PdfDocument.Open(ms.ToArray());
        var sb = new StringBuilder();

        foreach (var page in doc.GetPages())
        {
            sb.AppendLine(page.Text);
        }

        return sb.ToString().Trim();
    }

    // ── DOCX ──────────────────────────────────────────────────────────────────

    private static async Task<string> ExtractDocxAsync(IFormFile file, CancellationToken ct)
    {
        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        ms.Seek(0, SeekOrigin.Begin);

        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return "";

        var sb = new StringBuilder();
        foreach (var para in body.Descendants<Paragraph>())
        {
            var line = para.InnerText.Trim();
            if (line.Length > 0) sb.AppendLine(line);
        }

        return sb.ToString().Trim();
    }

    // ── Excel ─────────────────────────────────────────────────────────────────

    private static async Task<string> ExtractExcelAsync(IFormFile file, CancellationToken ct)
    {
        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        ms.Seek(0, SeekOrigin.Begin);

        using var wb = new XLWorkbook(ms);
        var sb = new StringBuilder();

        foreach (var ws in wb.Worksheets)
        {
            sb.AppendLine($"=== Sheet: {ws.Name} ===");
            foreach (var row in ws.RowsUsed())
            {
                var cells = row.Cells()
                    .Select(c => c.IsEmpty() ? "" : c.GetString())
                    .ToArray();
                sb.AppendLine(string.Join("\t", cells));
            }
            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    // ── Plain text ────────────────────────────────────────────────────────────

    private static async Task<string> ExtractTextAsync(IFormFile file, CancellationToken ct)
    {
        using var reader = new StreamReader(
            file.OpenReadStream(),
            encoding: Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: false);

        return (await reader.ReadToEndAsync(ct)).Trim();
    }
}
