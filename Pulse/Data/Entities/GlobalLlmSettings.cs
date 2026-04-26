namespace Pulse.Data.Entities;

/// <summary>
/// Global LLM configuration managed exclusively by SuperAdmin. Every tenant uses the
/// <see cref="ModelId"/> stored in this singleton row.
/// Singleton row enforced by CHECK constraint (Id = 1).
/// </summary>
public class GlobalLlmSettings
{
    public int Id { get; set; } = 1;

    /// <summary>
    /// OpenRouter model identifier (e.g. <c>openai/gpt-4o</c>, <c>anthropic/claude-3-5-sonnet</c>).
    /// </summary>
    public string ModelId { get; set; } = string.Empty;
}
