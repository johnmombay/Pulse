using Pulse.Data;
using Pulse.Infrastructure;
using Pulse.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace Pulse.Pages.Admin;

[Authorize(Policy = "TenantAdminOrAbove")]
public class UsersModel(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    ITenantContext tenantContext,
    ApplicationDbContext db,
    ILogger<UsersModel> logger) : PageModel
{
    public record UserRow(string Id, string? FullName, string? Email, string Role);

    public IReadOnlyList<UserRow> Users { get; private set; } = [];
    public IReadOnlyList<string> AvailableRoles { get; private set; } = [];

    [TempData] public string? StatusMessage { get; set; }

    [BindProperty] public CreateInput Create { get; set; } = new();
    [BindProperty] public EditInput   Edit   { get; set; } = new();

    // SuperAdmin sees and manages every tenant. TenantAdmin is scoped to their own tenant.
    private bool IsSuperAdmin => tenantContext.IsSuperAdmin;
    private Guid? CurrentTenantId => tenantContext.TenantId;

    // Roles a TenantAdmin is allowed to assign / edit (never SuperAdmin).
    private static readonly string[] TenantAssignableRoles = ["TenantAdmin", "TenantUser"];

    public class CreateInput
    {
        [Required, MaxLength(150)]
        [Display(Name = "Full Name")]
        public string FullName { get; set; } = "";

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

        [Required, MaxLength(150)]
        [Display(Name = "Full Name")]
        public string FullName { get; set; } = "";

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
            var fullName = (user.FirstName + " " + user.LastName).Trim();
            rows.Add(new UserRow(user.Id, string.IsNullOrWhiteSpace(fullName) ? null : fullName, user.Email, roles.FirstOrDefault() ?? "User"));
        }
        Users = rows;
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        // Clear errors from the Edit model so they don't block Create
        foreach (var key in ModelState.Keys.Where(k => k.StartsWith("Edit.")).ToList())
            ModelState.Remove(key);

        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        // TenantAdmins may not assign roles outside their permitted set (no SuperAdmin).
        if (!IsSuperAdmin && !TenantAssignableRoles.Contains(Create.Role))
        {
            ModelState.AddModelError(nameof(Create.Role), "You are not allowed to assign this role.");
            await LoadAsync();
            return Page();
        }

        var user = new ApplicationUser
        {
            UserName       = Create.Email,
            Email          = Create.Email,
            FirstName      = Create.FullName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty,
            LastName       = Create.FullName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() ?? string.Empty,
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
        // Clear errors from the Create model so they don't block Edit
        foreach (var key in ModelState.Keys.Where(k => k.StartsWith("Create.")).ToList())
            ModelState.Remove(key);

        if (!ModelState.IsValid)
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
            ModelState.AddModelError(nameof(Edit.Role), "You are not allowed to assign this role.");
            await LoadAsync();
            return Page();
        }

        var names = (Edit.FullName ?? string.Empty).Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        user.FirstName = names.Length > 0 ? names[0] : string.Empty;
        user.LastName = names.Length > 1 ? names[1] : string.Empty;
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
}
