using Pulse.Infrastructure;
using Pulse.Services;
using Hangfire;

namespace Pulse.Jobs;

/// <summary>
/// Hangfire background job that runs the full Reflection Loop for a completed
/// agent execution and persists the learned lesson into the tenant's lesson store.
///
/// Closed-loop steps performed:
///   1. EvaluateAsync  — LLM grades output against goal (0.0–1.0)
///   2. ExtractAsync   — distils lesson text and persists it (with dedup)
///
/// The persisted lesson is then retrieved by GetRelevantLessonsAsync on future
/// executions (Memory Loop) so the agent self-improves over time.
/// </summary>
public sealed class AgentReflectionJob(
    ITenantContext tenantContext,
    IServiceScopeFactory scopeFactory,
    ILogger<AgentReflectionJob> logger)
{
    [AutomaticRetry(Attempts = 1)]
    public async Task ReflectAsync(
        Guid   tenantId,
        string userId,
        string domain,
        string goal,
        string actionTaken,
        string outcome,
        bool   executionSucceeded,
        string? agentDefinitionId,
        IJobCancellationToken jobCt)
    {
        tenantContext.SetTenantId(tenantId);

        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(outcome))
            return;

        logger.LogDebug(
            "AgentReflectionJob: reflecting on domain={Domain} agent={AgentId} tenant={TenantId}",
            domain, agentDefinitionId ?? "global", tenantId);

        await using var scope      = scopeFactory.CreateAsyncScope();
        var reflectionService      = scope.ServiceProvider.GetRequiredService<AgentReflectionService>();

        // ── Reflect ───────────────────────────────────────────────────────────
        var (score, critique) = await reflectionService.EvaluateAsync(
            goal, outcome, executionSucceeded, jobCt.ShutdownToken);

        logger.LogInformation(
            "AgentReflectionJob: domain={Domain} score={Score:F2} critique={Critique}",
            domain, score, critique);

        // ── Learn ─────────────────────────────────────────────────────────────
        await reflectionService.ExtractAsync(
            userId, domain, goal, actionTaken, outcome,
            score, critique, agentDefinitionId, jobCt.ShutdownToken);
    }
}
