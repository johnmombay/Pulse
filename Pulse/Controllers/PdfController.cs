using Pulse.Infrastructure;
using Pulse.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Pulse.Controllers;

/// <summary>
/// Serves generated PDFs.
/// All endpoints require authentication so anonymous users cannot enumerate or
/// download PDFs that were generated in other users' sessions.
/// </summary>
[Authorize]
public class PdfController(
    IPdfService pdfService,
    ChatHistoryService chatHistory,
    DownloadTokenStore tokenStore,
    ILogger<PdfController> logger) : Controller
{
    // ── GET /api/pdf/chat/{sessionId} ─────────────────────────────────────────

    /// <summary>
    /// Exports the entire chat conversation for <paramref name="sessionId"/> as a PDF.
    /// Called directly from the chat UI's "Export PDF" button.
    /// </summary>
    [HttpGet("/api/pdf/chat/{sessionId}")]
    public IActionResult ExportChat(string sessionId)
    {
        var userId   = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var messages = chatHistory.GetDisplayMessages(sessionId, userId);
        if (messages.Count == 0)
            return NotFound(new { error = "No messages found for this session." });

        try
        {
            var pdf      = pdfService.GenerateChatPdf(sessionId, messages);
            var fileName = $"Pulse_Chat_{sessionId[..Math.Min(8, sessionId.Length)]}_{DateTime.UtcNow:yyyyMMdd}.pdf";

            logger.LogInformation(
                "Chat PDF exported: session={Session} messages={N} bytes={B}",
                sessionId, messages.Count, pdf.Length);

            return File(pdf, "application/pdf", fileName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Chat PDF export failed for session {SessionId}", sessionId);
            return StatusCode(500, new { error = "PDF generation failed." });
        }
    }

    // ── GET /api/pdf/download/{token} ─────────────────────────────────────────

    /// <summary>
    /// Redeems a one-time token issued by <see cref="PdfGeneratorPlugin"/> and
    /// streams the PDF to the client.
    /// </summary>
    [HttpGet("/api/pdf/download/{token}")]
    public IActionResult DownloadByToken(string token)
    {
        var entry = tokenStore.Get(token);
        if (entry is null)
        {
            logger.LogWarning("PDF token not found or expired: {Token}", token);
            return NotFound(new { error = "This PDF link has expired or is invalid. Ask the agent to regenerate it." });
        }

        logger.LogInformation("PDF downloaded via token {Token}: {FileName}", token, entry.Value.FileName);
        return File(entry.Value.Data, "application/pdf", entry.Value.FileName);
    }
}
