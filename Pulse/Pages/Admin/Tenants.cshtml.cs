using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pulse.Data;
using Pulse.Data.Entities;
using Pulse.Infrastructure;
using Pulse.Services;

namespace Pulse.Pages.Admin;

[Authorize(Policy = "SuperAdminOnly")]
public class TenantsModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly DeploymentOptions    _deployment;
    private readonly LlmSettingsService   _llmSettings;

    public TenantsModel(ApplicationDbContext db, IOptions<DeploymentOptions> deployment, LlmSettingsService llmSettings)
    {
        _db          = db;
        _deployment  = deployment.Value;
        _llmSettings = llmSettings;
    }

    public List<TenantRow> Tenants { get; set; } = [];
    public SelectList PlanOptions { get; set; } = new SelectList(Enumerable.Empty<object>());

    /// <summary>Plan pricing passed to the modal via JSON so JS can show live prices.</summary>
    public List<PlanOption> PlanOptionsList { get; set; } = [];

    [TempData] public string? StatusMessage { get; set; }

    [BindProperty] public AssignPlanInput AssignPlan { get; set; } = new();

    public class PlanOption
    {
        public int     Id           { get; init; }
        public string  Name         { get; init; } = "";
        public decimal MonthlyPrice { get; init; }
        public decimal AnnualPrice  { get; init; }
    }

    public class TenantRow
    {
        public Guid   Id              { get; init; }
        public string Name            { get; init; } = "";
        public string Slug            { get; init; } = "";
        public int    UserCount       { get; init; }
        public bool   IsActive        { get; init; }
        public string CreatedUtc      { get; init; } = "";
        public string? PlanName       { get; init; }
        public int?   CurrentPlanId   { get; init; }
        public string? SubscriptionStatus { get; init; }
        public string? BillingCycle   { get; init; }
        public bool   WebSearchEnabled { get; init; }
    }

    public class AssignPlanInput
    {
        public Guid TenantId          { get; set; }
        public int  SubscriptionPlanId { get; set; }
        public BillingCycle BillingCycle { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (_deployment.IsSingleTenant) return NotFound();
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid id)
    {
        var tenant = await _db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == id);
        if (tenant is null) return NotFound();

        tenant.IsActive = !tenant.IsActive;
        await _db.SaveChangesAsync();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleWebSearchAsync(Guid id)
    {
        var settings = await _llmSettings.GetAsync(id);
        var current  = settings.WebSearch ?? new();
        await _llmSettings.SaveWebSearchSettingsAsync(id, new Models.WebSearchSettings
        {
            IsEnabled  = !current.IsEnabled,
            MaxResults = current.MaxResults == 0 ? 5 : current.MaxResults,
        });
        StatusMessage = $"Web search {(!current.IsEnabled ? "enabled" : "disabled")} for tenant.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAssignPlanAsync()
    {
        if (!ModelState.IsValid) { await LoadAsync(); return Page(); }

        var plan = await _db.SubscriptionPlans.FindAsync(AssignPlan.SubscriptionPlanId);
        if (plan is null) return NotFound();

        // Cancel existing active subscription for this tenant
        var existing = await _db.TenantSubscriptions
            .Where(s => s.TenantId == AssignPlan.TenantId && s.Status == SubscriptionStatus.Active)
            .ToListAsync();

        foreach (var sub in existing)
        {
            sub.Status  = SubscriptionStatus.Cancelled;
            sub.EndDate = DateTime.UtcNow;
        }

        var nextRenewal = AssignPlan.BillingCycle == BillingCycle.Annual
            ? DateTime.UtcNow.AddYears(1)
            : DateTime.UtcNow.AddMonths(1);

        _db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId             = AssignPlan.TenantId,
            SubscriptionPlanId   = plan.Id,
            BillingCycle         = AssignPlan.BillingCycle,
            Status               = SubscriptionStatus.Active,
            StartDate            = DateTime.UtcNow,
            NextRenewalDate      = nextRenewal,
            // Snapshot limits so future plan edits don't affect this subscription
            SnapshotMaxUsers     = plan.MaxUsersPerTenant,
            SnapshotMaxDatabases = plan.MaxDatabases,
            SnapshotMaxAgents    = plan.MaxAgents,
            SnapshotMonthlyUsage = plan.MonthlyUsageLimitUnits,
        });

        await _db.SaveChangesAsync();

        StatusMessage = $"Plan \"{plan.Name}\" assigned.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCancelSubscriptionAsync(Guid tenantId)
    {
        var active = await _db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId && s.Status == SubscriptionStatus.Active)
            .ToListAsync();

        foreach (var sub in active)
        {
            sub.Status  = SubscriptionStatus.Cancelled;
            sub.EndDate = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        StatusMessage = "Subscription cancelled.";
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        // Active subscription per tenant (at most one)
        var activeSubs = await _db.TenantSubscriptions
            .Include(s => s.SubscriptionPlan)
            .Where(s => s.Status == SubscriptionStatus.Active)
            .ToListAsync();

        var subsByTenant = activeSubs.ToDictionary(s => s.TenantId);

        Tenants = await _db.Tenants
            .IgnoreQueryFilters()
            .OrderBy(t => t.Name)
            .Select(t => new TenantRow
            {
                Id        = t.Id,
                Name      = t.Name,
                Slug      = t.Slug,
                UserCount = t.Users.Count,
                IsActive  = t.IsActive,
                CreatedUtc = t.CreatedUtc.ToString("yyyy-MM-dd"),
            })
            .ToListAsync();

        // Load web search settings per tenant
        var webSearchByTenant = new Dictionary<Guid, bool>();
        foreach (var t in Tenants)
        {
            var s = await _llmSettings.GetAsync(t.Id);
            webSearchByTenant[t.Id] = s.WebSearch?.IsEnabled == true;
        }

        // Enrich rows with subscription info and web search flag
        Tenants = Tenants.Select(row =>
        {
            webSearchByTenant.TryGetValue(row.Id, out var wsEnabled);
            if (subsByTenant.TryGetValue(row.Id, out var sub))
                return new TenantRow
                {
                    Id                 = row.Id,
                    Name               = row.Name,
                    Slug               = row.Slug,
                    UserCount          = row.UserCount,
                    IsActive           = row.IsActive,
                    CreatedUtc         = row.CreatedUtc,
                    PlanName           = sub.SubscriptionPlan.Name,
                    CurrentPlanId      = sub.SubscriptionPlanId,
                    SubscriptionStatus = sub.Status.ToString(),
                    BillingCycle       = sub.BillingCycle.ToString(),
                    WebSearchEnabled   = wsEnabled,
                };
            return new TenantRow
            {
                Id              = row.Id,
                Name            = row.Name,
                Slug            = row.Slug,
                UserCount       = row.UserCount,
                IsActive        = row.IsActive,
                CreatedUtc      = row.CreatedUtc,
                WebSearchEnabled = wsEnabled,
            };
        }).ToList();

        var plans = await _db.SubscriptionPlans
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .ToListAsync();

        PlanOptions = new SelectList(plans, nameof(SubscriptionPlan.Id), nameof(SubscriptionPlan.Name));

        PlanOptionsList = plans.Select(p => new PlanOption
        {
            Id           = p.Id,
            Name         = p.Name,
            MonthlyPrice = p.MonthlyPrice,
            AnnualPrice  = p.AnnualPrice,
        }).ToList();
    }
}
