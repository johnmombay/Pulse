namespace Pulse.Services;

/// <summary>
/// Checks whether a tenant is within the limits imposed by their active subscription plan.
/// All counts use snapshots stored on the <see cref="Data.Entities.TenantSubscription"/> row
/// so that retroactive plan edits never silently change existing tenants' entitlements.
/// </summary>
public interface ISubscriptionLimitService
{
    /// <summary>Returns true when the tenant can add at least one more user.</summary>
    Task<bool> CanAddUserAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Returns true when the tenant can add at least one more agent definition.</summary>
    Task<bool> CanAddAgentAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Returns true when the tenant can add at least one more database/data source.</summary>
    Task<bool> CanAddDatabaseAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Returns true when the tenant's LLM token usage for the current calendar month
    /// is within the plan's monthly usage limit.
    /// </summary>
    Task<bool> IsWithinUsageLimitAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Returns the active subscription limits for the tenant, or null when no active
    /// subscription exists (e.g. pending payment, cancelled).
    /// </summary>
    Task<SubscriptionLimits?> GetLimitsAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>Snapshot of the limits that apply to a tenant right now.</summary>
public record SubscriptionLimits(
    int MaxUsers,
    int MaxAgents,
    int MaxDatabases,
    int MonthlyUsageUnits,
    int CurrentUsers,
    int CurrentAgents,
    int CurrentDatabases,
    long CurrentMonthlyUsage)
{
    public bool CanAddUser     => MaxUsers     <= 0 || CurrentUsers     < MaxUsers;
    public bool CanAddAgent    => MaxAgents    <= 0 || CurrentAgents    < MaxAgents;
    public bool CanAddDatabase => MaxDatabases <= 0 || CurrentDatabases < MaxDatabases;
    public bool WithinUsage    => MonthlyUsageUnits <= 0 || CurrentMonthlyUsage < MonthlyUsageUnits;
}
