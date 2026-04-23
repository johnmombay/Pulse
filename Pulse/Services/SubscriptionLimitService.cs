using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Pulse.Data;
using Pulse.Data.Entities;
using Pulse.Models;

namespace Pulse.Services;

/// <inheritdoc cref="ISubscriptionLimitService"/>
public sealed class SubscriptionLimitService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    UserManager<ApplicationUser> userManager,
    LlmUsageService usageService) : ISubscriptionLimitService
{
    public async Task<bool> CanAddUserAsync(Guid tenantId, CancellationToken ct = default)
    {
        var limits = await GetLimitsAsync(tenantId, ct);
        return limits?.CanAddUser ?? true;
    }

    public async Task<bool> CanAddAgentAsync(Guid tenantId, CancellationToken ct = default)
    {
        var limits = await GetLimitsAsync(tenantId, ct);
        return limits?.CanAddAgent ?? true;
    }

    public async Task<bool> CanAddDatabaseAsync(Guid tenantId, CancellationToken ct = default)
    {
        var limits = await GetLimitsAsync(tenantId, ct);
        return limits?.CanAddDatabase ?? true;
    }

    public async Task<bool> IsWithinUsageLimitAsync(Guid tenantId, CancellationToken ct = default)
    {
        var limits = await GetLimitsAsync(tenantId, ct);
        return limits?.WithinUsage ?? true;
    }

    public async Task<SubscriptionLimits?> GetLimitsAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Resolve the active subscription for this tenant.
        var sub = await db.TenantSubscriptions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId
                     && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trial))
            .OrderByDescending(s => s.StartDate)
            .FirstOrDefaultAsync(ct);

        // No active subscription — let the caller handle (SubscriptionGuardMiddleware
        // already blocks navigation, so this is a secondary check).
        if (sub is null) return null;

        // Current user count for this tenant.
        var currentUsers = await userManager.Users
            .CountAsync(u => u.TenantId == tenantId, ct);

        // Current agent count.
        var currentAgents = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .CountAsync(a => a.TenantId == tenantId, ct);

        // Current database + flat-file source count (both consume the "databases" limit).
        var currentDbs = await db.DatabaseConnections
            .IgnoreQueryFilters()
            .CountAsync(d => d.TenantId == tenantId, ct);

        var currentFlatFiles = await db.FlatFileSources
            .IgnoreQueryFilters()
            .CountAsync(f => f.TenantId == tenantId, ct);

        var currentDatabases = currentDbs + currentFlatFiles;

        // Current monthly LLM usage (total tokens).
        var usage = await usageService.GetCurrentMonthAsync(tenantId, ct);
        var currentUsage = usage.TotalTokens;

        return new SubscriptionLimits(
            MaxUsers:           sub.SnapshotMaxUsers,
            MaxAgents:          sub.SnapshotMaxAgents,
            MaxDatabases:       sub.SnapshotMaxDatabases,
            MonthlyUsageUnits:  sub.SnapshotMonthlyUsage,
            CurrentUsers:       currentUsers,
            CurrentAgents:      currentAgents,
            CurrentDatabases:   currentDatabases,
            CurrentMonthlyUsage: currentUsage);
    }
}
