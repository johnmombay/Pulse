using Pulse.Infrastructure;
using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace Pulse.Services;

/// <summary>
/// Exposes PDF generation as Semantic Kernel <see cref="KernelFunction"/>s so the
/// AI agent can produce downloadable reports directly inside a conversation.
///
/// The generated PDF is stored in <see cref="PdfTokenStore"/> (1-hour TTL).
/// The agent returns a download URL the user can click in the chat.
/// </summary>
public sealed class PdfGeneratorPlugin(
    IPdfService pdfService,
    DownloadTokenStore tokenStore,
    IHttpContextAccessor httpContextAccessor,
    ILogger<PdfGeneratorPlugin> logger)
{
    // ── Tool: generate_pdf_report ─────────────────────────────────────────────

    [KernelFunction("generate_pdf_report")]
    [Description(
        "Generates a formatted A4 PDF report from Markdown content and returns a download URL. " +
        "Use this when the user asks for a PDF, a downloadable report, or a printable summary. " +
        "The URL is valid for 1 hour. Include it in your response as a Markdown link so the user can click it.")]
    public string GenerateReportPdf(
        [Description("Short, descriptive title of the report (shown in the PDF header).")]
        string title,
        [Description("Full Markdown content of the report body. Supports headings, bold, lists, code blocks, tables.")]
        string content)
    {
        logger.LogInformation("Agent requested PDF report: {Title} ({Chars} chars)", title, content.Length);

        try
        {
            var pdf      = pdfService.GenerateReportPdf(title, content);
            var fileName = SanitizeFileName(title) + ".pdf";
            var token    = tokenStore.Store(pdf, fileName);
            var url      = BuildUrl($"/api/pdf/download/{token}");

            logger.LogInformation("PDF report generated: {FileName} ({Bytes} bytes), token={Token}", fileName, pdf.Length, token);

            return $"PDF generated successfully. [Download {fileName}]({url})";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PDF report generation failed for title: {Title}", title);
            return $"PDF generation failed: {ex.Message}";
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string BuildUrl(string path)
    {
        var ctx = httpContextAccessor.HttpContext;
        if (ctx is null) return path;
        return $"{ctx.Request.Scheme}://{ctx.Request.Host}{path}";
    }

    private static string SanitizeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean   = new string(title
            .Select(c => invalid.Contains(c) ? '_' : c)
            .ToArray())
            .Trim('_', ' ')
            .Replace(' ', '_');
        return clean.Length > 80 ? clean[..80] : (clean.Length == 0 ? "report" : clean);
    }
}
