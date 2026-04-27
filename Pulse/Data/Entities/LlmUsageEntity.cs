namespace Pulse.Data.Entities;

/// <summary>
/// One row per LLM call — captures prompt / completion / total token counts so
/// SuperAdmin can monitor consumption across all tenants and tenants can see
/// their own monthly burn-down. Tenant-owned: the global query filter on
/// <c>ApplicationDbContext</c> restricts non-SuperAdmin reads to the caller's tenant.
/// </summary>
public class LlmUsageEntity : ITenantOwned
{
    public long Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>ASP.NET Core Identity user id of the caller. Empty for system / job calls.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Logical agent that produced the call: "Orchestrator", a specialist agent name,
    /// "ScheduledTask", "Workflow", "MemoryExtraction", etc.
    /// </summary>
    public string AgentName { get; set; } = string.Empty;

    /// <summary>Model identifier used for this call (e.g. <c>openai/gpt-4o</c>, <c>anthropic/claude-3-5-sonnet</c>).</summary>
    public string ModelId { get; set; } = string.Empty;

    public int PromptTokens     { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens      { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
