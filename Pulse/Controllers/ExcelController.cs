using Pulse.Infrastructure;
using Pulse.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using System.Security.Claims;

namespace Pulse.Controllers;

/// <summary>Serves generated Excel (.xlsx) workbooks.</summary>
[Authorize]
public class ExcelController(
    IExcelService excelService,
    ChatHistoryService chatHistory,
    DownloadTokenStore tokenStore,
    ILogger<ExcelController> logger) : Controller
{
    private const string ExcelMime =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // ── GET /api/excel/chat/{sessionId} ───────────────────────────────────────

    [HttpGet("/api/excel/chat/{sessionId}")]
    public IActionResult ExportChat(string sessionId)
    {
        var userId   = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var messages = chatHistory.GetDisplayMessages(sessionId, userId);
        if (messages.Count == 0)
            return NotFound(new { error = "No messages found for this session." });

        try
        {
            var xlsx     = excelService.GenerateChatExcel(sessionId, messages);
            var fileName = $"Pulse_Chat_{sessionId[..Math.Min(8, sessionId.Length)]}_{DateTime.UtcNow:yyyyMMdd}.xlsx";

            logger.LogInformation("Chat XLSX exported: session={S} messages={N} bytes={B}",
                sessionId, messages.Count, xlsx.Length);

            return File(xlsx, ExcelMime, fileName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Chat XLSX export failed for session {SessionId}", sessionId);
            return StatusCode(500, new { error = "Excel generation failed." });
        }
    }

    // ── GET /api/excel/download/{token} ───────────────────────────────────────

    [HttpGet("/api/excel/download/{token}")]
    public IActionResult DownloadByToken(string token)
    {
        var entry = tokenStore.Get(token);
        if (entry is null)
        {
            logger.LogWarning("Excel token not found or expired: {Token}", token);
            return NotFound(new { error = "This link has expired or is invalid. Ask the agent to regenerate it." });
        }

        logger.LogInformation("XLSX downloaded via token {Token}: {FileName}", token, entry.Value.FileName);
        return File(entry.Value.Data, ExcelMime, entry.Value.FileName);
    }
}
