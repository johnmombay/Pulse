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
public class PaymentModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;

    public PaymentModel(ApplicationDbContext db, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    public TenantSubscription      Subscription { get; set; } = null!;
    public SubscriptionPlan        Plan         { get; set; } = null!;
    public PaymentGatewaySettings? Gateway      { get; set; }
    public decimal                 AmountDue    { get; set; }

    public async Task<IActionResult> OnGetAsync(int subscriptionId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user?.TenantId is null) return LocalRedirect("~/");

        var sub = await _db.TenantSubscriptions
            .Include(s => s.SubscriptionPlan)
            .FirstOrDefaultAsync(s => s.Id       == subscriptionId &&
                                      s.TenantId == user.TenantId);

        if (sub is null) return NotFound();

        // Already active — no need to be here
        if (sub.Status == SubscriptionStatus.Active)
            return LocalRedirect("~/");

        Subscription = sub;
        Plan         = sub.SubscriptionPlan;
        AmountDue    = sub.BillingCycle == BillingCycle.Annual
                           ? Plan.AnnualPrice
                           : Plan.MonthlyPrice;

        Gateway = await _db.PaymentGatewaySettings
            .FirstOrDefaultAsync(g => g.IsActive);

        return Page();
    }

    /// <summary>
    /// Confirms a completed payment and activates the tenant.
    /// Replace the stub body with real gateway-side payment verification before going live.
    /// </summary>
    public async Task<IActionResult> OnPostConfirmAsync(int subscriptionId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user?.TenantId is null) return LocalRedirect("~/");

        var sub = await _db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.Id       == subscriptionId &&
                                      s.TenantId == user.TenantId);

        if (sub is null) return NotFound();

        // TODO: verify payment with your gateway SDK here before activating
        sub.Status = SubscriptionStatus.Active;

        var tenant = await _db.Tenants.FindAsync(user.TenantId.Value);
        if (tenant is not null) tenant.IsActive = true;

        await _db.SaveChangesAsync();

        return LocalRedirect("~/");
    }
}
