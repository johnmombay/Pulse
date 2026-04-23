using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Pulse.Data;
using Pulse.Data.Entities;
using Pulse.Models;

namespace Pulse.Areas.Identity.Pages.Account;

[Authorize]
public class SelectPlanModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;

    public SelectPlanModel(ApplicationDbContext db, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    public List<SubscriptionPlan> Plans { get; set; } = [];

    [BindProperty] public int    SelectedPlanId { get; set; }
    [BindProperty] public string BillingCycle   { get; set; } = "Monthly";

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user?.TenantId is null) return LocalRedirect("~/");

        // Already active — skip
        var hasActive = await _db.TenantSubscriptions
            .AnyAsync(s => s.TenantId == user.TenantId &&
                           s.Status   == SubscriptionStatus.Active);
        if (hasActive) return LocalRedirect("~/");

        Plans = await _db.SubscriptionPlans
            .Where(p => p.IsActive)
            .OrderBy(p => p.MonthlyPrice)
            .ToListAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user?.TenantId is null) return LocalRedirect("~/");

        var plan = await _db.SubscriptionPlans.FindAsync(SelectedPlanId);
        if (plan is null || !plan.IsActive)
        {
            ModelState.AddModelError(string.Empty, "Selected plan is not available.");
            Plans = await _db.SubscriptionPlans.Where(p => p.IsActive)
                                                .OrderBy(p => p.MonthlyPrice)
                                                .ToListAsync();
            return Page();
        }

        var cycle = BillingCycle == "Annual"
            ? Data.Entities.BillingCycle.Annual
            : Data.Entities.BillingCycle.Monthly;

        var price = cycle == Data.Entities.BillingCycle.Annual
            ? plan.AnnualPrice
            : plan.MonthlyPrice;

        var isFree = price == 0m;

        // Remove any previous Pending subscription (e.g. user went back)
        var existing = await _db.TenantSubscriptions
            .Where(s => s.TenantId == user.TenantId.Value &&
                        s.Status   == SubscriptionStatus.Pending)
            .ToListAsync();
        _db.TenantSubscriptions.RemoveRange(existing);

        var subscription = new TenantSubscription
        {
            TenantId             = user.TenantId.Value,
            SubscriptionPlanId   = plan.Id,
            BillingCycle         = cycle,
            Status               = isFree ? SubscriptionStatus.Active : SubscriptionStatus.Pending,
            StartDate            = DateTime.UtcNow,
            SnapshotMaxUsers     = plan.MaxUsersPerTenant,
            SnapshotMaxDatabases = plan.MaxDatabases,
            SnapshotMaxAgents    = plan.MaxAgents,
            SnapshotMonthlyUsage = plan.MonthlyUsageLimitUnits,
        };
        _db.TenantSubscriptions.Add(subscription);

        if (isFree)
        {
            var tenant = await _db.Tenants.FindAsync(user.TenantId.Value);
            if (tenant is not null) tenant.IsActive = true;
        }

        await _db.SaveChangesAsync();

        if (isFree)
            return LocalRedirect("~/");

        return RedirectToPage("Payment", new { subscriptionId = subscription.Id });
    }
}
