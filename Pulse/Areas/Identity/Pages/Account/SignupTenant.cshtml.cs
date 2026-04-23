using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Pulse.Data;
using Pulse.Data.Entities;
using Pulse.Models;
using Pulse.Services;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.WebUtilities;

namespace Pulse.Areas.Identity.Pages.Account;

[AllowAnonymous]
public class SignupTenantModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IEmailSender _emailSender;
    private readonly ApplicationDbContext _db;
    private readonly LlmSettingsService _settingsService;

    public SignupTenantModel(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IEmailSender emailSender,
        ApplicationDbContext db,
        LlmSettingsService settingsService)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _emailSender = emailSender;
        _db = db;
        _settingsService = settingsService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; set; }

    public class InputModel
    {
        [Required, MaxLength(100)]
        [Display(Name = "Organisation name")]
        public string TenantName { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        [Display(Name = "First name")]
        public string FirstName { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        [Display(Name = "Last name")]
        public string LastName { get; set; } = string.Empty;

        [Required, EmailAddress]
        [Display(Name = "Email address")]
        public string Email { get; set; } = string.Empty;

        [Required, StringLength(100, MinimumLength = 6)]
        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string Password { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        [Display(Name = "Confirm password")]
        [Compare("Password", ErrorMessage = "Passwords do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public void OnGet(string? returnUrl = null)
    {
        ReturnUrl = returnUrl ?? Url.Content("~/");
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        returnUrl ??= Url.Content("~/");

        if (!ModelState.IsValid)
            return Page();

        await using var tx = await _db.Database.BeginTransactionAsync();

        try
        {
            // 1. Create tenant
            var slug = GenerateSlug(Input.TenantName);

            if (string.IsNullOrEmpty(slug))
            {
                ModelState.AddModelError("Input.TenantName",
                    "Organisation name must contain at least one letter or number.");
                return Page();
            }

            // Ensure slug uniqueness
            var slugBase = slug;
            int suffix = 1;
            while (await _db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Slug == slug))
                slug = $"{slugBase}-{suffix++}";

            var tenant = new Tenant
            {
                Name       = Input.TenantName.Trim(),
                Slug       = slug,
                CreatedUtc = DateTime.UtcNow,
                IsActive   = false,  // activated after subscription is confirmed
            };
            _db.Tenants.Add(tenant);
            await _db.SaveChangesAsync();

            // 2. Seed default AppSettings row for this tenant
            _db.AppSettings.Add(new AppSettingsEntity
            {
                TenantId  = tenant.Id,
                AppName   = Input.TenantName.Trim(),
            });
            await _db.SaveChangesAsync();

            // 3. Create admin user
            var user = new ApplicationUser
            {
                UserName       = Input.Email,
                Email          = Input.Email,
                FirstName      = Input.FirstName.Trim(),
                LastName       = Input.LastName.Trim(),
                TenantId       = tenant.Id,
                EmailConfirmed = false,
            };

            var createResult = await _userManager.CreateAsync(user, Input.Password);
            if (!createResult.Succeeded)
            {
                await tx.RollbackAsync();
                foreach (var error in createResult.Errors)
                    ModelState.AddModelError(string.Empty, error.Description);
                return Page();
            }

            // 4. Assign TenantAdmin role
            var roleResult = await _userManager.AddToRoleAsync(user, "TenantAdmin");
            if (!roleResult.Succeeded)
            {
                await tx.RollbackAsync();
                foreach (var e in roleResult.Errors)
                    ModelState.AddModelError(string.Empty, e.Description);
                return Page();
            }

            await tx.CommitAsync();

            // 5. Send welcome / email confirmation
            var code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
            var callbackUrl = Url.Page(
                "/Account/ConfirmEmail",
                pageHandler: null,
                values: new { area = "Identity", userId = user.Id, code },
                protocol: Request.Scheme)!;

            try
            {
                var encodedUrl = HtmlEncoder.Default.Encode(callbackUrl);
                var appName    = (await _settingsService.GetAsync(tenant.Id)).AppName ?? Input.TenantName.Trim();
                var firstName  = Input.FirstName.Trim();

                await _emailSender.SendEmailAsync(
                    Input.Email,
                    $"Welcome to {appName} — confirm your email",
                    BuildWelcomeEmailHtml(firstName, Input.TenantName.Trim(), encodedUrl, appName));
            }
            catch (Exception ex)
            {
                // Log but don't fail — account was created successfully
                // User can request a new confirmation email
                _ = ex; // suppress warning
            }

            if (_userManager.Options.SignIn.RequireConfirmedAccount)
                return RedirectToPage("RegisterConfirmation", new { email = Input.Email, returnUrl });

            await _signInManager.SignInAsync(user, isPersistent: false);
            return RedirectToPage("SelectPlan");
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            ModelState.AddModelError(string.Empty,
                $"An error occurred creating your account: {ex.GetBaseException().Message}");
            return Page();
        }
    }

    private static string GenerateSlug(string name)
    {
        var slug = name.ToLowerInvariant();
        slug = System.Text.RegularExpressions.Regex.Replace(slug, @"[^a-z0-9\s-]", "");
        slug = System.Text.RegularExpressions.Regex.Replace(slug, @"\s+", "-");
        slug = slug.Trim('-');
        return slug.Length > 80 ? slug[..80] : slug;
    }

    private static string BuildWelcomeEmailHtml(string firstName, string tenantName, string encodedUrl, string appName) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>
        <body style="margin:0;padding:0;background:#f4f6fb;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif">
          <table width="100%" cellpadding="0" cellspacing="0" style="padding:40px 16px">
            <tr><td align="center">
              <table width="100%" cellpadding="0" cellspacing="0" style="max-width:560px;background:#fff;border-radius:12px;border:1px solid #e5e7eb;box-shadow:0 1px 6px rgba(0,0,0,.06)">
                <tr>
                  <td style="padding:36px 40px 28px">

                    <div style="margin-bottom:28px">
                      <svg width="28" height="28" viewBox="0 0 24 24" fill="none">
                        <rect x="2"  y="14" width="4"  height="8"  rx="1" fill="#2563eb" opacity=".7"/>
                        <rect x="8"  y="9"  width="4"  height="13" rx="1" fill="#2563eb" opacity=".85"/>
                        <rect x="14" y="4"  width="4"  height="18" rx="1" fill="#2563eb"/>
                      </svg>
                      <span style="font-size:1.1rem;font-weight:700;color:#111827;vertical-align:middle;margin-left:8px">{{appName}}</span>
                    </div>

                    <div style="width:48px;height:48px;background:#eff6ff;border-radius:10px;display:inline-flex;align-items:center;justify-content:center;margin-bottom:20px">
                      <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="#2563eb" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                        <path d="M22 6l-10 7L2 6"/>
                        <rect x="2" y="4" width="20" height="16" rx="2"/>
                      </svg>
                    </div>

                    <h1 style="font-size:1.5rem;font-weight:700;color:#111827;margin:0 0 10px">Welcome aboard, {{firstName}} 👋</h1>
                    <p style="font-size:.95rem;color:#374151;margin:0 0 14px;line-height:1.65">
                      Your <strong>{{tenantName}}</strong> workspace on Lucentstride Pulse is almost ready.
                      You're one click away from a real-time view of your operations — monitor key signals,
                      automate workflows, and act on insights without the busywork.
                    </p>
                    <p style="font-size:.9rem;color:#6b7280;margin:0 0 28px;line-height:1.6">
                      Please confirm your email address to finish setting up your account.
                    </p>

                    <table cellpadding="0" cellspacing="0" style="margin-bottom:28px">
                      <tr>
                        <td style="background:#2563eb;border-radius:8px">
                          <a href="{{encodedUrl}}"
                             style="display:inline-block;padding:13px 32px;color:#fff;font-size:.925rem;font-weight:600;text-decoration:none;border-radius:8px">
                            Confirm my email
                          </a>
                        </td>
                      </tr>
                    </table>

                    <p style="font-size:.82rem;color:#9ca3af;margin:0 0 4px">Button not working? Copy this link into your browser:</p>
                    <p style="font-size:.78rem;color:#2563eb;word-break:break-all;margin:0 0 28px">
                      <a href="{{encodedUrl}}" style="color:#2563eb">{{encodedUrl}}</a>
                    </p>

                    <hr style="border:none;border-top:1px solid #f3f4f6;margin:0 0 20px" />
                    <p style="font-size:.78rem;color:#9ca3af;margin:0 0 6px">
                      You're receiving this email because this address was used to create a {{appName}} workspace.
                      If that wasn't you, you can safely ignore this message — the account won't activate without confirmation.
                    </p>
                    <p style="font-size:.78rem;color:#9ca3af;margin:0">
                      — The {{appName}} team
                    </p>

                  </td>
                </tr>
              </table>
            </td></tr>
          </table>
        </body>
        </html>
        """;
}
