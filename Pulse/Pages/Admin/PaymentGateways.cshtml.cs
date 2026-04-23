using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Pulse.Data.Entities;
using Pulse.Services;

namespace Pulse.Pages.Admin;

[Authorize(Policy = "SuperAdminOnly")]
public class PaymentGatewaysModel(PaymentGatewayService gatewayService) : PageModel
{
    public List<PaymentGatewaySettings> Gateways { get; set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    [BindProperty]
    public PaymentGatewaySettings Settings { get; set; } = new();

    public async Task OnGetAsync()
    {
        Gateways = await gatewayService.GetAllAsync();
    }

    public async Task<IActionResult> OnPostActivateAsync(PaymentGatewayProvider provider)
    {
        await gatewayService.ActivateAsync(provider);
        StatusMessage = $"{provider} is now the active payment gateway.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeactivateAsync()
    {
        await gatewayService.DeactivateAllAsync();
        StatusMessage = "All payment gateways have been deactivated.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        await gatewayService.SaveAsync(Settings);
        StatusMessage = $"{Settings.Provider} credentials saved.";
        return RedirectToPage();
    }
}
