using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Pulse.Data;
using Pulse.Data.Entities;

namespace Pulse.Pages.Admin;

[Authorize(Policy = "SuperAdminOnly")]
public class BillingModel(ApplicationDbContext db) : PageModel
{
    // ── Summary stats ──────────────────────────────────────────────────────────
    public int    TotalTenants       { get; private set; }
    public int    ActiveSubscriptions { get; private set; }
    public int    PastDueCount        { get; private set; }
    public int    UnpaidInvoiceCount  { get; private set; }
    public decimal UnpaidRevenue      { get; private set; }
    public decimal PaidRevenueMtd     { get; private set; }
    public decimal PaidRevenueTotal   { get; private set; }

    // ── Tenant rows ────────────────────────────────────────────────────────────
    public List<TenantBillingRow> Rows { get; private set; } = [];

    // ── Invoice drill-down (when tenantId query param is set) ─────────────────
    public Guid?         DrillTenantId   { get; private set; }
    public string?       DrillTenantName { get; private set; }
    public List<Invoice> DrillInvoices   { get; private set; } = [];

    // ── Filters ────────────────────────────────────────────────────────────────
    [BindProperty(SupportsGet = true)] public string? StatusFilter { get; set; }
    [BindProperty(SupportsGet = true)] public Guid?   TenantId    { get; set; }

    [TempData] public string? StatusMessage { get; set; }

    public class TenantBillingRow
    {
        public Guid    TenantId            { get; init; }
        public string  TenantName          { get; init; } = "";
        public bool    IsActive            { get; init; }
        public string? PlanName            { get; init; }
        public string? BillingCycle        { get; init; }
        public string? SubscriptionStatus  { get; init; }
        public DateTime? NextRenewalDate   { get; init; }
        public int     TotalInvoices       { get; init; }
        public int     UnpaidInvoices      { get; init; }
        public decimal TotalBilled         { get; init; }
        public decimal TotalPaid           { get; init; }
        public decimal OutstandingBalance  { get; init; }
    }

    public async Task OnGetAsync()
    {
        // ── Load invoice drill-down if requested ──────────────────────────────
        if (TenantId.HasValue)
        {
            DrillTenantId = TenantId;
            var tenant = await db.Tenants.IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == TenantId.Value);
            DrillTenantName = tenant?.Name;

            DrillInvoices = await db.Invoices
                .Where(i => i.TenantId == TenantId.Value)
                .OrderByDescending(i => i.IssuedUtc)
                .ToListAsync();
        }

        // ── Load all tenants with their active subscription ───────────────────
        var tenants = await db.Tenants
            .IgnoreQueryFilters()
            .OrderBy(t => t.Name)
            .ToListAsync();

        var activeSubs = await db.TenantSubscriptions
            .Include(s => s.SubscriptionPlan)
            .Where(s => s.Status == SubscriptionStatus.Active ||
                        s.Status == SubscriptionStatus.PastDue)
            .ToListAsync();

        var subsByTenant = activeSubs.ToDictionary(s => s.TenantId);

        // ── Load invoice aggregates per tenant ────────────────────────────────
        var invoiceAggs = await db.Invoices
            .GroupBy(i => i.TenantId)
            .Select(g => new
            {
                TenantId       = g.Key,
                TotalInvoices  = g.Count(),
                UnpaidCount    = g.Count(i => i.Status == InvoiceStatus.Unpaid),
                TotalBilled    = g.Sum(i => i.Amount),
                TotalPaid      = g.Sum(i => i.Status == InvoiceStatus.Paid ? i.Amount : 0m),
            })
            .ToListAsync();

        var aggByTenant = invoiceAggs.ToDictionary(a => a.TenantId);

