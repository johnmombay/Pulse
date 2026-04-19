namespace Pulse.Models;

/// <summary>
/// A named set of system-prompt instructions that customise the agent's behaviour.
/// Active skills are injected at the start of every new agent session.
/// </summary>
public class SkillConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Display name, e.g. "Business Analyst".</summary>
    public string Name { get; set; } = "";

    /// <summary>Single emoji shown in the UI alongside the name.</summary>
    public string Icon { get; set; } = "⚡";

    /// <summary>Short human-readable description of what the skill does.</summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// The actual system-prompt text injected into the agent.
    /// Supports Markdown formatting.
    /// </summary>
    public string Instructions { get; set; } = "";

    public bool IsActive { get; set; } = true;
}
