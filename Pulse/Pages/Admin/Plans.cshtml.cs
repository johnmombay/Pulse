using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pulse.Data;
using Pulse.Data.Entities;
using Pulse.Infrastructure;

namespace Pulse.Pages.Admin;

[Authorize(Policy = "SuperAdminOnly")]
public class PlansModel(ApplicationDbContext db, IOptions<DeploymentOptions> deployment) : PageModel
{
    private readonly DeploymentOptions _deployment = deployment.Value;
    // ── Display ───────────────────────────────────────────────────────────────
    public List<SubscriptionPlan> Plans { get; set; } = [];

    [TempData] public string? StatusMessage { get; set; }

    /// <summary>Tells the view which modal to re-open after a validation failure.</summary>
    public string? ShowModal { get; set; }

    // ── Bound forms — kept as separate classes so [Required] on one prefix
    //    never bleeds into the other prefix's model-state entries. ─────────────
    [BindProperty] public CreateInput Create { get; set; } = new();
    [BindProperty] public EditInput   Edit   { get; set; } = new();

    // ── Input models ─────────────────────────────────────────────────────────
    public class CreateInput
    {
        [Required, MaxLength(100)]
        [Display(Name = "Name")]
        public string Name { get; set; } = "";

        [MaxLength(500)]
        [Display(Name = "Description")]
        public string? Description { get; set; }

        [Range(1, int.MaxValue)]
        [Display(Name = "Max Users / Tenant")]
        public int MaxUsersPerTenant { get; set; } = 10;

        [Range(1, int.MaxValue)]
        [Display(Name = "Max Databases & Data Sources")]
        public int MaxDatabases { get; set; } = 5;

        [Range(1, int.MaxValue)]
        [Display(Name = "Max Agents")]
        public int MaxAgents { get; set; } = 3;

        [Range(0, int.MaxValue)]
        [Display(Name = "Monthly Usage Limit (units)")]
        public int MonthlyUsageLimitUnits { get; set; } = 100000;

        [Range(typeof(decimal), "0", "999999999")]
        [Display(Name = "Monthly Price ($)")]
        public decimal MonthlyPrice { get; set; }

        [Range(typeof(decimal), "0", "999999999")]
        [Display(Name = "Annual Price ($)")]
        public decimal AnnualPrice { get; set; }

        [Range(typeof(decimal), "0", "999999999")]
        [Display(Name = "Overage per Extra User ($)")]
        public decimal OveragePricePerUser { get; set; }

        [Range(typeof(decimal), "0", "999999999")]
        [Display(Name = "Overage per Extra Database ($)")]
        public decimal OveragePricePerDatabase { get; set; }

        [Range(typeof(decimal), "0", "999999999")]
        [Display(Name = "Overage per Extra Agent ($)")]
        public decimal OveragePricePerAgent { get; set; }

        [Range(typeof(decimal), "0", "999999999")]
        [Display(Name = "Overage per Usage Unit ($)")]
        public decimal OveragePricePerUsageUnit { get; set; }

        [Display(Name = "Active")]
        public bool IsActive { get; set; } = true;
    }

    public class EditInput
    {
        public int Id { get; set; }

        [Required, MaxLength(100)]
        [Display(Name = "Name")]
        public string Name { get; set; } = "";

        [MaxLength(500)]
        [Display(Name = "Description")]
        public string? Description { get; set; }

        [Range(1, int.MaxValue)]
        [Display(Name = "Max Users / Tenant")]
        public int MaxUsersPerTenant { get; set; } = 10;

        [Range(1, int.MaxValue)]
        [Display(Name = "Max Databases & Data Sources")]
        public int MaxDatabases { get; set; } = 5;

        [Range(1, int.MaxValue)]
        [Display(Name = "Max Agents")]
        public int MaxAgents { get; set; } = 3;

        [Range(0, int.MaxValue)]
        [Display(Name = "Monthly Usage Limit (units)")]
        public int MonthlyUsageLimitUnits { get; set; } = 100000;

        [Range(typeof(decimal), "0", "999999999")]
        [Display(Name = "Monthly Price ($)")]
        public decimal MonthlyPrice { get; set; }

        [Range(typeof(decimal), "0", "999999999")]
        [Display(Name = "Annual Price ($)")]
        public decimal AnnualPrice { get; set; }

        [Range(typeof(decimal), "0", "999999999")]
        [Display(Name = "Overage per Extra User ($)")]
        public decimal OveragePricePerUser { get; set; }

        [Range(typeof(decimal), "0", "999999999")]
        [Display(Name = "Overage per Extra Database ($)")]
        public decimal OveragePricePerDatabase { get; set; }

