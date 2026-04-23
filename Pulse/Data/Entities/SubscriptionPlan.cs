namespace Pulse.Data.Entities;

public class SubscriptionPlan
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    // ── Limits ────────────────────────────────────────────────────────────────
    public int MaxUsersPerTenant     { get; set; }
    public int MaxDatabases          { get; set; }   // databases & data sources
    public int MaxAgents             { get; set; }
    public int MonthlyUsageLimitUnits { get; set; }  // e.g. LLM tokens / API calls

    // ── Base pricing ──────────────────────────────────────────────────────────
    public decimal MonthlyPrice { get; set; }
    public decimal AnnualPrice  { get; set; }

    // ── Overage pricing (per unit beyond the plan limit) ──────────────────────
    public decimal OveragePricePerUser     { get; set; }
    public decimal OveragePricePerDatabase { get; set; }
    public decimal OveragePricePerAgent    { get; set; }
    public decimal OveragePricePerUsageUnit { get; set; }

    public bool     IsActive   { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // Navigation
    public ICollection<TenantSubscription> TenantSubscriptions { get; set; } = [];
}
