using Pulse.Jobs;
using Pulse.Models;
using Pulse.Services;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Security.Claims;
using System.Text.Json;

namespace Pulse.Pages;

[Authorize]
public class WorkflowsModel(
    WorkflowService workflowService,
    IBackgroundJobClient backgroundJobs,
    ILogger<WorkflowsModel> logger) : PageModel
{
    public IReadOnlyList<WorkflowDefinition> Workflows  { get; private set; } = [];
    public IReadOnlyList<WorkflowRun>        RecentRuns { get; private set; } = [];

    [BindProperty] public WorkflowInput Input { get; set; } = new();

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    public async Task OnGetAsync()
    {
        Workflows  = await workflowService.GetAllAsync(UserId);
        RecentRuns = await workflowService.GetRecentRunsAsync(UserId, 20);
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Input.Title))
            return new JsonResult(new { success = false, error = "Title is required." });
        if (string.IsNullOrWhiteSpace(Input.GoalDescription))
            return new JsonResult(new { success = false, error = "Goal is required." });

        List<StepInput> steps;
        try { steps = JsonSerializer.Deserialize<List<StepInput>>(Input.StepsJson ?? "[]", new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? []; }
        catch { return new JsonResult(new { success = false, error = "Invalid steps data." }); }

        if (steps.Count == 0)
            return new JsonResult(new { success = false, error = "Add at least one step." });

        for (int i = 0; i < steps.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(steps[i].Name)) return new JsonResult(new { success = false, error = $"Step {i + 1} name is required." });
            if (steps[i].StepType == StepType.Webhook)
            {
                if (string.IsNullOrWhiteSpace(steps[i].WebhookUrl)) return new JsonResult(new { success = false, error = $"Step {i + 1} requires a webhook URL." });
            }
            else if (steps[i].StepType == StepType.CallWorkflow)
            {
                if (steps[i].CalledWorkflowId is null or 0) return new JsonResult(new { success = false, error = $"Step {i + 1} requires a workflow to call." });
            }
            else
            {
                if (string.IsNullOrWhiteSpace(steps[i].Instructions)) return new JsonResult(new { success = false, error = $"Step {i + 1} instructions are required." });
            }
        }

        try
        {
            bool isNew = Input.Id == 0;
            var def = isNew
                ? new WorkflowDefinition { UserId = UserId }
                : await workflowService.GetAsync(Input.Id, UserId) ?? new WorkflowDefinition { UserId = UserId };

            def.Title           = Input.Title.Trim();
            def.GoalDescription = Input.GoalDescription.Trim();
            def.IsEnabled       = Input.IsEnabled;
            def.Steps = steps.Select((s, i) => new WorkflowStep
            {
                Order        = i + 1,
                Name         = s.Name.Trim(),
                Instructions = s.Instructions.Trim(),
                StepType        = s.StepType,
                WebhookUrl          = s.WebhookUrl?.Trim(),
                WebhookPlatform     = s.WebhookPlatform?.Trim(),
                CalledWorkflowId    = s.StepType == StepType.CallWorkflow ? s.CalledWorkflowId : null,
            }).ToList();

            if (isNew) await workflowService.CreateAsync(def);
            else        await workflowService.UpdateAsync(def);

            return new JsonResult(new { success = true, message = $"Workflow \"{def.Title}\" {(isNew ? "created" : "updated")}.", id = def.Id });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save workflow");
            return new JsonResult(new { success = false, error = $"Save failed: {ex.Message}" }) { StatusCode = 500 };
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        await workflowService.DeleteAsync(id, UserId);
        TempData["WorkflowSuccess"] = "Workflow deleted.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        await workflowService.ToggleAsync(id, UserId);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRunNowAsync(int id)
    {
        var def = await workflowService.GetAsync(id, UserId);
        if (def is null) return new JsonResult(new { success = false, error = "Workflow not found." });

        backgroundJobs.Enqueue<WorkflowJob>(j => j.RunAsync(def.Id, UserId, JobCancellationToken.Null));

        return new JsonResult(new { success = true, message = $"Workflow \"{def.Title}\" queued — {def.Steps.Count} step(s) will run in sequence." });
    }

    public async Task<IActionResult> OnGetWorkflowAsync(int id)
    {
        var def = await workflowService.GetAsync(id, UserId);
        if (def is null) return NotFound();
        return new JsonResult(new
        {
            def.Id, def.Title, def.GoalDescription, def.IsEnabled,
            steps = def.Steps.Select(s => new { s.Name, s.Instructions, stepType = (int)s.StepType, s.WebhookUrl, s.WebhookPlatform, s.CalledWorkflowId }),
        });
    }

    public async Task<IActionResult> OnGetWorkflowListAsync()
    {
        var list = await workflowService.GetAllAsync(UserId);
        return new JsonResult(list.Select(w => new { w.Id, w.Title }));
    }

    public async Task<IActionResult> OnGetRunAsync(int id)
    {
        var run = await workflowService.GetRunAsync(id, UserId);
        if (run is null) return NotFound();
        return new JsonResult(new
        {
            run.Id, run.WorkflowTitle, run.Status, run.StopReason,
            startedAt  = run.StartedAt.ToString("MMM d, yyyy HH:mm") + " UTC",
            finishedAt = run.FinishedAt?.ToString("MMM d, yyyy HH:mm") + " UTC",
            stepRuns = run.StepRuns.Select(s => new
            {
                s.StepOrder, s.StepName, stepType = s.StepType.ToString(),
                s.Output, s.IsSuccess, s.Decision,
                runAt = s.RunAt.ToString("HH:mm:ss") + " UTC",
            }),
        });
    }

    // ── Run: delete one ───────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostDeleteRunAsync(int id)
    {
        await workflowService.DeleteRunAsync(id, UserId);
        return new JsonResult(new { success = true });
    }

    // ── Run: delete all ───────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostDeleteAllRunsAsync()
    {
        var count = await workflowService.DeleteAllRunsAsync(UserId);
        return new JsonResult(new { success = true, count });
    }

    public class WorkflowInput
    {
        public int    Id              { get; set; } = 0;
        public string Title           { get; set; } = "";
        public string GoalDescription { get; set; } = "";
        public bool   IsEnabled       { get; set; } = true;
        public string StepsJson       { get; set; } = "[]";
    }

    public class StepInput
    {
        public string   Name         { get; set; } = "";
        public string   Instructions { get; set; } = "";
        public StepType StepType     { get; set; } = StepType.Execute;
        public string?  WebhookUrl         { get; set; }
        public string?  WebhookPlatform    { get; set; }
        public int?     CalledWorkflowId   { get; set; }
    }
}
