using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Pulse.Data;
using Pulse.Data.Entities;
using Pulse.Models;

namespace Pulse.Middleware;

/// <summary>
/// Redirects authenticated tenant users who have no active subscription
/// to the SelectPlan page so they can complete sign-up.
/// SuperAdmin users (TenantId == null) are always allowed through.
/// </summary>
public class SubscriptionGuardMiddleware
{
    private readonly RequestDelegate _next;

    private static readonly string[] _allowedPrefixes =
    [
        "/Identity/Account/SelectPlan",
        "/Identity/Account/Payment",
        "/Identity/Account/Logout",
        "/Identity/Account/Login",
        "/Identity/Account/Register",
        "/Identity/Account/SignupTenant",
        "/Identity/Account/ConfirmEmail",
        "/Identity/Account/RegisterConfirmation",
        "/Identity/Account/ForgotPassword",
        "/Identity/Account/ResetPassword",
        "/Billing",
        "/_blazor",
        "/_framework",
        "/favicon",
        "/css",
        "/js",
        "/lib",
        "/images",
        "/api",
    ];

    public SubscriptionGuardMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext ctx,
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext db)
    {
        if (ctx.User.Identity?.IsAuthenticated == true)
        {
            var path = ctx.Request.Path.Value ?? string.Empty;

            var isAllowed = _allowedPrefixes.Any(p =>
                path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

            if (!isAllowed)
            {
                var user = await userManager.GetUserAsync(ctx.User);

                // SuperAdmin has no TenantId — always allowed
                if (user?.TenantId is not null)
                {
                    var hasActive = await db.TenantSubscriptions
                        .AnyAsync(s => s.TenantId == user.TenantId &&
                                       s.Status   == SubscriptionStatus.Active);

                    if (!hasActive)
                    {
                        ctx.Response.Redirect("/Identity/Account/SelectPlan");
                        return;
                    }
                }
            }
        }

        await _next(ctx);
    }
}
