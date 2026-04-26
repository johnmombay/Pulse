namespace Pulse.Services;

/// <summary>
/// Provides the active OpenRouter API key for agent kernel construction.
/// Replaces the former GeminiKeyRotationService — OpenRouter handles load-balancing
/// on their end so no client-side key rotation is needed.
/// </summary>
public sealed class OpenRouterService
{
    public const string BaseUrl = "https://openrouter.ai/api/v1";

    /// <summary>
    /// Returns the first non-empty key from <paramref name="keys"/>.
    /// Throws <see cref="InvalidOperationException"/> when the list is empty or all entries are blank.
    /// </summary>
    public string GetApiKey(IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var key = keys.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k));
        if (key is null)
            throw new InvalidOperationException(
                "No OpenRouter API key is configured. Add one in Settings → LLM.");
        return key;
    }
}
