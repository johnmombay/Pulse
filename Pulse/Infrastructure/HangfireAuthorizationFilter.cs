using Hangfire.Dashboard;

namespace Pulse.Infrastructure;

/// <summary>Restricts the Hangfire dashboard to authenticated users.</summary>
public sealed class HangfireAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        return httpContext.User.IsInRole("Admin");
    }
}
