using Pulse.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Pulse.Controllers;

[Authorize]
[Route("api/scheduler")]
public class SchedulerController(SchedulerService schedulerService) : Controller
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    [HttpGet("notifications")]
    public async Task<IActionResult> GetNotifications(int limit = 20)
    {
        var results = await schedulerService.GetRecentResultsAsync(UserId, limit);
        return Ok(results.Select(r => new
        {
            r.Id,
            r.TaskTitle,
            r.IsSuccess,
            r.IsRead,
            runAt   = r.RunAt.ToString("MMM d, yyyy HH:mm") + " UTC",
            preview = r.Output.Length > 200 ? r.Output[..197] + "…" : r.Output,
            output  = r.Output,
        }));
    }

    [HttpGet("unread-count")]
    public async Task<IActionResult> GetUnreadCount()
    {
        var count = await schedulerService.GetUnreadCountAsync(UserId);
        return Ok(new { count });
    }

    [HttpPost("mark-read/{id:int}")]
    public async Task<IActionResult> MarkRead(int id)
    {
        await schedulerService.MarkResultReadAsync(id, UserId);
        return Ok(new { success = true });
    }

    [HttpPost("mark-all-read")]
    public async Task<IActionResult> MarkAllRead()
    {
        await schedulerService.MarkAllResultsReadAsync(UserId);
        return Ok(new { success = true });
    }

    [HttpDelete("delete-result/{id:int}")]
    public async Task<IActionResult> DeleteResult(int id)
    {
        await schedulerService.DeleteResultAsync(id, UserId);
        return Ok(new { success = true });
    }

    [HttpDelete("delete-all-results")]
    public async Task<IActionResult> DeleteAllResults()
    {
        await schedulerService.DeleteAllResultsAsync(UserId);
        return Ok(new { success = true });
    }

    [HttpGet("task/{id:int}")]
    public async Task<IActionResult> GetTask(int id)
    {
        var task = await schedulerService.GetAsync(id, UserId);
        if (task is null) return NotFound();
        return Ok(new
        {
            task.Id,
            task.Title,
            task.Instructions,
            task.FrequencyType,
            task.FrequencyValue,
            task.ScheduledAt,
            task.DeliveryType,
            task.DeliveryEmail,
            task.IsEnabled,
            task.WorkflowDefinitionId,
        });
    }
}
