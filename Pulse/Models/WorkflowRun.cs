using System.ComponentModel.DataAnnotations;
using Pulse.Data.Entities;

namespace Pulse.Models;

public class WorkflowRun : ITenantOwned
{
    public int Id { get; set; }
    public int WorkflowDefinitionId { get; set; }

    public Guid TenantId { get; set; }

    [Required, MaxLength(450)]
    public string UserId { get; set; } = "";

    [MaxLength(200)]
    public string WorkflowTitle { get; set; } = "";

    [MaxLength(20)]
    public string Status { get; set; } = "pending";

    public string? StopReason { get; set; }

    public DateTime  StartedAt  { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }

    public List<WorkflowStepRun> StepRuns { get; set; } = [];

    public WorkflowDefinition? WorkflowDefinition { get; set; }
}