        // ── Build rows ────────────────────────────────────────────────────────
        var rows = tenants.Select(t =>
        {
            subsByTenant.TryGetValue(t.Id, out var sub);
            aggByTenant.TryGetValue(t.Id, out var agg);

            return new TenantBillingRow
            {
                TenantId           = t.Id,
                TenantName         = t.Name,
                IsActive           = t.IsActive,
                PlanName           = sub?.SubscriptionPlan.Name,
                BillingCycle       = sub?.BillingCycle.ToString(),
                SubscriptionStatus = sub?.Status.ToString(),
                NextRenewalDate    = sub?.NextRenewalDate,
                TotalInvoices      = agg?.TotalInvoices ?? 0,
                UnpaidInvoices     = agg?.UnpaidCount   ?? 0,
                TotalBilled        = agg?.TotalBilled   ?? 0m,
                TotalPaid          = agg?.TotalPaid     ?? 0m,
                OutstandingBalance = (agg?.TotalBilled ?? 0m) - (agg?.TotalPaid ?? 0m),
            };
        }).ToList();

        // ── Apply status filter ───────────────────────────────────────────────
        Rows = StatusFilter switch
        {
            "pastdue"  => rows.Where(r => r.SubscriptionStatus == "PastDue").ToList(),
            "unpaid"   => rows.Where(r => r.UnpaidInvoices > 0).ToList(),
            "inactive" => rows.Where(r => !r.IsActive).ToList(),
            "nosub"    => rows.Where(r => r.PlanName is null).ToList(),
            _          => rows,
        };

        // ── Summary stats ─────────────────────────────────────────────────────
        TotalTenants        = tenants.Count;
        ActiveSubscriptions = activeSubs.Count(s => s.Status == SubscriptionStatus.Active);
        PastDueCount        = activeSubs.Count(s => s.Status == SubscriptionStatus.PastDue);
        UnpaidInvoiceCount  = invoiceAggs.Sum(a => a.UnpaidCount);
        UnpaidRevenue       = rows.Sum(r => r.OutstandingBalance);
        PaidRevenueMtd      = await db.Invoices
            .Where(i => i.Status == InvoiceStatus.Paid &&
                        i.PaidUtc!.Value.Year  == DateTime.UtcNow.Year &&
                        i.PaidUtc!.Value.Month == DateTime.UtcNow.Month)
            .SumAsync(i => i.Amount);
        PaidRevenueTotal = invoiceAggs.Sum(a => a.TotalPaid);
    }

    public async Task<IActionResult> OnPostVoidInvoiceAsync(int invoiceId, Guid tenantId)
    {
        var invoice = await db.Invoices
            .FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId);

        if (invoice is null) return NotFound();
        if (invoice.Status == InvoiceStatus.Paid)
        {
            StatusMessage = "Cannot void a paid invoice.";
            return RedirectToPage(new { tenantId });
        }

        invoice.Status = InvoiceStatus.Void;
        await db.SaveChangesAsync();

        StatusMessage = $"Invoice {invoice.Number} voided.";
        return RedirectToPage(new { tenantId });
    }

    public async Task<IActionResult> OnPostMarkPaidAsync(int invoiceId, Guid tenantId)
    {
        var invoice = await db.Invoices
            .FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId);

        if (invoice is null) return NotFound();
        if (invoice.Status == InvoiceStatus.Void)
        {
            StatusMessage = "Cannot mark a void invoice as paid.";
            return RedirectToPage(new { tenantId });
        }

        invoice.Status           = InvoiceStatus.Paid;
        invoice.PaidUtc          = DateTime.UtcNow;
        invoice.PaymentReference ??= $"MANUAL-{DateTime.UtcNow:yyyyMMddHHmmss}";

        // Reactivate subscription if it was PastDue
        var sub = await db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.Id == invoice.TenantSubscriptionId);
        if (sub?.Status == SubscriptionStatus.PastDue)
        {
            sub.Status = SubscriptionStatus.Active;
        }

        await db.SaveChangesAsync();
        StatusMessage = $"Invoice {invoice.Number} marked as paid.";
        return RedirectToPage(new { tenantId });
    }
}
