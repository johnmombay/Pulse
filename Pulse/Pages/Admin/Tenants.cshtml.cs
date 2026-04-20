using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Pulse.Data;
using Pulse.Data.Entities;

namespace Pulse.Pages.Admin;

[Authorize(Policy = "SuperAdminOnly")]
public class TenantsModel : PageModel
{
    private readonly ApplicationDbContext _db;

    public TenantsModel(ApplicationDbContext db) => _db = db;

    public List<TenantRow> Tenants { get; set; } = [];

    public class TenantRow
    {
        public Guid   Id        { get; init; }
        public string Name      { get; init; } = "";
        public string Slug      { get; init; } = "";
        public int    UserCount { get; init; }
        public bool   IsActive  { get; init; }
        public string CreatedUtc { get; init; } = "";
    }

    public async Task OnGetAsync()
    {
        Tenants = await _db.Tenants
            .IgnoreQueryFilters()
            .OrderBy(t => t.Name)
            .Select(t => new TenantRow
            {
                Id        = t.Id,
                Name      = t.Name,
                Slug      = t.Slug,
                UserCount = t.Users.Count,
                IsActive  = t.IsActive,
                CreatedUtc = t.CreatedUtc.ToString("yyyy-MM-dd"),
            })
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid id)
    {
        var tenant = await _db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == id);
        if (tenant is null) return NotFound();

        tenant.IsActive = !tenant.IsActive;
        await _db.SaveChangesAsync();
        return RedirectToPage();
    }
}
