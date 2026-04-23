using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Pulse.Data;
using Pulse.Data.Entities;

namespace Pulse.Pages.Admin;

[Authorize(Policy = "SuperAdminOnly")]
public class TenantsModel : PageModel
{
    private readonly ApplicationDbContext _db;

    public TenantsModel(ApplicationDbContext db) => _db = db;

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
    }

    public class AssignPlanInput
    {
        public Guid TenantId          { get; set; }
        public int  SubscriptionPlanId { get; set; }
        public BillingCycle BillingCycle { get; set; }
    }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid id)
    {
        var tenant = await _db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == id);
        if (tenant is null) return NotFound();

        tenant.IsActive = !tenant.IsActive;
        await _db.SaveChangesAsync();
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

        // Enrich rows with subscription info after the projection
        Tenants = Tenants.Select(row =>
        {
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
                };
            return row;
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
