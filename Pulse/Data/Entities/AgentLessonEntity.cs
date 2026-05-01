using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace Pulse.Data.Entities;

/// <summary>
/// A lesson learned from a completed or failed agent execution, persisted per tenant.
/// The system injects relevant lessons before future executions of the same domain/goal
/// so every agent can self-improve through its own historical experience.
/// </summary>
public class AgentLessonEntity : ITenantOwned
{
    public int  Id       { get; set; }
    public Guid TenantId { get; set; }

    [Required, MaxLength(450)]
    public string UserId { get; set; } = "";

    /// <summary>Specialist agent this lesson belongs to; null = global / orchestrator.</summary>
    [MaxLength(64)]
    public string? AgentDefinitionId { get; set; }

    /// <summary>Execution domain: Chat | SubAgent | Workflow | Scheduler | Telegram</summary>
    [Required, MaxLength(50)]
    public string Domain { get; set; } = "";

    /// <summary>The original goal/instruction that was evaluated.</summary>
    [Required, MaxLength(1000)]
    public string GoalText { get; set; } = "";

    /// <summary>A summary of the action taken (prompt snippet, step name, etc.).</summary>
    [MaxLength(2000)]
    public string ActionTaken { get; set; } = "";

    /// <summary>Snippet of the outcome that was evaluated.</summary>
    public string Outcome { get; set; } = "";

    /// <summary>Distilled, actionable lesson extracted from this experience.</summary>
    public string LessonText { get; set; } = "";

    /// <summary>Reflection score 0.0 (total failure) – 1.0 (perfect). Stored for ranking.</summary>
    public double Score { get; set; }

    /// <summary>Number of times this lesson has been reinforced by subsequent similar outcomes.</summary>
    public int ReinforcementCount { get; set; } = 1;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>JSON-serialised float[] embedding of <see cref="GoalText"/> for semantic retrieval.</summary>
    public string? EmbeddingJson { get; set; }

    [NotMapped]
    public float[]? Embedding
    {
        get => EmbeddingJson is null ? null
            : JsonSerializer.Deserialize<float[]>(EmbeddingJson);
        set => EmbeddingJson = value is null ? null
            : JsonSerializer.Serialize(value);
    }
}
