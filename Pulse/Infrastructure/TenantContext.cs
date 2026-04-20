using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Pulse.Infrastructure;

/// <summary>
/// Provides the current tenant context resolved from the authenticated user's claims.
/// Scoped — one instance per HTTP request.
/// </summary>
public interface ITenantContext
{
    /// <summary>The current tenant's ID. Null for SuperAdmin accounts or unauthenticated requests.</summary>
    Guid? TenantId { get; }

    /// <summary>True when the current user has the SuperAdmin role.</summary>
    bool IsSuperAdmin { get; }

    /// <summary>
    /// Allows background jobs (Hangfire) to supply a TenantId outside of an HTTP context.
    /// Call this at the start of a background job before any DB work.
    /// </summary>
    void SetTenantId(Guid? tenantId);
}

public class TenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private Guid? _overrideTenantId;
    private bool _overrideSet;

    public TenantContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? TenantId
    {
        get
        {
            // Background job override takes precedence
            if (_overrideSet) return _overrideTenantId;

            var claim = _httpContextAccessor.HttpContext?
                .User.FindFirstValue("tid");

            return claim is not null && Guid.TryParse(claim, out var id) ? id : null;
        }
    }

    public bool IsSuperAdmin =>
        _httpContextAccessor.HttpContext?
            .User.IsInRole("SuperAdmin") ?? false;

    public void SetTenantId(Guid? tenantId)
    {
        _overrideTenantId = tenantId;
        _overrideSet = true;
    }
}
