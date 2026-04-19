using System.ComponentModel.DataAnnotations;

namespace Pulse.Models;

public class WorkflowDefinition
{
    public int Id { get; set; }

    [Required, MaxLength(450)]
    public string UserId { get; set; } = "";

    [Required, MaxLength(200)]
    public string Title { get; set; } = "";

    [Required]
    public string GoalDescription { get; set; } = "";

    public bool IsEnabled { get; set; } = true;

    public List<WorkflowStep> Steps { get; set; } = [];
    public List<WorkflowRun>  Runs  { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
