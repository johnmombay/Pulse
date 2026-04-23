using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Pulse.Infrastructure;

/// <summary>
/// Provides the current tenant context resolved from the authenticated user's claims.
/// Registered as a singleton — per-request state comes from <see cref="IHttpContextAccessor"/>,
/// and per-async-flow overrides (background jobs) are isolated via <see cref="AsyncLocal{T}"/>.
/// </summary>
public interface ITenantContext
{
    /// <summary>The current tenant's ID. Null for SuperAdmin accounts or unauthenticated requests.</summary>
    Guid? TenantId { get; }

    /// <summary>True when the current user has the SuperAdmin role.</summary>
    bool IsSuperAdmin { get; }

    /// <summary>
    /// Allows background jobs (Hangfire) to supply a TenantId outside of an HTTP context.
    /// Call this at the start of a background job before any DB work. The override is
    /// flowed via <see cref="AsyncLocal{T}"/> so it is isolated per async call chain.
    /// </summary>
    void SetTenantId(Guid? tenantId);
}

public class TenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly DeploymentOptions    _deployment;

    // AsyncLocal so that overrides set by background jobs are isolated per async flow
    // and do not leak across concurrent jobs sharing this singleton instance.
    private static readonly AsyncLocal<(bool Set, Guid? Value)> _override = new();

    /// <summary>
    /// Well-known singleton tenant ID used when running in SingleTenant mode.
    /// This row is seeded automatically at startup and never changes.
    /// </summary>
    public static readonly Guid SingleTenantId = new("00000000-0000-0000-0000-000000000001");

    public TenantContext(
        IHttpContextAccessor httpContextAccessor,
        IOptions<DeploymentOptions> deploymentOptions)
    {
        _httpContextAccessor = httpContextAccessor;
        _deployment          = deploymentOptions.Value;
    }

    public Guid? TenantId
    {
        get
        {
            // Background-job override always wins.
            var ovr = _override.Value;
            if (ovr.Set) return ovr.Value;

            // In single-tenant mode every request belongs to the one tenant.
            if (_deployment.IsSingleTenant)
                return SingleTenantId;

            // Multi-tenant: resolve from the authenticated user's claim.
            var claim = _httpContextAccessor.HttpContext?
                .User.FindFirstValue("tid");

            return claim is not null && Guid.TryParse(claim, out var id) ? id : null;
        }
    }

    public bool IsSuperAdmin =>
        _httpContextAccessor.HttpContext?
            .User.IsInRole("SuperAdmin") ?? false;

    public void SetTenantId(Guid? tenantId)
        => _override.Value = (true, tenantId);
}
