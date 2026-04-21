#nullable disable

using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Pulse.Data;
using Pulse.Models;

namespace Pulse.Areas.Identity.Pages.Account;

/// <summary>
/// Landing page for invited users. Validates the password-reset token issued
/// at invitation time, lets the invitee set their initial password, marks the
/// account email-confirmed, and signs them in.
/// </summary>
[AllowAnonymous]
public class AcceptInvitationModel : PageModel
{
    private readonly UserManager<ApplicationUser>   _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ApplicationDbContext           _db;
    private readonly ILogger<AcceptInvitationModel> _logger;

    public AcceptInvitationModel(
        UserManager<ApplicationUser>   userManager,
        SignInManager<ApplicationUser> signInManager,
        ApplicationDbContext           db,
        ILogger<AcceptInvitationModel> logger)
    {
        _userManager   = userManager;
        _signInManager = signInManager;
        _db            = db;
        _logger        = logger;
    }

    [BindProperty(SupportsGet = true)] public string UserId { get; set; }
    [BindProperty(SupportsGet = true)] public string Code   { get; set; }

    [BindProperty] public InputModel Input { get; set; } = new();

    // Display fields populated on GET / POST (no DB hit on POST until validation passes).
    public bool   IsValidInvitation { get; private set; }
    public string Email             { get; private set; }
    public string InviteeFirstName  { get; private set; } = "there";
    public string TenantName        { get; private set; } = "your workspace";

    public class InputModel
    {
        [Required, StringLength(100, MinimumLength = 6,
            ErrorMessage = "Password must be at least {2} characters.")]
        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string Password { get; set; }

        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Confirm password")]
        [Compare("Password", ErrorMessage = "Passwords do not match.")]
        public string ConfirmPassword { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await PopulateAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await PopulateAsync();

        if (!IsValidInvitation)
            return Page();

        if (!ModelState.IsValid)
            return Page();

        var user = await _userManager.FindByIdAsync(UserId);
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, "This invitation is no longer valid.");
            IsValidInvitation = false;
            return Page();
        }

        // The invitation link carries a Base64Url-encoded password-reset token.
        string token;
        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(Code));
        }
        catch
        {
            ModelState.AddModelError(string.Empty, "This invitation link is malformed.");
            IsValidInvitation = false;
            return Page();
        }

        var resetResult = await _userManager.ResetPasswordAsync(user, token, Input.Password);
        if (!resetResult.Succeeded)
        {
            // Most common failure: the token has already been used or expired.
            foreach (var e in resetResult.Errors)
                ModelState.AddModelError(string.Empty, e.Description);
            return Page();
        }

        // Setting the password by way of the invite link implicitly proves the
        // invitee owns the inbox, so we can confirm the email at the same time.
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await _userManager.UpdateAsync(user);
        }

        // Auto sign-in and land on the dashboard.
        await _signInManager.SignInAsync(user, isPersistent: false);

        _logger.LogInformation(
            "Invitation accepted by {Email} (tenant {TenantId})",
            user.Email, user.TenantId);

        return LocalRedirect("~/");
    }

    private async Task PopulateAsync()
    {
        if (string.IsNullOrWhiteSpace(UserId) || string.IsNullOrWhiteSpace(Code))
        {
            IsValidInvitation = false;
            return;
        }

        var user = await _userManager.FindByIdAsync(UserId);
        if (user is null)
        {
            IsValidInvitation = false;
            return;
        }

        IsValidInvitation = true;
        Email             = user.Email;
        InviteeFirstName  = string.IsNullOrWhiteSpace(user.FirstName) ? "there" : user.FirstName;

        if (user.TenantId is Guid tid)
        {
            TenantName = await _db.Tenants.IgnoreQueryFilters()
                             .Where(t => t.Id == tid)
                             .Select(t => t.Name)
                             .FirstOrDefaultAsync()
                         ?? TenantName;
        }
    }
}
