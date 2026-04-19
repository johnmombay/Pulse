using System.ComponentModel.DataAnnotations;

namespace Pulse.Models;

public class WorkflowStep
{
    public int Id { get; set; }
    public int WorkflowDefinitionId { get; set; }
    public int Order { get; set; }

    [Required, MaxLength(200)]
    public string Name { get; set; } = "";

    [Required]
    public string Instructions { get; set; } = "";

    public StepType StepType { get; set; } = StepType.Execute;

    [MaxLength(1000)]
    public string? WebhookUrl { get; set; }

    /// <summary>n8n | Zapier | Make.com | Custom</summary>
    [MaxLength(20)]
    public string? WebhookPlatform { get; set; }

    /// <summary>Target workflow when StepType is CallWorkflow.</summary>
    public int? CalledWorkflowId { get; set; }
    public WorkflowDefinition? CalledWorkflow { get; set; }

    public WorkflowDefinition? WorkflowDefinition { get; set; }
}
