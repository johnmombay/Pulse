using Hangfire;
using Microsoft.EntityFrameworkCore;
using Pulse.Data;
using Pulse.Data.Entities;

namespace Pulse.Jobs;

/// <summary>
/// Hangfire recurring job (runs daily). Finds subscriptions due for renewal
/// and delegates to <see cref="ISubscriptionService.ProcessRenewalAsync"/>.
/// Registered as a daily cron in Program.cs.
/// </summary>
public sealed class SubscriptionRenewalJob(
    IDbContextFactory<ApplicationDbContext> factory,
    Pulse.Services.ISubscriptionService subscriptionService,
    ILogger<SubscriptionRenewalJob> logger)
{
    [AutomaticRetry(Attempts = 2)]
    public async Task ExecuteAsync(IJobCancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken.ShutdownToken);

        var due = await db.TenantSubscriptions
            .Where(s => s.Status == SubscriptionStatus.Active
                     && s.NextRenewalDate.Date <= DateTime.UtcNow.Date)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken.ShutdownToken);

        logger.LogInformation("SubscriptionRenewalJob: {Count} subscription(s) due for renewal", due.Count);

        foreach (var id in due)
        {
            try
            {
                await subscriptionService.ProcessRenewalAsync(id, cancellationToken.ShutdownToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Renewal failed for subscription {Id}", id);
            }
        }
    }
}
