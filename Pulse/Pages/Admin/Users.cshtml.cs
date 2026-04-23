using Pulse.Data;
using Pulse.Infrastructure;
using Pulse.Models;
using Pulse.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;

namespace Pulse.Pages.Admin;

[Authorize(Policy = "TenantAdminOrAbove")]
public class UsersModel(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    ITenantContext tenantContext,
    ApplicationDbContext db,
    IEmailSender emailSender,
    LlmSettingsService settingsService,
    ISubscriptionLimitService subscriptionLimits,
    ILogger<UsersModel> logger) : PageModel
{
    public record UserRow(string Id, string? FirstName, string? LastName, string? Email, string Role)
    {
        public string? FullName =>
            $"{FirstName} {LastName}".Trim() is { Length: > 0 } s ? s : null;
    }

    public IReadOnlyList<UserRow> Users { get; private set; } = [];
    public IReadOnlyList<string> AvailableRoles { get; private set; } = [];

    [TempData] public string? StatusMessage { get; set; }

    [BindProperty] public CreateInput Create { get; set; } = new();
    [BindProperty] public EditInput   Edit   { get; set; } = new();
    [BindProperty] public InviteInput Invite { get; set; } = new();

    // SuperAdmin sees and manages every tenant. TenantAdmin is scoped to their own tenant.
    public bool IsSuperAdmin => tenantContext.IsSuperAdmin;
    private Guid? CurrentTenantId => tenantContext.TenantId;

    /// <summary>
    /// Only SuperAdmin can create users with a password directly. TenantAdmins
    /// must use the invitation flow so the new user picks their own credentials.
    /// </summary>
    public bool CanCreateUsers => IsSuperAdmin;

    // Roles a TenantAdmin is allowed to assign / edit (never SuperAdmin).
    private static readonly string[] TenantAssignableRoles = ["TenantAdmin", "TenantUser"];

    public class CreateInput
    {
        [Required, MaxLength(100)]
        [Display(Name = "First Name")]
        public string FirstName { get; set; } = "";

        [Required, MaxLength(100)]
        [Display(Name = "Last Name")]
        public string LastName { get; set; } = "";

        [Required, EmailAddress]
        [Display(Name = "Email")]
        public string Email { get; set; } = "";

        [Required, MinLength(6)]
        [Display(Name = "Password")]
        public string Password { get; set; } = "";

        [Required]
        [Display(Name = "Role")]
        public string Role { get; set; } = "TenantUser";
    }

    public class EditInput
    {
        [Required] public string UserId { get; set; } = "";

        [Required, MaxLength(100)]
        [Display(Name = "First Name")]
        public string FirstName { get; set; } = "";

        [Required, MaxLength(100)]
        [Display(Name = "Last Name")]
        public string LastName { get; set; } = "";

        [Required, EmailAddress]
        [Display(Name = "Email")]
        public string Email { get; set; } = "";

        [Required]
        [Display(Name = "Role")]
        public string Role { get; set; } = "TenantUser";

        [MinLength(6)]
        [Display(Name = "New Password")]
        public string? NewPassword { get; set; }
    }

    public class InviteInput
    {
        [Required, MaxLength(100)]
        [Display(Name = "First Name")]
        public string FirstName { get; set; } = "";

        [Required, MaxLength(100)]
        [Display(Name = "Last Name")]
        public string LastName { get; set; } = "";

        [Required, EmailAddress]
        [Display(Name = "Email")]
        public string Email { get; set; } = "";

        [Required]
        [Display(Name = "Role")]
        public string Role { get; set; } = "TenantUser";
    }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        // SuperAdmin sees every role. TenantAdmin can only assign tenant-scoped roles.
        if (IsSuperAdmin)
        {
            AvailableRoles = roleManager.Roles
                .OrderBy(r => r.Name)
                .Select(r => r.Name!)
                .ToList();
        }
        else
        {
            AvailableRoles = TenantAssignableRoles;
        }

        // Filter the user list by tenant for non-SuperAdmin callers.
        var query = userManager.Users.AsQueryable();
        if (!IsSuperAdmin)
        {
            var tid = CurrentTenantId;
            query = query.Where(u => u.TenantId == tid);
        }

        var rows = new List<UserRow>();
        foreach (var user in query.OrderBy(u => u.Email).ToList())
        {
            var roles = await userManager.GetRolesAsync(user);
            rows.Add(new UserRow(
                user.Id,
                string.IsNullOrWhiteSpace(user.FirstName) ? null : user.FirstName,
                string.IsNullOrWhiteSpace(user.LastName)  ? null : user.LastName,
                user.Email,
                roles.FirstOrDefault() ?? "User"));
        }
        Users = rows;
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        // Only SuperAdmin may create users with a password directly. TenantAdmins
        // must invite — see OnPostInviteAsync.
        if (!IsSuperAdmin) return Forbid();

        // Each handler validates only its own input model. The other BindProperty
        // models (Edit / Invite) are bound with empty defaults on every POST and
        // would otherwise trip their own [Required] rules.
        ModelState.Clear();
        if (!TryValidateModel(Create, nameof(Create)))
        {
            await LoadAsync();
            return Page();
        }

        // TenantAdmins may not assign roles outside their permitted set (no SuperAdmin).
        if (!IsSuperAdmin && !TenantAssignableRoles.Contains(Create.Role))
        {
            ModelState.AddModelError($"{nameof(Create)}.{nameof(Create.Role)}", "You are not allowed to assign this role.");
            await LoadAsync();
            return Page();
        }

        var user = new ApplicationUser
        {
            UserName       = Create.Email,
            Email          = Create.Email,
            FirstName      = Create.FirstName.Trim(),
            LastName       = Create.LastName.Trim(),
            EmailConfirmed = true,
            // TenantAdmins always create users inside their own tenant.
            // SuperAdmin creates in their own (null) tenant — cross-tenant user creation
            // belongs on the SuperAdmin Tenants page, not here.
            TenantId       = CurrentTenantId,
        };
        var result = await userManager.CreateAsync(user, Create.Password);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors)
                ModelState.AddModelError(string.Empty, e.Description);
            await LoadAsync();
            return Page();
        }

        await userManager.AddToRoleAsync(user, Create.Role);
        StatusMessage = $"User {Create.Email} created successfully.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEditAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(Edit, nameof(Edit)))
        {
            await LoadAsync();
            return Page();
        }

        var user = await userManager.FindByIdAsync(Edit.UserId);
        if (user is null) return NotFound();

        // TenantAdmins may only edit users inside their own tenant.
        if (!IsSuperAdmin && user.TenantId != CurrentTenantId)
            return Forbid();

        // TenantAdmins may not promote anyone to (or out of) SuperAdmin.
        if (!IsSuperAdmin && !TenantAssignableRoles.Contains(Edit.Role))
        {
            ModelState.AddModelError($"{nameof(Edit)}.{nameof(Edit.Role)}", "You are not allowed to assign this role.");
            await LoadAsync();
            return Page();
        }

        user.FirstName = Edit.FirstName.Trim();
        user.LastName  = Edit.LastName.Trim();
        user.Email    = Edit.Email;
        user.UserName = Edit.Email;
        await userManager.UpdateAsync(user);

        var existingRoles = await userManager.GetRolesAsync(user);

        // Defence-in-depth: a TenantAdmin must not strip a SuperAdmin role from someone
        // (this can only happen if a tenant boundary was already breached).
        if (!IsSuperAdmin && existingRoles.Contains("SuperAdmin"))
            return Forbid();

        await userManager.RemoveFromRolesAsync(user, existingRoles);
        await userManager.AddToRoleAsync(user, Edit.Role);

        if (!string.IsNullOrWhiteSpace(Edit.NewPassword))
        {
            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            await userManager.ResetPasswordAsync(user, token, Edit.NewPassword);
        }

        StatusMessage = $"User {Edit.Email} updated.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string userId)
    {
        if (userManager.GetUserId(User) == userId)
        {
            StatusMessage = "You cannot delete your own account.";
            return RedirectToPage();
        }

        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            StatusMessage = "User deleted.";
            return RedirectToPage();
        }

        // TenantAdmins may only delete users inside their own tenant, and never SuperAdmins.
        if (!IsSuperAdmin)
        {
            if (user.TenantId != CurrentTenantId)
                return Forbid();

            var roles = await userManager.GetRolesAsync(user);
            if (roles.Contains("SuperAdmin"))
                return Forbid();
        }

        var deletedTenantId = user.TenantId;
        await userManager.DeleteAsync(user);

        // If this was the last user in the tenant, delete the tenant (and its
        // AppSettings row) so the organisation name is freed and can be reused.
        if (deletedTenantId is Guid tid)
        {
            var remaining = await userManager.Users
                .CountAsync(u => u.TenantId == tid);

            if (remaining == 0)
            {
                var tenant = await db.Tenants
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(t => t.Id == tid);

                if (tenant is not null)
                {
                    var appSettings = await db.AppSettings
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(a => a.TenantId == tid);
                    if (appSettings is not null)
                        db.AppSettings.Remove(appSettings);

                    db.Tenants.Remove(tenant);
                    await db.SaveChangesAsync();

                    logger.LogInformation(
                        "Tenant {TenantId} ({Name}) deleted \u2014 had no remaining users.",
                        tid, tenant.Name);

                    StatusMessage = $"User deleted. Organisation \"{tenant.Name}\" was also removed because it had no remaining users.";
                    return RedirectToPage();
                }
            }
        }

        StatusMessage = "User deleted.";
        return RedirectToPage();
    }

    // ── Invite ────────────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostInviteAsync()
    {
        // Each handler validates only its own input model. The other BindProperty
        // models (Create / Edit) are bound with empty defaults on every POST and
        // would otherwise trip their own [Required] rules.
        ModelState.Clear();
        if (!TryValidateModel(Invite, nameof(Invite)))
        {
            await LoadAsync();
            return Page();
        }

        // TenantAdmins may only invite into roles inside their permitted set (no SuperAdmin).
        if (!IsSuperAdmin && !TenantAssignableRoles.Contains(Invite.Role))
        {
            ModelState.AddModelError($"{nameof(Invite)}.{nameof(Invite.Role)}", "You are not allowed to assign this role.");
            await LoadAsync();
            return Page();
        }

        // Enforce subscription user limit for tenant invitations.
        if (CurrentTenantId is Guid inviteTenantId)
        {
            if (!await subscriptionLimits.CanAddUserAsync(inviteTenantId))
            {
                ModelState.AddModelError(string.Empty, "Your subscription plan's user limit has been reached. Please upgrade your plan to invite more users.");
                await LoadAsync();
                return Page();
            }
        }

        // Check whether the email already exists anywhere in the system.
        // equals email (required for email-based login), so UserName is globally unique.
        var existing = await userManager.FindByEmailAsync(Invite.Email);
        ApplicationUser user;
        bool reinvite = false;

        if (existing is not null)
        {
            // If the existing account belongs to another tenant (or this user is
            // a SuperAdmin outside any tenant), we cannot reuse the email.
            if (existing.TenantId != CurrentTenantId)
            {
                ModelState.AddModelError($"{nameof(Invite)}.{nameof(Invite.Email)}",
                    "That email is already registered to another account and cannot be invited here.");
                await LoadAsync();
                return Page();
            }

            // Same-tenant re-invite is only allowed if the original invitation
            // was never accepted (email not confirmed). Otherwise the admin
            // should edit / reset the password of the existing user instead.
            if (existing.EmailConfirmed)
            {
                ModelState.AddModelError($"{nameof(Invite)}.{nameof(Invite.Email)}",
                    "A user with that email already exists in this organisation.");
                await LoadAsync();
                return Page();
            }

            // Refresh the pending user with the latest name + role and resend the link.
            existing.FirstName = Invite.FirstName.Trim();
            existing.LastName  = Invite.LastName.Trim();
            await userManager.UpdateAsync(existing);

            var currentRoles = await userManager.GetRolesAsync(existing);
            if (currentRoles.Count > 0)
                await userManager.RemoveFromRolesAsync(existing, currentRoles);
            await userManager.AddToRoleAsync(existing, Invite.Role);

            user     = existing;
            reinvite = true;
        }
        else
        {
            user = new ApplicationUser
            {
                UserName       = Invite.Email,
                Email          = Invite.Email,
                FirstName      = Invite.FirstName.Trim(),
                LastName       = Invite.LastName.Trim(),
                EmailConfirmed = false,
                TenantId       = CurrentTenantId,
            };
        }

        // Create with no password — the invitee will set one when they accept.
        // Skip on re-invite where the user already exists in this tenant.
        if (!reinvite)
        {
            var createResult = await userManager.CreateAsync(user);
            if (!createResult.Succeeded)
            {
                foreach (var e in createResult.Errors)
                    ModelState.AddModelError(string.Empty, e.Description);
                await LoadAsync();
                return Page();
            }

            var roleResult = await userManager.AddToRoleAsync(user, Invite.Role);
            if (!roleResult.Succeeded)
            {
                // Roll back the half-created user so a retry can succeed.
                await userManager.DeleteAsync(user);
                foreach (var e in roleResult.Errors)
                    ModelState.AddModelError(string.Empty, e.Description);
                await LoadAsync();
                return Page();
            }
        }

        // Build the accept-invitation link using a password-reset token.
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var code  = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

        var callbackUrl = Url.Page(
            "/Account/AcceptInvitation",
            pageHandler: null,
            values: new { area = "Identity", userId = user.Id, code },
            protocol: Request.Scheme)!;

        var encodedUrl = HtmlEncoder.Default.Encode(callbackUrl);

        // Resolve tenant + app branding for the email.
        var tenantId   = CurrentTenantId ?? Guid.Empty;
        var tenantName = tenantId == Guid.Empty
            ? "your organisation"
            : (await db.Tenants.IgnoreQueryFilters()
                  .Where(t => t.Id == tenantId)
                  .Select(t => t.Name)
                  .FirstOrDefaultAsync()) ?? "your organisation";
        var appName    = (await settingsService.GetAsync(tenantId)).AppName ?? "Pulse";
        var inviterId  = userManager.GetUserId(User);
        var inviter    = inviterId is null ? null : await userManager.FindByIdAsync(inviterId);
        var inviterName = inviter is null
            ? "A teammate"
            : ($"{inviter.FirstName} {inviter.LastName}".Trim() is { Length: > 0 } n ? n : (inviter.Email ?? "A teammate"));

        try
        {
            await emailSender.SendEmailAsync(
                Invite.Email,
                $"You're invited to join {tenantName} on {appName}",
                BuildInviteEmailHtml(
                    inviteeFirstName: user.FirstName,
                    inviterName:      inviterName,
                    tenantName:       tenantName,
                    appName:          appName,
                    encodedUrl:       encodedUrl));

            logger.LogInformation(
                "Invitation email queued for {Email} (tenant {TenantId}, role {Role})",
                Invite.Email, tenantId, Invite.Role);
        }
        catch (Exception ex)
        {
            // Don't fail the request — the user row exists and the admin can
            // resend / share the link manually if delivery had issues.
            logger.LogError(ex, "Failed to send invitation email to {Email}", Invite.Email);
        }

        StatusMessage = reinvite
            ? $"Invitation re-sent to {Invite.Email}."
            : $"Invitation sent to {Invite.Email}.";
        return RedirectToPage();
    }

    private static string BuildInviteEmailHtml(
        string inviteeFirstName,
        string inviterName,
        string tenantName,
        string appName,
        string encodedUrl)
    {
        var greetingName = string.IsNullOrWhiteSpace(inviteeFirstName) ? "there" : inviteeFirstName;
        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>
            <body style="margin:0;padding:0;background:#f4f6fb;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif">
              <table width="100%" cellpadding="0" cellspacing="0" style="padding:40px 16px">
                <tr><td align="center">
                  <table width="100%" cellpadding="0" cellspacing="0" style="max-width:560px;background:#fff;border-radius:12px;border:1px solid #e5e7eb;box-shadow:0 1px 6px rgba(0,0,0,.06)">
                    <tr><td style="padding:36px 40px 28px">

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
                          <path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"/>
                          <circle cx="9" cy="7" r="4"/>
                          <line x1="19" y1="8" x2="19" y2="14"/>
                          <line x1="22" y1="11" x2="16" y2="11"/>
                        </svg>
                      </div>

                      <h1 style="font-size:1.5rem;font-weight:700;color:#111827;margin:0 0 10px">You're invited, {{greetingName}} 👋</h1>
                      <p style="font-size:.95rem;color:#374151;margin:0 0 14px;line-height:1.65">
                        <strong>{{inviterName}}</strong> has invited you to join the
                        <strong>{{tenantName}}</strong> workspace on Lucentstride Pulse.
                      </p>
                      <p style="font-size:.9rem;color:#6b7280;margin:0 0 28px;line-height:1.6">
                        Click the button below to set your password and finish setting up your account.
                        You'll be signed in automatically.
                      </p>

                      <table cellpadding="0" cellspacing="0" style="margin-bottom:28px">
                        <tr><td style="background:#2563eb;border-radius:8px">
                          <a href="{{encodedUrl}}"
                             style="display:inline-block;padding:13px 32px;color:#fff;font-size:.925rem;font-weight:600;text-decoration:none;border-radius:8px">
                            Accept invitation
                          </a>
                        </td></tr>
                      </table>

                      <p style="font-size:.82rem;color:#9ca3af;margin:0 0 4px">Button not working? Copy this link into your browser:</p>
                      <p style="font-size:.78rem;color:#2563eb;word-break:break-all;margin:0 0 28px">
                        <a href="{{encodedUrl}}" style="color:#2563eb">{{encodedUrl}}</a>
                      </p>

                      <hr style="border:none;border-top:1px solid #f3f4f6;margin:0 0 20px" />
                      <p style="font-size:.78rem;color:#9ca3af;margin:0">
                        This invitation was sent to you because {{inviterName}} added your email to their {{tenantName}} workspace.
                        If you weren't expecting it, you can ignore this email — the account stays inactive until you set a password.
                      </p>

                    </td></tr>
                  </table>
                </td></tr>
              </table>
            </body>
            </html>
            """;
    }
}
