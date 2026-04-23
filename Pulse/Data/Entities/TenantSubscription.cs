namespace Pulse.Data.Entities;

public enum BillingCycle   { Monthly, Annual }
public enum SubscriptionStatus { Active, Cancelled, Suspended, PastDue, Trial, Pending }

public class TenantSubscription
{
    public int  Id       { get; set; }
    public Guid TenantId { get; set; }
    public int  SubscriptionPlanId { get; set; }

    public BillingCycle        BillingCycle { get; set; }
    public SubscriptionStatus  Status       { get; set; } = SubscriptionStatus.Active;

    public DateTime  StartDate       { get; set; } = DateTime.UtcNow;
    public DateTime? EndDate         { get; set; }
    public DateTime? TrialEndsAt     { get; set; }
    public DateTime  NextRenewalDate { get; set; } = DateTime.UtcNow.AddMonths(1);

    // Snapshot of limits at the time of subscription — prevents retroactive plan
    // edits from silently breaking a tenant's current entitlements.
    public int SnapshotMaxUsers        { get; set; }
    public int SnapshotMaxDatabases    { get; set; }
    public int SnapshotMaxAgents       { get; set; }
    public int SnapshotMonthlyUsage    { get; set; }

    // Navigation
    public Tenant           Tenant           { get; set; } = null!;
    public SubscriptionPlan SubscriptionPlan { get; set; } = null!;
    public ICollection<Invoice> Invoices     { get; set; } = [];
}
