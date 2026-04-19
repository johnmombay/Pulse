using Pulse.Services;
using Hangfire;

namespace Pulse.Jobs;

/// <summary>
/// Hangfire background job that extracts memorable facts from a completed
/// agent exchange and stores them in the user's persistent memory.
/// Runs silently after every agent response; failures are logged but never
/// bubble up to the user.
/// </summary>
public sealed class MemoryExtractionJob(
    IServiceScopeFactory scopeFactory,
    ILogger<MemoryExtractionJob> logger)
{
    [AutomaticRetry(Attempts = 1)]
    public async Task ExtractAsync(
        string userId,
        string userMessage,
        string assistantResponse,
        IJobCancellationToken jobCt)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(assistantResponse))
            return;

        logger.LogDebug("MemoryExtractionJob: extracting for user {UserId}", userId);

        // MemoryService depends on DbContext which is scoped — create a scope explicitly
        await using var scope = scopeFactory.CreateAsyncScope();
        var memoryService     = scope.ServiceProvider.GetRequiredService<MemoryService>();

        await memoryService.ExtractFromExchangeAsync(
            userId, userMessage, assistantResponse, jobCt.ShutdownToken);
    }
}
