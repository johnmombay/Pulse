using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pulse.Services;

/// <summary>
/// Thin HTTP wrapper around the AgentMail REST API (https://api.agentmail.to/v0).
/// All endpoints are authenticated with a Bearer token read live from <see cref="LlmSettingsService"/>
/// so credential changes take effect without a restart.
/// </summary>
public sealed class AgentMailService(
    IHttpClientFactory httpClientFactory,
    LlmSettingsService settingsService,
    ILogger<AgentMailService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    // ── Client factory ────────────────────────────────────────────────────────

    private HttpClient CreateClient()
    {
        var s      = settingsService.Get().AgentMail;
        var client = httpClientFactory.CreateClient("AgentMail");
        client.BaseAddress = new Uri(s.BaseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", s.ApiKey);
        return client;
    }

    // ── Read operations ───────────────────────────────────────────────────────

    public async Task<string> ListThreadsAsync(
        string inbox, int limit = 20, string? pageToken = null,
        CancellationToken ct = default)
    {
        var client = CreateClient();
        var url    = $"inboxes/{Uri.EscapeDataString(inbox)}/threads?limit={limit}";
        if (!string.IsNullOrWhiteSpace(pageToken))
            url += $"&page_token={Uri.EscapeDataString(pageToken)}";

        logger.LogDebug("AgentMail GET {Url}", url);
        return await client.GetStringAsync(url, ct);
    }

    public async Task<string> GetThreadAsync(
        string inbox, string threadId,
        CancellationToken ct = default)
    {
        var client = CreateClient();
        var url    = $"inboxes/{Uri.EscapeDataString(inbox)}/threads/{Uri.EscapeDataString(threadId)}";
        logger.LogDebug("AgentMail GET {Url}", url);
        return await client.GetStringAsync(url, ct);
    }

    public async Task<string> ListMessagesAsync(
        string inbox, int limit = 20, string? pageToken = null,
        CancellationToken ct = default)
    {
        var client = CreateClient();
        var url    = $"inboxes/{Uri.EscapeDataString(inbox)}/messages?limit={limit}";
        if (!string.IsNullOrWhiteSpace(pageToken))
            url += $"&page_token={Uri.EscapeDataString(pageToken)}";

        logger.LogDebug("AgentMail GET {Url}", url);
        return await client.GetStringAsync(url, ct);
    }

    public async Task<string> GetMessageAsync(
        string inbox, string messageId,
        CancellationToken ct = default)
    {
        var client = CreateClient();
        var url    = $"inboxes/{Uri.EscapeDataString(inbox)}/messages/{Uri.EscapeDataString(messageId)}";
        logger.LogDebug("AgentMail GET {Url}", url);
        return await client.GetStringAsync(url, ct);
    }

    // ── Send ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a new email or replies to an existing thread.
    /// Pass <paramref name="inReplyTo"/> (a message_id string) to thread the reply correctly.
    /// </summary>
    public async Task<string> SendEmailAsync(
        string    inbox,
        string[]  to,
        string    subject,
        string    text,
        string?   html        = null,
        string[]? cc          = null,
        string[]? bcc         = null,
        string?   inReplyTo   = null,
        CancellationToken ct  = default)
    {
        var client = CreateClient();

        var payload = new SendEmailPayload
        {
            To         = to,
            Cc         = cc?.Length > 0  ? cc  : null,
            Bcc        = bcc?.Length > 0 ? bcc : null,
            Subject    = string.IsNullOrWhiteSpace(subject) ? null : subject,
            Text       = text,
            Html       = html,
            InReplyTo  = inReplyTo,
        };

        var json    = JsonSerializer.Serialize(payload, JsonOpts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        logger.LogDebug("AgentMail POST messages/send: to={To} subject={Subject}",
            string.Join(", ", to), subject);

        var resp = await client.PostAsync(
            $"inboxes/{Uri.EscapeDataString(inbox)}/messages/send", content, ct);

        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"AgentMail API error ({(int)resp.StatusCode} {resp.StatusCode}): {body}");

        return body;
    }

    // ── Private DTOs ──────────────────────────────────────────────────────────

    private sealed class SendEmailPayload
    {
        [JsonPropertyName("to")]
        public string[] To { get; init; } = [];

        [JsonPropertyName("cc")]
        public string[]? Cc { get; init; }

        [JsonPropertyName("bcc")]
        public string[]? Bcc { get; init; }

        [JsonPropertyName("subject")]
        public string? Subject { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("html")]
        public string? Html { get; init; }

        [JsonPropertyName("in_reply_to")]
        public string? InReplyTo { get; init; }
    }
}
