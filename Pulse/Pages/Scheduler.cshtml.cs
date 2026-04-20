using Pulse.Jobs;
using Pulse.Models;
using Pulse.Services;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace Pulse.Pages;

[Authorize]
public class SchedulerModel(
    SchedulerService schedulerService,
    WorkflowService workflowService,
    ILogger<SchedulerModel> logger) : PageModel
{
    public IReadOnlyList<ScheduledTask>        Tasks     { get; private set; } = [];
    public IReadOnlyList<WorkflowDefinition>   Workflows { get; private set; } = [];

    [BindProperty]
    public TaskInput Input { get; set; } = new();

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    // ── GET ───────────────────────────────────────────────────────────────────

    public async Task OnGetAsync()
    {
        Tasks     = await schedulerService.GetAllAsync(UserId);
        Workflows = await workflowService.GetAllAsync(UserId);
    }

    // ── Save (add / edit) — AJAX ──────────────────────────────────────────────

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Input?.Title))
            return new JsonResult(new { success = false, error = "Title is required." });

        bool isWorkflow = Input.WorkflowDefinitionId.HasValue;

        if (!isWorkflow && string.IsNullOrWhiteSpace(Input.Instructions))
            return new JsonResult(new { success = false, error = "Instructions are required." });

        if (Input.DeliveryType is DeliveryType.Email or DeliveryType.Both &&
            string.IsNullOrWhiteSpace(Input.DeliveryEmail))
            return new JsonResult(new { success = false, error = "Email address is required for email delivery." });

        if (Input.FrequencyType == FrequencyType.OneTime && Input.ScheduledAt is null)
            Input.ScheduledAt = DateTime.UtcNow.AddMinutes(1);

        try
        {
            bool isNew = Input.Id == 0;

            var task = isNew
                ? new ScheduledTask { UserId = UserId }
                : await schedulerService.GetAsync(Input.Id, UserId) ?? new ScheduledTask { UserId = UserId };

            task.Title          = Input.Title.Trim();
            task.Instructions   = Input.Instructions?.Trim() ?? "";
            task.FrequencyType  = Input.FrequencyType;
            task.FrequencyValue = Math.Max(1, Input.FrequencyValue);
            task.ScheduledAt    = Input.FrequencyType == FrequencyType.OneTime ? Input.ScheduledAt?.ToUniversalTime() : null;
            task.DeliveryType   = Input.DeliveryType;
            task.WorkflowDefinitionId = Input.WorkflowDefinitionId;
            task.DeliveryEmail  = Input.DeliveryEmail?.Trim();
            task.IsEnabled      = Input.IsEnabled;

            if (isNew)
                await schedulerService.CreateAsync(task);
            else
                await schedulerService.UpdateAsync(task);

            return new JsonResult(new
            {
                success = true,
                message = $"Task \"{task.Title}\" {(isNew ? "created" : "updated")}.",
                nextRun = task.NextRunAt?.ToString("MMM d, yyyy HH:mm") + " UTC"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save scheduled task");
            return new JsonResult(new { success = false, error = $"Save failed: {ex.Message}" })
            { StatusCode = 500 };
        }
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        await schedulerService.DeleteAsync(id, UserId);
        TempData["SchedulerSuccess"] = "Task deleted.";
        return RedirectToPage();
    }

    // ── Toggle ────────────────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        await schedulerService.ToggleAsync(id, UserId);
        return RedirectToPage();
    }

    // ── Run now — AJAX ────────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostRunNowAsync(int id)
    {
        var task = await schedulerService.GetAsync(id, UserId);
        if (task is null)
            return new JsonResult(new { success = false, error = "Task not found." });

        BackgroundJob.Enqueue<ScheduledTaskJob>(j =>
            j.RunAsync(task.TenantId, task.Id, JobCancellationToken.Null));

        return new JsonResult(new
        {
            success = true,
            message = $"Task \"{task.Title}\" queued for immediate execution."
        });
    }

    // ── Input model ───────────────────────────────────────────────────────────

    public class TaskInput
    {
        public int           Id                   { get; set; } = 0;

        [Required, MaxLength(200)]
        public string        Title                { get; set; } = "";

        public string?       Instructions         { get; set; }

        public int?          WorkflowDefinitionId { get; set; }

        public FrequencyType FrequencyType        { get; set; } = FrequencyType.Days;
        public int           FrequencyValue       { get; set; } = 1;

        public DateTime?     ScheduledAt          { get; set; }

        public DeliveryType  DeliveryType         { get; set; } = DeliveryType.Dashboard;

        [MaxLength(320)]
        public string?       DeliveryEmail        { get; set; }

        public bool          IsEnabled            { get; set; } = true;
    }
}
