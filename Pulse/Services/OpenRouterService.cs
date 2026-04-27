using System.Collections.Concurrent;

namespace Pulse.Services;

/// <summary>
/// Provides OpenRouter API key selection with per-key rate-limit tracking.
/// When a key receives a 429, callers mark it via <see cref="MarkRateLimited"/>
/// and the next <see cref="GetApiKey"/> call automatically skips it until the
/// cooldown expires, rotating to the next available key.
/// </summary>
public sealed class OpenRouterService
{
    public const string BaseUrl = "https://openrouter.ai/api/v1";

    // Per-key expiry: the key is considered rate-limited until this timestamp passes.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _rateLimited = new();

    /// <summary>
    /// Returns the first non-empty, non-rate-limited key from <paramref name="keys"/>.
    /// Throws <see cref="InvalidOperationException"/> when no usable key is available.
    /// </summary>
    public string GetApiKey(IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var now = DateTimeOffset.UtcNow;
        var key = keys.FirstOrDefault(k =>
            !string.IsNullOrWhiteSpace(k) &&
            (!_rateLimited.TryGetValue(k, out var exp) || exp <= now));

        if (key is null)
        {
            var anyConfigured = keys.Any(k => !string.IsNullOrWhiteSpace(k));
            throw new InvalidOperationException(anyConfigured
                ? "All OpenRouter API keys are currently rate-limited. Try again in a moment, " +
                  "or add more keys in Settings → LLM."
                : "No OpenRouter API key is configured. Add one in Settings → LLM.");
        }

        return key;
    }

    /// <summary>
    /// Marks <paramref name="key"/> as rate-limited for <paramref name="cooldown"/>
    /// (defaults to 12 seconds — just above the free-tier 10 s reset window on OpenRouter).
    /// Subsequent <see cref="GetApiKey"/> calls skip it until the cooldown expires.
    /// </summary>
    public void MarkRateLimited(string key, TimeSpan? cooldown = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var expiry = DateTimeOffset.UtcNow.Add(cooldown ?? TimeSpan.FromSeconds(12));
        _rateLimited[key] = expiry;
    }
}
