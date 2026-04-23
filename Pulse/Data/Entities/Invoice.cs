using Pulse.Models;

namespace Pulse.Data.Entities;

public enum InvoiceStatus { Unpaid, Paid, Void }

public class Invoice
{
    public int    Id                   { get; set; }
    public Guid   TenantId             { get; set; }
    public int    TenantSubscriptionId { get; set; }

    /// <summary>Human-readable number, e.g. INV-2026-00042.</summary>
    public string        Number      { get; set; } = string.Empty;
    public decimal       Amount      { get; set; }
    public InvoiceStatus Status      { get; set; } = InvoiceStatus.Unpaid;
    public BillingCycle  BillingCycle { get; set; }

    public DateTime  PeriodStart { get; set; }
    public DateTime  PeriodEnd   { get; set; }
    public DateTime  IssuedUtc   { get; set; } = DateTime.UtcNow;
    public DateTime  DueDate     { get; set; }
    public DateTime? PaidUtc     { get; set; }

    /// <summary>Gateway transaction reference after a successful charge.</summary>
    public string? PaymentReference { get; set; }

    // Navigation
    public Tenant             Tenant             { get; set; } = null!;
    public TenantSubscription TenantSubscription { get; set; } = null!;
}
