using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Pulse.Data.Entities;
using Pulse.Models;
using Pulse.Services;

namespace Pulse.Pages.Billing;

[Authorize]
public class InvoicesModel(
    UserManager<ApplicationUser> userManager,
    IInvoiceService invoiceService) : PageModel
{
    public List<Invoice> Invoices { get; set; } = [];

    [TempData] public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user?.TenantId is null) return LocalRedirect("~/");

        Invoices = await invoiceService.GetForTenantAsync(user.TenantId.Value);
        return Page();
    }
}
