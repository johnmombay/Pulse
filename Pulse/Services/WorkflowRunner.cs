using Pulse.Models;
using System.Text;
using System.Text.Json;

namespace Pulse.Services;

/// <summary>
/// Executes a WorkflowDefinition step-by-step.
/// Each step receives the accumulated output of every prior step as context.
/// Decision steps emit [CONTINUE] or [STOP] to control the chain.
/// </summary>
public sealed class WorkflowRunner(
    ScheduledAgentRunner agentRunner,
    WorkflowService workflowService,
    IHttpClientFactory httpClientFactory,
    ILogger<WorkflowRunner> logger)
{
    public async Task<WorkflowRun> ExecuteAsync(int workflowId, string userId, CancellationToken ct = default, int depth = 0)
    {
        var workflow = await workflowService.GetAsync(workflowId, userId, ct)
            ?? throw new KeyNotFoundException($"Workflow {workflowId} not found.");

        var run = await workflowService.CreateRunAsync(workflow, userId, ct);
        logger.LogInformation("WorkflowRun {RunId} started � {Title} ({Steps} steps)", run.Id, workflow.Title, workflow.Steps.Count);

        var context = new StringBuilder();

        foreach (var step in workflow.Steps.OrderBy(s => s.Order))
        {
            ct.ThrowIfCancellationRequested();
            logger.LogInformation("WorkflowRun {RunId} � step {Order}: {Name} ({Type})", run.Id, step.Order, step.Name, step.StepType);

            var prompt = BuildPrompt(workflow.GoalDescription, context.ToString(), step);
            string output;
            bool   success;

            if (step.StepType == StepType.Webhook)
            {
                (output, success) = await CallWebhookAsync(run.Id, step, workflow.GoalDescription, context.ToString(), ct);
            }
            else if (step.StepType == StepType.CallWorkflow)
            {
                (output, success) = await CallSubWorkflowAsync(run.Id, step, userId, ct, depth);
            }
            else
            {
                (output, success) = await agentRunner.RunAsync(prompt, ct);
            }

            string? decision   = null;
            bool    shouldStop = false;

            if (step.StepType == StepType.Decision)
            {
                var upper = output.ToUpperInvariant();
                if (upper.Contains("[STOP]"))     { decision = "STOP";     shouldStop = true; }
                else                              { decision = "CONTINUE"; }
                logger.LogInformation("WorkflowRun {RunId} � decision: {Decision}", run.Id, decision);
            }

            await workflowService.AddStepRunAsync(new WorkflowStepRun
            {
                WorkflowRunId = run.Id,
                StepOrder     = step.Order,
                StepName      = step.Name,
                StepType      = step.StepType,
                Output        = output,
                IsSuccess     = success,
                Decision      = decision,
                RunAt         = DateTime.UtcNow,
            }, ct);

            context.AppendLine($"\n### Step {step.Order}: {step.Name}");
            context.AppendLine(output);

            if (!success)      { run.Status = "failed";  run.StopReason = $"Step {step.Order} ({step.Name}) failed."; break; }
            if (shouldStop)    { run.Status = "stopped"; run.StopReason = $"Agent chose to stop after step {step.Order} ({step.Name})."; break; }
        }

        if (run.Status == "running") run.Status = "completed";
        run.FinishedAt = DateTime.UtcNow;
        await workflowService.UpdateRunAsync(run, ct);

        logger.LogInformation("WorkflowRun {RunId} finished � status={Status}", run.Id, run.Status);
        return run;
    }

    private async Task<(string output, bool success)> CallSubWorkflowAsync(
        int parentRunId, WorkflowStep step, string userId, CancellationToken ct, int depth)
    {
        if (step.CalledWorkflowId is null)
            return ("No workflow selected for this Call Workflow step.", false);

        if (depth >= 4)
            return ("Maximum chain depth (5) reached � cannot call further sub-workflows.", false);

        try
        {
            logger.LogInformation(
                "WorkflowRun {RunId} ? calling sub-workflow {SubId} at depth {Depth}",
                parentRunId, step.CalledWorkflowId.Value, depth + 1);

            var subRun = await ExecuteAsync(step.CalledWorkflowId.Value, userId, ct, depth + 1);

            var summary = subRun.StepRuns.Count > 0
                ? string.Join("\n\n", subRun.StepRuns
                    .OrderBy(s => s.StepOrder)
                    .Select(s => $"### {s.StepName}\n{s.Output}"))
                : "(no steps ran)";

            var output  = $"Sub-workflow \"{subRun.WorkflowTitle}\" finished � status: {subRun.Status}.\n\n{summary}";
            var success = subRun.Status is "completed" or "stopped";
            return (output, success);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sub-workflow call failed at step {StepOrder} in run {RunId}",
                step.Order, parentRunId);
            return ($"Failed to call sub-workflow: {ex.Message}", false);
        }
    }

    private async Task<(string output, bool success)> CallWebhookAsync(
        int runId, WorkflowStep step, string goal, string context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(step.WebhookUrl))
            return ("Webhook URL is not configured for this step.", false);

        try
        {
            var client  = httpClientFactory.CreateClient("N8n");
            var payload = JsonSerializer.Serialize(new
            {
                runId,
                stepOrder    = step.Order,
                stepName     = step.Name,
                goal,
                context,
                instructions = step.Instructions,
            });

            using var response = await client.PostAsync(
                step.WebhookUrl,
                new StringContent(payload, Encoding.UTF8, "application/json"),
                ct);

            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return ($"n8n webhook returned HTTP {(int)response.StatusCode}: {body}", false);

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("output", out var prop))
                    return (prop.GetString() ?? body, true);
            }
            catch { /* fall through: use raw body */ }

            return (string.IsNullOrWhiteSpace(body) ? "Webhook executed successfully." : body, true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Webhook call failed for step {StepOrder} in run {RunId}", step.Order, runId);
            return ($"Webhook call failed: {ex.Message}", false);
        }
    }

    private static string BuildPrompt(string goal, string accumulatedContext, WorkflowStep step)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Overall Goal");
        sb.AppendLine(goal);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(accumulatedContext))
        {
            sb.AppendLine("## Context from Previous Steps");
            sb.AppendLine(accumulatedContext.Trim());
            sb.AppendLine();
        }

        if (step.StepType == StepType.Decision)
        {
            sb.AppendLine($"## Decision Required � Step {step.Order}: {step.Name}");
            sb.AppendLine(step.Instructions);
            sb.AppendLine();
            sb.AppendLine("Based on the overall goal and all previous step results, decide whether to continue or stop.");
            sb.AppendLine("Provide your reasoning, then end your response with EXACTLY ONE of:");
            sb.AppendLine("[CONTINUE] � proceed to the next step");
            sb.AppendLine("[STOP] � goal achieved or continuing adds no value");
        }
        else
        {
            sb.AppendLine($"## Current Step {step.Order}: {step.Name}");
            sb.AppendLine(step.Instructions);
            sb.AppendLine();
            sb.AppendLine("Complete this step thoroughly. Your output will be passed as context to all subsequent steps.");
        }

        return sb.ToString();
    }
}
