using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pulse.Data;
using Pulse.Data.Entities;
using Pulse.Infrastructure;

namespace Pulse.Services;

/// <summary>
/// Ensures the well-known singleton tenant row exists when running in SingleTenant mode.
/// Called from Program.cs during startup, after migrations have run.
/// </summary>
public static class SingleTenantSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        var deployment = services.GetRequiredService<IOptions<DeploymentOptions>>().Value;
        if (!deployment.IsSingleTenant) return;

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var ctx = await db.CreateDbContextAsync();

        var exists = await ctx.Tenants
            .AnyAsync(t => t.Id == TenantContext.SingleTenantId);

        if (!exists)
        {
            ctx.Tenants.Add(new Tenant
            {
                Id       = TenantContext.SingleTenantId,
                Name     = "Default",
                Slug     = "default",
                IsActive = true
            });
            await ctx.SaveChangesAsync();
        }
    }
}
