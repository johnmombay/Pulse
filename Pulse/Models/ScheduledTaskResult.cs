using System.ComponentModel.DataAnnotations;

namespace Pulse.Models;

/// <summary>
/// Stores the output of a single scheduled task execution for dashboard display.
/// </summary>
public class ScheduledTaskResult
{
    public int    Id              { get; set; }
    public int    ScheduledTaskId { get; set; }

    [Required, MaxLength(450)]
    public string UserId    { get; set; } = "";

    [MaxLength(200)]
    public string TaskTitle { get; set; } = "";

    public string Output    { get; set; } = "";
    public bool   IsSuccess { get; set; } = true;
    public bool   IsRead    { get; set; } = false;
    public DateTime RunAt   { get; set; } = DateTime.UtcNow;

    // Navigation
    public ScheduledTask? ScheduledTask { get; set; }
}
