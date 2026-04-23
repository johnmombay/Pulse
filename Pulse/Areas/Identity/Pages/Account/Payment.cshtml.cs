using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Pulse.Data;
using Pulse.Data.Entities;
using Pulse.Models;
using Pulse.Services;

namespace Pulse.Areas.Identity.Pages.Account;

[Authorize]
public class PaymentModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ISubscriptionService _subscriptionService;

    public PaymentModel(
        ApplicationDbContext db,
        UserManager<ApplicationUser> userManager,
        ISubscriptionService subscriptionService)
    {
        _db = db;
        _userManager = userManager;
        _subscriptionService = subscriptionService;
    }

    public TenantSubscription      Subscription { get; set; } = null!;
    public SubscriptionPlan        Plan         { get; set; } = null!;
    public PaymentGatewaySettings? Gateway      { get; set; }
    public decimal                 AmountDue    { get; set; }

    [TempData] public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int subscriptionId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user?.TenantId is null) return LocalRedirect("~/");

        var sub = await _db.TenantSubscriptions
            .Include(s => s.SubscriptionPlan)
            .FirstOrDefaultAsync(s => s.Id       == subscriptionId &&
                                      s.TenantId == user.TenantId);

        if (sub is null) return NotFound();

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

    public async Task<IActionResult> OnPostConfirmAsync(int subscriptionId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user?.TenantId is null) return LocalRedirect("~/");

        var result = await _subscriptionService.ActivateAsync(subscriptionId, user.Id);

        if (!result.Success)
        {
            ErrorMessage = result.ErrorMessage;
            return RedirectToPage("SelectPlan");
        }

        return LocalRedirect("~/");
    }
}

