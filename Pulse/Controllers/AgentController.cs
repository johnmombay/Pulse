using Pulse.Infrastructure;
using Pulse.Jobs;
using Pulse.Services;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Pulse.Controllers;

[Authorize]
public class AgentController(
    ChatHistoryService chatHistoryService,
    FileExtractionService fileExtractionService,
    ITenantContext tenantContext) : Controller
{
    private string CurrentUserId =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    // GET /Agent/Chat[?sessionId=xxx]
    [HttpGet]
    public IActionResult Chat(string? sessionId)
    {
        ViewData["SessionId"] = string.IsNullOrWhiteSpace(sessionId)
            ? Guid.NewGuid().ToString("N")
            : sessionId;

        ViewData["UserId"] = CurrentUserId;
        return View();
    }

    // GET /api/agent/sessions  — returns all sessions for the current user
    [HttpGet("/api/agent/sessions")]
    public IActionResult GetSessions()
    {
        var sessions = chatHistoryService.GetUserSessions(CurrentUserId);
        return Ok(sessions.Select(s => new
        {
            id           = s.SessionId,
            firstMessage = s.FirstUserMessage,
            createdAt    = s.CreatedAt,
            lastAt       = s.LastAt,
        }));
    }

    // POST /api/agent/message
    [HttpPost("/api/agent/message")]
    public IActionResult SendMessage([FromBody] SendMessageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "Message cannot be empty." });

        var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? Guid.NewGuid().ToString("N")
            : request.SessionId;

        var userId = CurrentUserId;

        BackgroundJob.Enqueue<AgentTaskJob>(j =>
            j.ExecuteAsync(tenantContext.TenantId ?? Guid.Empty, sessionId, request.Message, userId, JobCancellationToken.Null));

        return Ok(new { sessionId });
    }

    // GET /api/agent/history/{sessionId}
    [HttpGet("/api/agent/history/{sessionId}")]
    public IActionResult GetHistory(string sessionId)
    {
        var messages = chatHistoryService.GetDisplayMessages(sessionId, CurrentUserId);
        return Ok(messages);
    }

    // GET /api/agent/status/{sessionId}
    [HttpGet("/api/agent/status/{sessionId}")]
    public IActionResult GetStatus(string sessionId)
    {
        var status = chatHistoryService.GetSessionStatus(sessionId);
        return Ok(new { status = status ?? "idle", isActive = status is not null });
    }

    // DELETE /api/agent/session/{sessionId}
    [HttpDelete("/api/agent/session/{sessionId}")]
    public IActionResult DeleteSession(string sessionId)
    {
        chatHistoryService.RemoveSession(sessionId, CurrentUserId);
        return Ok();
    }

    // POST /api/agent/upload-file
    [HttpPost("/api/agent/upload-file")]
    [RequestSizeLimit(10 * 1024 * 1024)]   // 10 MB
    public async Task<IActionResult> UploadFile(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file provided." });

        if (file.Length > 10 * 1024 * 1024)
            return BadRequest(new { error = "File too large — maximum 10 MB." });

        var content = await fileExtractionService.ExtractAsync(file, ct);
        return Ok(new
        {
            fileName  = file.FileName,
            charCount = content.Length,
            content,
        });
    }
}

public record SendMessageRequest(string? SessionId, string Message);
