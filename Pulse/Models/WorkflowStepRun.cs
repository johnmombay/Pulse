using System.ComponentModel.DataAnnotations;

namespace Pulse.Models;

public class WorkflowStepRun
{
    public int Id { get; set; }
    public int WorkflowRunId { get; set; }
    public int StepOrder { get; set; }

    [MaxLength(200)]
    public string StepName { get; set; } = "";

    public StepType StepType { get; set; }
    public string Output { get; set; } = "";
    public bool IsSuccess { get; set; } = true;

    [MaxLength(10)]
    public string? Decision { get; set; }

    public DateTime RunAt { get; set; } = DateTime.UtcNow;

    public WorkflowRun? WorkflowRun { get; set; }
}
