using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Pulse.Data;
using Pulse.Infrastructure;
using Pulse.Services;

namespace Pulse.Pages;

[Authorize]
public class UsageModel(
    LlmUsageService usageService,
    ITenantContext tenantContext,
    IDbContextFactory<ApplicationDbContext> dbFactory) : PageModel
{
    public LlmUsageService.MonthlyTotal CurrentMonth { get; private set; } = new(0, 0, 0, 0, 0, 0);
    public IReadOnlyList<LlmUsageService.MonthlyTotal> Monthly { get; private set; } = [];
    public IReadOnlyList<LlmUsageService.GroupTotal>   ByModel { get; private set; } = [];
    public IReadOnlyList<LlmUsageService.GroupTotal>   ByAgent { get; private set; } = [];

    public string TenantDisplayName { get; private set; } = "this tenant";
    public string MonthlySeriesJson { get; private set; } = "[]";
    public bool   ShowByModel       { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var tenantId = tenantContext.TenantId ?? Guid.Empty;
        ShowByModel  = User.IsInRole("SuperAdmin");

        CurrentMonth = await usageService.GetCurrentMonthAsync(tenantId, ct);
        Monthly      = await usageService.GetMonthlyAsync(tenantId, months: 12, ct);
        ByAgent      = await usageService.GetGroupedAsync(tenantId, "agent", months: 1, ct);
        ByModel      = ShowByModel
            ? await usageService.GetGroupedAsync(tenantId, "model", months: 1, ct)
            : [];

        if (tenantId != Guid.Empty)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var t = await db.Tenants.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == tenantId, ct);
            if (t is not null) TenantDisplayName = t.Name;
        }
        else if (User.IsInRole("SuperAdmin"))
        {
            TenantDisplayName = "SuperAdmin (no tenant)";
        }

        MonthlySeriesJson = JsonSerializer.Serialize(Monthly.Select(m => new
        {
            label      = $"{m.Year:0000}-{m.Month:00}",
            prompt     = m.PromptTokens,
            completion = m.CompletionTokens,
            total      = m.TotalTokens,
            calls      = m.Calls,
        }));
    }
}
