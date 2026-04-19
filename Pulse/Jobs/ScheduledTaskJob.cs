using Pulse.Models;
using Pulse.Services;
using Hangfire;

namespace Pulse.Jobs;

/// <summary>
/// Hangfire job that executes a <see cref="ScheduledTask"/>.
/// Creates its own DI scope so it can resolve scoped/transient services safely.
/// </summary>
public sealed class ScheduledTaskJob(
    IServiceScopeFactory scopeFactory,
    ILogger<ScheduledTaskJob> logger)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(int taskId, IJobCancellationToken jobCt)
    {
        logger.LogInformation("ScheduledTaskJob starting for task {TaskId}", taskId);

        await using var scope    = scopeFactory.CreateAsyncScope();
        var sp                   = scope.ServiceProvider;
        var schedulerService     = sp.GetRequiredService<SchedulerService>();
        var agentRunner          = sp.GetRequiredService<ScheduledAgentRunner>();
        var agentMailService     = sp.GetRequiredService<AgentMailService>();
        var llmSettings          = sp.GetRequiredService<LlmSettingsService>();

        var task = await schedulerService.GetByIdAsync(taskId, jobCt.ShutdownToken);
        if (task is null)
        {
            logger.LogWarning("ScheduledTaskJob: task {TaskId} not found — removing job", taskId);
            SchedulerService.HangfireJobId(taskId);   // log only; job is already running
            return;
        }

        if (!task.IsEnabled)
        {
            logger.LogDebug("ScheduledTaskJob: task {TaskId} is disabled — skipping", taskId);
            return;
        }

        // For week/month-based tasks the recurring job fires more often than needed;
        // check the stored NextRunAt to avoid premature execution.
        if (task.FrequencyType is FrequencyType.Weeks or FrequencyType.Months)
        {
            if (task.NextRunAt.HasValue && DateTime.UtcNow < task.NextRunAt.Value)
            {
                logger.LogDebug(
                    "ScheduledTaskJob: task {TaskId} not due yet (next={Next:u})",
                    taskId, task.NextRunAt.Value);
                return;
            }
        }

        // ── Execute ───────────────────────────────────────────────────────────
        string result; bool success;

        if (task.WorkflowDefinitionId.HasValue)
        {
            var workflowRunner = sp.GetRequiredService<Pulse.Services.WorkflowRunner>();
            var run = await workflowRunner.ExecuteAsync(
                task.WorkflowDefinitionId.Value, task.UserId, jobCt.ShutdownToken);

            success = run.Status is "completed" or "stopped";
            result  = FormatWorkflowSummary(run);
        }
        else
        {
            (result, success) = await agentRunner.RunAsync(
                task.Instructions, jobCt.ShutdownToken);
        }

        // ── Deliver ───────────────────────────────────────────────────────────
        if (task.DeliveryType is DeliveryType.Dashboard or DeliveryType.Both)
        {
            await schedulerService.SaveResultAsync(new ScheduledTaskResult
            {
                ScheduledTaskId = task.Id,
                UserId          = task.UserId,
                TaskTitle       = task.Title,
                Output          = result,
                IsSuccess       = success,
                RunAt           = DateTime.UtcNow,
            }, jobCt.ShutdownToken);
        }

        if (task.DeliveryType is DeliveryType.Email or DeliveryType.Both)
        {
            var am = llmSettings.Get().AgentMail;
            if (am.IsEnabled &&
                !string.IsNullOrWhiteSpace(am.ApiKey) &&
                !string.IsNullOrWhiteSpace(task.DeliveryEmail))
            {
                try
                {
                    var subject = $"[Pulse] {task.Title} — " +
                                  (success ? "✅ Completed" : "❌ Error");
                    await agentMailService.SendEmailAsync(
                        am.DefaultInbox,
                        [task.DeliveryEmail],
                        subject,
                        result,
                        ct: jobCt.ShutdownToken);

                    logger.LogInformation(
                        "ScheduledTaskJob: email sent to {Email} for task {TaskId}",
                        task.DeliveryEmail, taskId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "ScheduledTaskJob: email delivery failed for task {TaskId}", taskId);
                }
            }
        }

        // ── Update status ─────────────────────────────────────────────────────
        await schedulerService.MarkLastRunAsync(
            taskId,
            success ? "success" : "error",
            success ? null : result,
            jobCt.ShutdownToken);

        logger.LogInformation(
            "ScheduledTaskJob completed for task {TaskId} — success={Success}", taskId, success);
    }

    private static string FormatWorkflowSummary(Pulse.Models.WorkflowRun run)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Workflow: {run.WorkflowTitle} — {run.Status.ToUpperInvariant()}");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(run.StopReason))
        {
            sb.AppendLine($"> {run.StopReason}");
            sb.AppendLine();
        }
        foreach (var s in run.StepRuns.OrderBy(x => x.StepOrder))
        {
            sb.AppendLine($"### Step {s.StepOrder}: {s.StepName}" +
                          (s.Decision is not null ? $" [{s.Decision}]" : ""));
            sb.AppendLine(s.Output);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
