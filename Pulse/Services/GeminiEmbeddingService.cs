using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Pulse.Services;

/// <summary>
/// Calls the Gemini embedding API to produce 768-dimensional vectors.
/// Uses <c>embedding-001</c> by default — available on every Gemini Developer
/// API key via v1beta.  Override in appsettings.json: "Gemini:EmbeddingModel".
/// </summary>
public sealed class GeminiEmbeddingService(
    IHttpClientFactory httpClientFactory,
    LlmSettingsService settingsService,
    IConfiguration configuration,
    ILogger<GeminiEmbeddingService> logger)
{
    // "embedding-001" works for all free-tier Gemini Developer API keys on v1beta.
    // "text-embedding-004" returns HTTP 404 on v1beta for most key tiers.
    private string Model => configuration["Gemini:EmbeddingModel"] ?? "embedding-001";

    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

    /// <summary>
    /// Returns <c>(vector, null)</c> on success or <c>(null, errorMessage)</c> on failure.
    /// Never throws.
    /// </summary>
    public async Task<(float[]? Vector, string? Error)> EmbedAsync(
        string text, CancellationToken ct = default)
    {
        var keys = await settingsService.GetApiKeysAsync();
        if (keys.Count == 0)
            return (null, "No Gemini API keys configured in Settings.");

        var model  = Model;
        var apiKey = keys[0];
        var url    = $"{BaseUrl}/{model}:embedContent?key={Uri.EscapeDataString(apiKey)}";

        var body = new
        {
            model   = $"models/{model}",
            content = new { parts = new[] { new { text } } }
        };

        try
        {
            var client   = httpClientFactory.CreateClient("GeminiEmbed");
            var response = await client.PostAsJsonAsync(url, body, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body2 = await response.Content.ReadAsStringAsync(ct);
                // Trim noisy error bodies to a readable length
                var snippet = body2.Length > 300 ? body2[..300] + "…" : body2;
                var msg = $"HTTP {(int)response.StatusCode} — {snippet}";
                logger.LogError("Gemini embedding API error: {Msg}", msg);
                return (null, msg);
            }

            var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(ct);
            var vec    = result?.Embedding?.Values;

            if (vec is not { Length: > 0 })
                return (null, "API returned an empty embedding vector — unexpected response shape.");

            return (vec, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Embedding call threw an exception for text of length {Len}", text.Length);
            return (null, ex.Message);
        }
    }

    private record EmbeddingResponse(
        [property: JsonPropertyName("embedding")] EmbeddingValues? Embedding);

    private record EmbeddingValues(
        [property: JsonPropertyName("values")] float[]? Values);
}
