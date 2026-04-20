using System.ComponentModel.DataAnnotations;
using Pulse.Data.Entities;

namespace Pulse.Models;

/// <summary>
/// A user-defined scheduled agent task.
/// The agent runs the <see cref="Instructions"/> on the specified cadence
/// and delivers the result to the dashboard and/or an email address.
/// </summary>
public class ScheduledTask : ITenantOwned
{
    public int    Id           { get; set; }

    public Guid TenantId { get; set; }

    [Required, MaxLength(450)]
    public string UserId       { get; set; } = "";

    [Required, MaxLength(200)]
    public string Title        { get; set; } = "";

    /// <summary>What the agent should do when triggered (free-form instructions).</summary>
    [Required]
    public string Instructions { get; set; } = "";

    public FrequencyType FrequencyType  { get; set; } = FrequencyType.Days;
    public int           FrequencyValue { get; set; } = 1;

    /// <summary>For OneTime tasks: when to run.</summary>
    public DateTime? ScheduledAt { get; set; }

    public DeliveryType DeliveryType  { get; set; } = DeliveryType.Dashboard;

    [MaxLength(320)]
    public string? DeliveryEmail { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Hangfire recurring job ID (null for one-time tasks).</summary>
    [MaxLength(100)]
    public string? HangfireJobId { get; set; }

    public DateTime? LastRunAt  { get; set; }
    public DateTime? NextRunAt  { get; set; }

    /// <summary>idle | running | success | error</summary>
    [MaxLength(20)]
    public string  LastStatus { get; set; } = "idle";

    public string? LastError  { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When set, the scheduled job runs this workflow instead of <see cref="Instructions"/>.
    /// </summary>
    public int? WorkflowDefinitionId { get; set; }
    public WorkflowDefinition? WorkflowDefinition { get; set; }

    // Navigation
    public ICollection<ScheduledTaskResult> Results { get; set; } = [];
}
