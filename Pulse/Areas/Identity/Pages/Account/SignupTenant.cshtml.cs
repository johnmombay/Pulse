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
                IsActive   = true,
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

            // 5. Send email confirmation
            var code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
            var callbackUrl = Url.Page(
                "/Account/ConfirmEmail",
                pageHandler: null,
                values: new { area = "Identity", userId = user.Id, code },
                protocol: Request.Scheme)!;

            try
            {
                await _emailSender.SendEmailAsync(Input.Email, "Confirm your email",
                    $"Please confirm your account by <a href='{HtmlEncoder.Default.Encode(callbackUrl)}'>clicking here</a>.");
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
            return LocalRedirect(returnUrl);
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
}
