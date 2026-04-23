using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Pulse.Infrastructure;
using Pulse.Services;

namespace Pulse.Pages.Admin;

[Authorize(Policy = "SuperAdminOnly")]
public class UsageModel(
    LlmUsageService usageService,
    IOptions<DeploymentOptions> deployment) : PageModel
{
    private readonly DeploymentOptions _deployment = deployment.Value;
    [BindProperty(SupportsGet = true)]
    public int Months { get; set; } = 1;

    public IReadOnlyList<LlmUsageService.TenantTotal> ByTenant { get; private set; } = [];
    public IReadOnlyList<LlmUsageService.MonthlyTotal> Monthly { get; private set; } = [];

    public long GrandTotal      { get; private set; }
    public long SuperAdminTotal { get; private set; }
    public long TotalCalls      { get; private set; }

    public string MonthlySeriesJson { get; private set; } = "[]";

    public string WindowLabel => Months switch
    {
        1  => "this month",
        3  => "last 3 months",
        6  => "last 6 months",
        12 => "last 12 months",
        _  => $"last {Months} months",
    };

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (_deployment.IsSingleTenant) return NotFound();
        if (Months is not (1 or 3 or 6 or 12)) Months = 1;

        ByTenant         = await usageService.GetByTenantAsync(Months, ct);
        GrandTotal       = ByTenant.Sum(t => t.TotalTokens);
        TotalCalls       = ByTenant.Sum(t => t.Calls);
        SuperAdminTotal  = ByTenant.FirstOrDefault(t => t.TenantId == Guid.Empty)?.TotalTokens ?? 0;

        // Always chart the trailing 12 months regardless of the table window.
        Monthly = await usageService.GetMonthlyAsync(Guid.Empty, months: 12, ct);

        MonthlySeriesJson = JsonSerializer.Serialize(Monthly.Select(m => new
        {
            label      = $"{m.Year:0000}-{m.Month:00}",
            prompt     = m.PromptTokens,
            completion = m.CompletionTokens,
            total      = m.TotalTokens,
            calls      = m.Calls,
        }));
        return Page();
    }
}
