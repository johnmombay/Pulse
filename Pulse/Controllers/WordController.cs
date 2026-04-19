using Pulse.Infrastructure;
using Pulse.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using System.Security.Claims;

namespace Pulse.Controllers;

/// <summary>Serves generated Word (.docx) documents.</summary>
[Authorize]
public class WordController(
    IWordService wordService,
    ChatHistoryService chatHistory,
    DownloadTokenStore tokenStore,
    ILogger<WordController> logger) : Controller
{
    private const string WordMime =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    // ── GET /api/word/chat/{sessionId} ────────────────────────────────────────

    [HttpGet("/api/word/chat/{sessionId}")]
    public IActionResult ExportChat(string sessionId)
    {
        var userId   = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var messages = chatHistory.GetDisplayMessages(sessionId, userId);
        if (messages.Count == 0)
            return NotFound(new { error = "No messages found for this session." });

        try
        {
            var docx     = wordService.GenerateChatDocx(sessionId, messages);
            var fileName = $"Pulse_Chat_{sessionId[..Math.Min(8, sessionId.Length)]}_{DateTime.UtcNow:yyyyMMdd}.docx";

            logger.LogInformation("Chat DOCX exported: session={S} messages={N} bytes={B}",
                sessionId, messages.Count, docx.Length);

            return File(docx, WordMime, fileName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Chat DOCX export failed for session {SessionId}", sessionId);
            return StatusCode(500, new { error = "Word document generation failed." });
        }
    }

    // ── GET /api/word/download/{token} ────────────────────────────────────────

    [HttpGet("/api/word/download/{token}")]
    public IActionResult DownloadByToken(string token)
    {
        var entry = tokenStore.Get(token);
        if (entry is null)
        {
            logger.LogWarning("Word token not found or expired: {Token}", token);
            return NotFound(new { error = "This link has expired or is invalid. Ask the agent to regenerate it." });
        }

        logger.LogInformation("DOCX downloaded via token {Token}: {FileName}", token, entry.Value.FileName);
        return File(entry.Value.Data, WordMime, entry.Value.FileName);
    }
}