        [Range(typeof(decimal), "0", "999999999")]
        [Display(Name = "Overage per Extra Agent ($)")]
        public decimal OveragePricePerAgent { get; set; }

        [Range(typeof(decimal), "0", "999999999")]
        [Display(Name = "Overage per Usage Unit ($)")]
        public decimal OveragePricePerUsageUnit { get; set; }

        [Display(Name = "Active")]
        public bool IsActive { get; set; } = true;
    }

    // ── GET
    public async Task<IActionResult> OnGetAsync()
    {
        if (_deployment.IsSingleTenant) return NotFound();
        Plans = await db.SubscriptionPlans
            .Include(p => p.TenantSubscriptions)
            .OrderBy(p => p.Name)
            .ToListAsync();
        return Page();
    }

    // ── POST: Create ──────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostCreateAsync()
    {
        // Only validate fields that belong to the Create form
        RemoveModelStatePrefix("Edit.");
        ModelState.ClearValidationState(nameof(Edit));

        var hasErrors = ModelState.Any(k => k.Key.StartsWith("Create.") && k.Value!.Errors.Count > 0);
        if (hasErrors)
        {
            ShowModal = "create";
            await OnGetAsync();
            return Page();
        }

        var plan = new SubscriptionPlan
        {
            Name                     = Create.Name.Trim(),
            Description              = Create.Description?.Trim(),
            MaxUsersPerTenant        = Create.MaxUsersPerTenant,
            MaxDatabases             = Create.MaxDatabases,
            MaxAgents                = Create.MaxAgents,
            MonthlyUsageLimitUnits   = Create.MonthlyUsageLimitUnits,
            MonthlyPrice             = Create.MonthlyPrice,
            AnnualPrice              = Create.AnnualPrice,
            OveragePricePerUser      = Create.OveragePricePerUser,
            OveragePricePerDatabase  = Create.OveragePricePerDatabase,
            OveragePricePerAgent     = Create.OveragePricePerAgent,
            OveragePricePerUsageUnit = Create.OveragePricePerUsageUnit,
            IsActive                 = Create.IsActive,
        };

        db.SubscriptionPlans.Add(plan);
        await db.SaveChangesAsync();

        StatusMessage = $"Plan \"{plan.Name}\" created.";
        return RedirectToPage();
    }

    // ── POST: Edit ────────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostEditAsync()
    {
        // Only validate fields that belong to the Edit form
        RemoveModelStatePrefix("Create.");
        ModelState.ClearValidationState(nameof(Create));

        var hasErrors = ModelState.Any(k => k.Key.StartsWith("Edit.") && k.Value!.Errors.Count > 0);
        if (hasErrors)
        {
            ShowModal = "edit";
            await OnGetAsync();
            return Page();
        }

        var plan = await db.SubscriptionPlans.FindAsync(Edit.Id);
        if (plan is null) return NotFound();

        plan.Name                     = Edit.Name.Trim();
        plan.Description              = Edit.Description?.Trim();
        plan.MaxUsersPerTenant        = Edit.MaxUsersPerTenant;
        plan.MaxDatabases             = Edit.MaxDatabases;
        plan.MaxAgents                = Edit.MaxAgents;
        plan.MonthlyUsageLimitUnits   = Edit.MonthlyUsageLimitUnits;
        plan.MonthlyPrice             = Edit.MonthlyPrice;
        plan.AnnualPrice              = Edit.AnnualPrice;
        plan.OveragePricePerUser      = Edit.OveragePricePerUser;
        plan.OveragePricePerDatabase  = Edit.OveragePricePerDatabase;
        plan.OveragePricePerAgent     = Edit.OveragePricePerAgent;
        plan.OveragePricePerUsageUnit = Edit.OveragePricePerUsageUnit;
        plan.IsActive                 = Edit.IsActive;

        await db.SaveChangesAsync();

        StatusMessage = $"Plan \"{plan.Name}\" updated.";
        return RedirectToPage();
    }

    // ── POST: Delete ──────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var plan = await db.SubscriptionPlans
            .Include(p => p.TenantSubscriptions)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (plan is null) return NotFound();

        if (plan.TenantSubscriptions.Count > 0)
        {
            StatusMessage = $"Cannot delete \"{plan.Name}\" — it has active tenant subscriptions.";
            return RedirectToPage();
        }

        db.SubscriptionPlans.Remove(plan);
        await db.SaveChangesAsync();

        StatusMessage = $"Plan \"{plan.Name}\" deleted.";
        return RedirectToPage();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private void RemoveModelStatePrefix(string prefix)
    {
        foreach (var key in ModelState.Keys.Where(k => k.StartsWith(prefix)).ToList())
            ModelState.Remove(key);
    }
}
