using Pulse.Services;
using Hangfire;

namespace Pulse.Jobs;

/// <summary>
/// Hangfire background job that processes one agent turn, then fires a
/// <see cref="MemoryExtractionJob"/> to persist memorable facts from the exchange.
/// </summary>
public sealed class AgentTaskJob(
    AgentOrchestrationService orchestration,
    ILogger<AgentTaskJob> logger)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(
        string sessionId,
        string userMessage,
        string userId,
        IJobCancellationToken jobCancellationToken)
    {
        logger.LogInformation("AgentTaskJob starting for session {SessionId}", sessionId);

        var response = await orchestration.ExecuteAsync(
            sessionId, userMessage, userId, jobCancellationToken.ShutdownToken);

        // Queue memory extraction in the background so it never delays the user
        if (!string.IsNullOrWhiteSpace(userId) && !string.IsNullOrWhiteSpace(response))
        {
            BackgroundJob.Enqueue<MemoryExtractionJob>(j =>
                j.ExtractAsync(userId, userMessage, response, JobCancellationToken.Null));
        }
    }
}
