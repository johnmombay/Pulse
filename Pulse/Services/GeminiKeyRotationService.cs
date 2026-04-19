namespace Pulse.Services;

/// <summary>
/// Thread-safe Gemini API key rotation with built-in rate limiting.
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

    /// <summary>
    /// Returns the next API key in round-robin rotation from the supplied list.
    /// Throws if the list is empty (prompts user to configure keys in Settings).
    /// </summary>
    public string GetNextKey(IReadOnlyList<string> keys)
    {
        if (keys.Count == 0)
            throw new InvalidOperationException(
                "No API keys are configured. Add at least one Gemini API key in Settings → LLM.");

        var index = (int)((uint)Interlocked.Increment(ref _keyIndex) % (uint)keys.Count);
        return keys[index];
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
