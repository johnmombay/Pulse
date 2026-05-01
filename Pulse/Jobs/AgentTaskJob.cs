using Pulse.Infrastructure;
using Pulse.Services;
using Hangfire;

namespace Pulse.Jobs;

/// <summary>
/// Hangfire background job that processes one agent turn, then fires a
/// <see cref="MemoryExtractionJob"/> to persist memorable facts from the exchange.
/// </summary>
public sealed class AgentTaskJob(
    ITenantContext tenantContext,
    AgentOrchestrationService orchestration,
    ILogger<AgentTaskJob> logger)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(
        Guid tenantId,
        string sessionId,
        string userMessage,
        string userId,
        IJobCancellationToken jobCancellationToken)
    {
        tenantContext.SetTenantId(tenantId);
        logger.LogInformation("AgentTaskJob starting for session {SessionId}", sessionId);

        var response = await orchestration.ExecuteAsync(
            sessionId, userMessage, userId, jobCancellationToken.ShutdownToken);

        if (!string.IsNullOrWhiteSpace(userId) && !string.IsNullOrWhiteSpace(response))
        {
            // Memory extraction
            BackgroundJob.Enqueue<MemoryExtractionJob>(j =>
                j.ExtractAsync(tenantId, userId, userMessage, response, null, JobCancellationToken.Null));

            // Self-learning: Chat domain reflection
            BackgroundJob.Enqueue<AgentReflectionJob>(j =>
                j.ReflectAsync(tenantId, userId, AgentDomain.Chat,
                    userMessage, string.Empty, response,
                    true, null, JobCancellationToken.Null));
        }
    }
}
