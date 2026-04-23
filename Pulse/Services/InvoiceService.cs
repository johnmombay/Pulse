using Microsoft.EntityFrameworkCore;
using Pulse.Data;
using Pulse.Data.Entities;

namespace Pulse.Services;

public interface IInvoiceService
{
    Task<Invoice> GenerateAsync(TenantSubscription sub, string? paymentReference, CancellationToken ct = default);
    Task<List<Invoice>> GetForTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task<Invoice?> GetByIdAsync(int invoiceId, Guid tenantId, CancellationToken ct = default);
}

public sealed class InvoiceService(
    IDbContextFactory<ApplicationDbContext> factory,
    ILogger<InvoiceService> logger) : IInvoiceService
{
    public async Task<Invoice> GenerateAsync(
        TenantSubscription sub,
        string? paymentReference,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // Generate sequential number within the year
        var year     = DateTime.UtcNow.Year;
        var yearStr  = year.ToString();
        var count    = await db.Invoices
            .CountAsync(i => i.IssuedUtc.Year == year, ct);
        var number   = $"INV-{yearStr}-{count + 1:D5}";

        var plan  = await db.SubscriptionPlans.FindAsync([sub.SubscriptionPlanId], ct)
                    ?? throw new InvalidOperationException("Plan not found.");
        var price = sub.BillingCycle == BillingCycle.Annual
            ? plan.AnnualPrice
            : plan.MonthlyPrice;

        var periodEnd = sub.BillingCycle == BillingCycle.Annual
            ? sub.NextRenewalDate
            : sub.NextRenewalDate;

        // PeriodStart is the current renewal date (or StartDate for first invoice)
        var periodStart = sub.StartDate > DateTime.UtcNow.AddDays(-2)
            ? sub.StartDate
            : sub.BillingCycle == BillingCycle.Annual
                ? sub.NextRenewalDate.AddYears(-1)
                : sub.NextRenewalDate.AddMonths(-1);

        var invoice = new Invoice
        {
            TenantId             = sub.TenantId,
            TenantSubscriptionId = sub.Id,
            Number               = number,
            Amount               = price,
            Status               = paymentReference is not null ? InvoiceStatus.Paid : InvoiceStatus.Unpaid,
            BillingCycle         = sub.BillingCycle,
            PeriodStart          = periodStart,
            PeriodEnd            = periodEnd,
            IssuedUtc            = DateTime.UtcNow,
            DueDate              = DateTime.UtcNow.AddDays(7),
            PaidUtc              = paymentReference is not null ? DateTime.UtcNow : null,
            PaymentReference     = paymentReference,
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Invoice {Number} generated for tenant {TenantId} (status={Status})",
            number, sub.TenantId, invoice.Status);

        return invoice;
    }

    public async Task<List<Invoice>> GetForTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Invoices
            .Where(i => i.TenantId == tenantId)
            .OrderByDescending(i => i.IssuedUtc)
            .ToListAsync(ct);
    }

    public async Task<Invoice?> GetByIdAsync(int invoiceId, Guid tenantId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Invoices
            .Include(i => i.TenantSubscription)
                .ThenInclude(s => s.SubscriptionPlan)
            .FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId, ct);
    }
}
