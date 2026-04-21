using Pulse.Infrastructure;
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
    ITenantContext tenantContext,
    IServiceScopeFactory scopeFactory,
    ILogger<MemoryExtractionJob> logger)
{
    [AutomaticRetry(Attempts = 1)]
    public async Task ExtractAsync(
        Guid tenantId,
        string userId,
        string userMessage,
        string assistantResponse,
        string? agentDefinitionId,
        IJobCancellationToken jobCt)
    {
        tenantContext.SetTenantId(tenantId);
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(assistantResponse))
            return;

        logger.LogDebug("MemoryExtractionJob: extracting for user {UserId} agent {AgentId}",
            userId, agentDefinitionId ?? "global");

        // MemoryService depends on DbContext which is scoped — create a scope explicitly
        await using var scope = scopeFactory.CreateAsyncScope();
        var memoryService     = scope.ServiceProvider.GetRequiredService<MemoryService>();

        await memoryService.ExtractFromExchangeAsync(
            userId, userMessage, assistantResponse, agentDefinitionId, jobCt.ShutdownToken);
    }
}
