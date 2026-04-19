using Pulse.Infrastructure;
using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace Pulse.Services;

/// <summary>
/// Exposes Word document generation as Semantic Kernel <see cref="KernelFunction"/>s
/// so the AI agent can produce downloadable .docx reports directly inside a conversation.
/// The generated file is stored in <see cref="DownloadTokenStore"/> (1-hour TTL).
/// </summary>
public sealed class WordGeneratorPlugin(
    IWordService wordService,
    DownloadTokenStore tokenStore,
    IHttpContextAccessor httpContextAccessor,
    ILogger<WordGeneratorPlugin> logger)
{
    [KernelFunction("generate_word_report")]
    [Description(
        "Generates a Microsoft Word (.docx) report from Markdown content and returns a download URL. " +
        "Use this when the user asks for a Word document, an editable report, or a .docx file. " +
        "The URL is valid for 1 hour. Include it in your response as a Markdown link so the user can click it.")]
    public string GenerateReportDocx(
        [Description("Short, descriptive title of the report (shown in the document header).")]
        string title,
        [Description("Full Markdown content of the report body. Supports headings, bold, italic, lists, code blocks.")]
        string content)
    {
        logger.LogInformation("Agent requested Word report: {Title} ({Chars} chars)", title, content.Length);

        try
        {
            var docx     = wordService.GenerateReportDocx(title, content);
            var fileName = SanitizeFileName(title) + ".docx";
            var token    = tokenStore.Store(docx, fileName);
            var url      = BuildUrl($"/api/word/download/{token}");

            logger.LogInformation("Word report generated: {FileName} ({Bytes} bytes), token={Token}",
                fileName, docx.Length, token);

            return $"Word document generated. [Download {fileName}]({url})";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Word report generation failed for title: {Title}", title);
            return $"Word generation failed: {ex.Message}";
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
        return clean.Length > 80 ? clean[..80] : (clean.Length == 0 ? "report" : clean);
    }
}
