using System.Collections.Concurrent;

namespace Pulse.Services;

/// <summary>
/// Thread-safe Gemini API key rotation with built-in rate limiting and
/// per-key cooldown after a 429 (Too Many Requests) response.
/// Keys are supplied per-call (sourced from LlmSettingsService) so the Settings page
/// changes take effect immediately without restarting the app.
/// A minimum of 5 seconds is enforced between calls.
/// </summary>
public sealed class GeminiKeyRotationService
{
    private int _keyIndex = -1;
    private readonly SemaphoreSlim _rateLimitSemaphore = new(1, 1);
    private DateTime _lastCallTime = DateTime.MinValue;
    private static readonly TimeSpan MinCallInterval = TimeSpan.FromSeconds(5);

    // key -> UTC timestamp until which the key is considered rate-limited
    private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();

    /// <summary>
    /// Returns the next API key in round-robin order, skipping keys whose
    /// cooldown has not yet expired. Throws when every key is cooling down
    /// or the supplied list is empty.
    /// </summary>
    public string GetNextKey(IReadOnlyList<string> keys)
    {
        if (keys.Count == 0)
            throw new InvalidOperationException(
                "No API keys are configured. Add at least one Gemini API key in Settings → LLM.");

        var now = DateTime.UtcNow;
        for (var i = 0; i < keys.Count; i++)
        {
            var idx = (int)((uint)Interlocked.Increment(ref _keyIndex) % (uint)keys.Count);
            var candidate = keys[idx];
            if (!_cooldowns.TryGetValue(candidate, out var until) || until <= now)
                return candidate;
        }

        // All keys are cooling down — report when the soonest one recovers.
        var soonest = keys
            .Select(k => _cooldowns.TryGetValue(k, out var u) ? u : DateTime.MinValue)
            .Min();
        var wait = soonest - now;

        throw new InvalidOperationException(
            $"All {keys.Count} Gemini API key(s) are rate-limited. " +
            $"Try again in ~{Math.Max(1, (int)wait.TotalSeconds)}s, or add more keys in Settings → LLM.");
    }

    /// <summary>
    /// Mark the given key as rate-limited for <paramref name="duration"/>.
    /// Subsequent calls to <see cref="GetNextKey"/> will skip it until the cooldown expires.
    /// </summary>
    public void MarkRateLimited(string key, TimeSpan duration)
    {
        var until = DateTime.UtcNow.Add(duration);
        _cooldowns.AddOrUpdate(key, until, (_, existing) => existing > until ? existing : until);
    }

    /// <summary>
    /// Acquires a rate-limit slot, waiting at least 5 seconds since the last call.
    /// Serializes concurrent callers so Gemini rate limits are not exceeded.
    /// </summary>
    public async Task EnforceRateLimitAsync(CancellationToken cancellationToken = default)
    {
        await _rateLimitSemaphore.WaitAsync(cancellationToken);
        try
        {
            var elapsed = DateTime.UtcNow - _lastCallTime;
            if (elapsed < MinCallInterval)
                await Task.Delay(MinCallInterval - elapsed, cancellationToken);

            _lastCallTime = DateTime.UtcNow;
        }
        finally
        {
            _rateLimitSemaphore.Release();
        }
    }
}
