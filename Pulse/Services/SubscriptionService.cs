using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Pulse.Data;
using Pulse.Data.Entities;
using Pulse.Models;

namespace Pulse.Services;

public record SubscriptionResult(bool Success, string? ErrorMessage, Invoice? Invoice = null);

public interface ISubscriptionService
{
    /// <summary>
    /// Activates a tenant after successful payment. If no gateway is configured,
    /// notifies the tenant and all SuperAdmins and returns an error result.
    /// </summary>
    Task<SubscriptionResult> ActivateAsync(int subscriptionId, string tenantAdminUserId, CancellationToken ct = default);

    /// <summary>Pays a specific unpaid invoice (tenant self-service).</summary>
    Task<SubscriptionResult> PayInvoiceAsync(int invoiceId, Guid tenantId, CancellationToken ct = default);

    /// <summary>SuperAdmin-initiated plan change. Takes effect immediately.</summary>
    Task<SubscriptionResult> AdminChangePlanAsync(Guid tenantId, int newPlanId, BillingCycle newCycle, CancellationToken ct = default);

    /// <summary>Called by the renewal Hangfire job to charge and generate the next invoice.</summary>
    Task ProcessRenewalAsync(int subscriptionId, CancellationToken ct = default);
}

public sealed class SubscriptionService(
    IDbContextFactory<ApplicationDbContext> factory,
    PaymentGatewayService gatewayService,
    IInvoiceService invoiceService,
    IInAppNotificationService notificationService,
    BillingEmailService billingEmail,
    UserManager<ApplicationUser> userManager,
    ILogger<SubscriptionService> logger) : ISubscriptionService
{
    // ─────────────────────────────────────────────────────────────────────────
    // Activate (first payment after plan selection)
    // ─────────────────────────────────────────────────────────────────────────
    public async Task<SubscriptionResult> ActivateAsync(
        int subscriptionId, string tenantAdminUserId, CancellationToken ct = default)
    {
        var gateway = await gatewayService.GetActiveAsync();

        await using var db = await factory.CreateDbContextAsync(ct);

        var sub = await db.TenantSubscriptions
            .Include(s => s.SubscriptionPlan)
            .Include(s => s.Tenant)
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, ct);

        if (sub is null) return new(false, "Subscription not found.");

        var plan  = sub.SubscriptionPlan;
        var price = sub.BillingCycle == BillingCycle.Annual
            ? plan.AnnualPrice
            : plan.MonthlyPrice;

        // Free plans bypass payment
        if (price == 0m)
            return await FinalizeActivationAsync(db, sub, paymentRef: null, ct);

        if (gateway is null)
        {
            var tenant      = sub.Tenant;
            var adminUser   = await userManager.FindByIdAsync(tenantAdminUserId);

            // Notify tenant admin
            if (adminUser is not null)
                await notificationService.NotifyAsync(
                    adminUser.Id,
                    "Payment Unavailable",
                    "No payment gateway is currently configured. Please choose a different plan or try again later.",
                    "/Identity/Account/SelectPlan",
                    ct);

            // Alert all SuperAdmins
            await NotifySuperAdminsAsync(tenant.Name, tenant.Id, ct);
            await billingEmail.SendGatewayUnconfiguredAlertAsync(tenant.Name, tenant.Id, ct);

            return new(false,
                "Payment processing is temporarily unavailable. " +
                "Please choose a different plan or try again later.");
        }

        // ── Charge via gateway ────────────────────────────────────────────────
        // NOTE: Actual gateway SDK call goes here. The current architecture
        // stores gateway credentials in PaymentGatewaySettings. Implement
        // per-provider charging in a dedicated adapter class (e.g. StripeAdapter)
        // and inject it here. For now we record a stub reference so activation
        // can be tested end-to-end without live credentials.
        var transactionRef = $"{gateway.Provider}-PENDING-{Guid.NewGuid():N}";

        return await FinalizeActivationAsync(db, sub, transactionRef, ct);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Pay an existing unpaid invoice
    // ─────────────────────────────────────────────────────────────────────────
    public async Task<SubscriptionResult> PayInvoiceAsync(
        int invoiceId, Guid tenantId, CancellationToken ct = default)
    {
        var gateway = await gatewayService.GetActiveAsync();
        if (gateway is null)
            return new(false, "Payment processing is temporarily unavailable. Please try again later.");

        await using var db = await factory.CreateDbContextAsync(ct);

        var invoice = await db.Invoices
            .Include(i => i.Tenant)
            .FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId, ct);

        if (invoice is null) return new(false, "Invoice not found.");
        if (invoice.Status == InvoiceStatus.Paid) return new(false, "Invoice is already paid.");
        if (invoice.Status == InvoiceStatus.Void) return new(false, "Invoice is void.");

        // Stub transaction — replace with real gateway charge
        var transactionRef = $"{gateway.Provider}-{Guid.NewGuid():N}";

        invoice.Status           = InvoiceStatus.Paid;
        invoice.PaidUtc          = DateTime.UtcNow;
        invoice.PaymentReference = transactionRef;
        await db.SaveChangesAsync(ct);

        // Mark subscription active if it was past-due
        var sub = await db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.Id == invoice.TenantSubscriptionId, ct);
        if (sub is { Status: SubscriptionStatus.PastDue or SubscriptionStatus.Suspended })
        {
            sub.Status = SubscriptionStatus.Active;
            await db.SaveChangesAsync(ct);
        }

        // Send updated invoice email
        var admin = (await userManager.GetUsersInRoleAsync("TenantAdmin"))
            .FirstOrDefault(u => u.TenantId == tenantId);
        if (admin?.Email is not null)
            await billingEmail.SendInvoiceEmailAsync(invoice, admin.Email, invoice.Tenant.Name, ct);

        return new(true, null, invoice);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SuperAdmin plan change
    // ─────────────────────────────────────────────────────────────────────────
    public async Task<SubscriptionResult> AdminChangePlanAsync(
        Guid tenantId, int newPlanId, BillingCycle newCycle, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var newPlan = await db.SubscriptionPlans.FindAsync([newPlanId], ct);
        if (newPlan is null || !newPlan.IsActive)
            return new(false, "Selected plan is not available.");

        // Cancel existing active subscription
        var existing = await db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId && s.Status == SubscriptionStatus.Active)
            .ToListAsync(ct);
        foreach (var s in existing)
        {
            s.Status  = SubscriptionStatus.Cancelled;
            s.EndDate = DateTime.UtcNow;
        }

        var renewal = newCycle == BillingCycle.Annual
            ? DateTime.UtcNow.AddYears(1)
            : DateTime.UtcNow.AddMonths(1);

        var newSub = new TenantSubscription
        {
            TenantId             = tenantId,
            SubscriptionPlanId   = newPlanId,
            BillingCycle         = newCycle,
            Status               = SubscriptionStatus.Active,
            StartDate            = DateTime.UtcNow,
            NextRenewalDate      = renewal,
            SnapshotMaxUsers     = newPlan.MaxUsersPerTenant,
            SnapshotMaxDatabases = newPlan.MaxDatabases,
            SnapshotMaxAgents    = newPlan.MaxAgents,
            SnapshotMonthlyUsage = newPlan.MonthlyUsageLimitUnits,
        };
        db.TenantSubscriptions.Add(newSub);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("SuperAdmin changed plan for tenant {TenantId} → plan {PlanId}", tenantId, newPlanId);
        return new(true, null);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Renewal (called by Hangfire job)
    // ─────────────────────────────────────────────────────────────────────────
    public async Task ProcessRenewalAsync(int subscriptionId, CancellationToken ct = default)
    {
        var gateway = await gatewayService.GetActiveAsync();

        await using var db = await factory.CreateDbContextAsync(ct);

        var sub = await db.TenantSubscriptions
            .Include(s => s.SubscriptionPlan)
            .Include(s => s.Tenant)
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, ct);

        if (sub is null) return;

        // Advance renewal date first
        sub.NextRenewalDate = sub.BillingCycle == BillingCycle.Annual
            ? sub.NextRenewalDate.AddYears(1)
            : sub.NextRenewalDate.AddMonths(1);

        var plan  = sub.SubscriptionPlan;
        var price = sub.BillingCycle == BillingCycle.Annual
            ? plan.AnnualPrice
            : plan.MonthlyPrice;

        if (price == 0m)
        {
            await db.SaveChangesAsync(ct);
            // Generate a $0 invoice for record-keeping
            var freeInvoice = await invoiceService.GenerateAsync(sub, "FREE", ct);
            await SendInvoiceToAdminAsync(freeInvoice, sub.Tenant, ct);
            return;
        }

        if (gateway is null)
        {
            // Generate unpaid invoice; mark subscription PastDue
            sub.Status = SubscriptionStatus.PastDue;
            await db.SaveChangesAsync(ct);

            var unpaidInvoice = await invoiceService.GenerateAsync(sub, null, ct);
            await SendInvoiceToAdminAsync(unpaidInvoice, sub.Tenant, ct);

            await NotifySuperAdminsAsync(sub.Tenant.Name, sub.Tenant.Id, ct);
            await billingEmail.SendGatewayUnconfiguredAlertAsync(sub.Tenant.Name, sub.Tenant.Id, ct);
            return;
        }

        // Charge and generate paid invoice
        var transactionRef = $"{gateway.Provider}-RENEWAL-{Guid.NewGuid():N}";
        await db.SaveChangesAsync(ct);

        var invoice = await invoiceService.GenerateAsync(sub, transactionRef, ct);
        await SendInvoiceToAdminAsync(invoice, sub.Tenant, ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<SubscriptionResult> FinalizeActivationAsync(
        ApplicationDbContext db, TenantSubscription sub,
        string? paymentRef, CancellationToken ct)
    {
        var renewal = sub.BillingCycle == BillingCycle.Annual
            ? DateTime.UtcNow.AddYears(1)
            : DateTime.UtcNow.AddMonths(1);

        sub.Status          = SubscriptionStatus.Active;
        sub.StartDate       = DateTime.UtcNow;
        sub.NextRenewalDate = renewal;

        var tenant = await db.Tenants.FindAsync([sub.TenantId], ct);
        if (tenant is not null) tenant.IsActive = true;

        await db.SaveChangesAsync(ct);

        var invoice = await invoiceService.GenerateAsync(sub, paymentRef, ct);
        await SendInvoiceToAdminAsync(invoice, sub.Tenant ?? tenant!, ct);

        return new(true, null, invoice);
    }

    private async Task NotifySuperAdminsAsync(string tenantName, Guid tenantId, CancellationToken ct)
    {
        var admins = await userManager.GetUsersInRoleAsync("SuperAdmin");
        foreach (var admin in admins)
        {
            await notificationService.NotifyAsync(
                admin.Id,
                "⚠️ Payment Gateway Not Configured",
                $"Tenant '{tenantName}' could not be activated because no payment gateway is configured.",
                "/Admin/PaymentGateways",
                ct);
        }
    }

    private async Task SendInvoiceToAdminAsync(Invoice invoice, Tenant tenant, CancellationToken ct)
    {
        var admins = await userManager.GetUsersInRoleAsync("TenantAdmin");
        var admin  = admins.FirstOrDefault(u => u.TenantId == tenant.Id);
        if (admin?.Email is not null)
            await billingEmail.SendInvoiceEmailAsync(invoice, admin.Email, tenant.Name, ct);
    }
}
