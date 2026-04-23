using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Pulse.Data;
using Pulse.Data.Entities;
using Pulse.Models;
using Pulse.Services;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Pages.Billing;

[Authorize]
public class PayModel(
    UserManager<ApplicationUser> userManager,
    IInvoiceService invoiceService,
    ISubscriptionService subscriptionService,
    ApplicationDbContext db) : PageModel
{
    public Invoice             Invoice { get; set; } = null!;
    public PaymentGatewaySettings? Gateway { get; set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage  { get; set; }

    public async Task<IActionResult> OnGetAsync(int invoiceId)
    {
        var user = await userManager.GetUserAsync(User);
        if (user?.TenantId is null) return LocalRedirect("~/");

        var invoice = await invoiceService.GetByIdAsync(invoiceId, user.TenantId.Value);
        if (invoice is null) return NotFound();
        if (invoice.Status != InvoiceStatus.Unpaid)
            return RedirectToPage("/Billing/Invoices");

        Invoice = invoice;
        Gateway = await db.PaymentGatewaySettings.FirstOrDefaultAsync(g => g.IsActive);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int invoiceId)
    {
        var user = await userManager.GetUserAsync(User);
        if (user?.TenantId is null) return LocalRedirect("~/");

        var result = await subscriptionService.PayInvoiceAsync(invoiceId, user.TenantId.Value);

        if (!result.Success)
        {
            ErrorMessage = result.ErrorMessage;
            return RedirectToPage(new { invoiceId });
        }

        StatusMessage = "Payment successful! Your invoice has been marked as paid.";
        return RedirectToPage("/Billing/Invoices");
    }
}
