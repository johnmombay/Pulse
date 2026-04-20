using Pulse.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;

namespace Pulse.Pages.Admin;

[Authorize(Policy = "SuperAdminOnly")]
public class UsersModel(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager) : PageModel
{
    public record UserRow(string Id, string? FullName, string? Email, string Role);

    public IReadOnlyList<UserRow> Users { get; private set; } = [];
    public IReadOnlyList<string> AvailableRoles { get; private set; } = [];

    [TempData] public string? StatusMessage { get; set; }

    [BindProperty] public CreateInput Create { get; set; } = new();
    [BindProperty] public EditInput   Edit   { get; set; } = new();

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
        public string Role { get; set; } = "User";
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
        public string Role { get; set; } = "User";

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
        AvailableRoles = roleManager.Roles.OrderBy(r => r.Name).Select(r => r.Name!).ToList();
        var rows = new List<UserRow>();
        foreach (var user in userManager.Users.OrderBy(u => u.Email).ToList())
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

        var user = new ApplicationUser
        {
            UserName       = Create.Email,
            Email          = Create.Email,
            FirstName      = Create.FullName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty,
            LastName       = Create.FullName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() ?? string.Empty,
            EmailConfirmed = true
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

        var names = (Edit.FullName ?? string.Empty).Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        user.FirstName = names.Length > 0 ? names[0] : string.Empty;
        user.LastName = names.Length > 1 ? names[1] : string.Empty;
        user.Email    = Edit.Email;
        user.UserName = Edit.Email;
        await userManager.UpdateAsync(user);

        var existingRoles = await userManager.GetRolesAsync(user);
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
        if (user is not null)
            await userManager.DeleteAsync(user);

        StatusMessage = "User deleted.";
        return RedirectToPage();
    }
}
