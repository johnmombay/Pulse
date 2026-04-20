using Pulse.Infrastructure;
using Pulse.Services;
using Hangfire;

namespace Pulse.Jobs;

public sealed class WorkflowJob(
    ITenantContext tenantContext,
    IServiceScopeFactory scopeFactory,
    ILogger<WorkflowJob> logger)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(Guid tenantId, int workflowId, string userId, IJobCancellationToken jobCt)
    {
        tenantContext.SetTenantId(tenantId);
        logger.LogInformation("WorkflowJob starting for workflow {WorkflowId}", workflowId);
        await using var scope = scopeFactory.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<WorkflowRunner>();
        await runner.ExecuteAsync(workflowId, userId, jobCt.ShutdownToken);
    }
}
