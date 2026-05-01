using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Pulse.Models;
using Pulse.Services.Telegram;

namespace Pulse.Areas.Identity.Pages.Account.Manage;

[Authorize]
public class TelegramModel(
    UserManager<ApplicationUser> userManager,
    UserTelegramSettingsService telegramSettingsSvc,
    TelegramBotService telegramBotService) : PageModel
{
    [TempData] public string? StatusMessage { get; set; }
    public bool      IsPaired          { get; set; }
    public string?   PairCode          { get; set; }
    public DateTime? PairCodeExpiresAt { get; set; }
    public bool      HasBotToken       { get; set; }
    [BindProperty] public string? BotToken { get; set; }

    private async Task LoadAsync(ApplicationUser user)
    {
        var s = await telegramSettingsSvc.GetByUserIdAsync(user.Id);
        IsPaired          = s?.IsPaired ?? false;
        HasBotToken       = !string.IsNullOrWhiteSpace(s?.BotToken);
        PairCode          = s?.PairCode;
        PairCodeExpiresAt = s?.PairCodeExpiresAt;
        BotToken = s?.BotToken is { Length: > 10 } t
            ? t[..6] + new string('*', t.Length - 10) + t[^4..] : s?.BotToken;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return NotFound();
        await LoadAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveBotTokenAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return NotFound();
        if (string.IsNullOrWhiteSpace(BotToken))
        {
            ModelState.AddModelError(nameof(BotToken), "Bot token is required.");
            await LoadAsync(user);
            return Page();
        }
        var existing = await telegramSettingsSvc.GetByUserIdAsync(user.Id);
        if (existing?.BotToken is not null && BotToken.Contains('*'))
        {
            StatusMessage = "OK:Bot token unchanged.";
            return RedirectToPage();
        }
        var oldToken = existing?.BotToken;
        await telegramSettingsSvc.SaveBotTokenAsync(user.Id, user.TenantId ?? Guid.Empty, BotToken);
        await telegramBotService.EnsureUserBotStartedAsync(oldToken, BotToken);
        StatusMessage = "OK:Bot token saved. Generate a pair code and send it to your bot.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostGeneratePairCodeAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return NotFound();
        var existing = await telegramSettingsSvc.GetByUserIdAsync(user.Id);
        if (existing is null || string.IsNullOrWhiteSpace(existing.BotToken))
        {
            StatusMessage = "WARN:Save a bot token first.";
            return RedirectToPage();
        }
        var code = await telegramSettingsSvc.GeneratePairCodeAsync(user.Id);
        StatusMessage = "CODE:" + code;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUnpairAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return NotFound();
        await telegramSettingsSvc.UnpairAsync(user.Id);
        StatusMessage = "OK:Telegram account unpaired.";
        return RedirectToPage();
    }
}
