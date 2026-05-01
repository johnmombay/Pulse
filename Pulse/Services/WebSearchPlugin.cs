using Microsoft.SemanticKernel;
using Pulse.Infrastructure;
using System.ComponentModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pulse.Services;

/// <summary>
/// Semantic Kernel plugin that searches the web using DuckDuckGo's free public API.
/// No API key required. Enabled when <see cref="Models.WebSearchSettings.IsEnabled"/> is <c>true</c>.
/// </summary>
public sealed class WebSearchPlugin(
    LlmSettingsService settingsService,
    ITenantContext tenantContext,
    IHttpClientFactory httpClientFactory,
    ILogger<WebSearchPlugin> logger)
{
    [KernelFunction("search_web")]
    [Description(
        "Search the web for current information using DuckDuckGo. " +
        "Use this to find up-to-date facts, news, documentation, or any information not in your training data. " +
        "Returns a summary and a list of relevant results with titles and snippets.")]
    public async Task<string> SearchWebAsync(
        [Description("The search query to look up.")]
        string query,

        CancellationToken ct = default)
    {
        var settings = (await settingsService.GetAsync(tenantContext.TenantId ?? Guid.Empty)).WebSearch;

        if (!settings.IsEnabled)
            return "Web search is disabled. Enable it in Settings → Web Search.";

        if (string.IsNullOrWhiteSpace(query))
            return "Query cannot be empty.";

        var maxResults = Math.Clamp(settings.MaxResults, 1, 20);

        logger.LogInformation("WebSearch: query={Query} maxResults={Max}", query, maxResults);

        var client = httpClientFactory.CreateClient("WebSearch");

        var sb = new StringBuilder();

        // ── DuckDuckGo Instant Answer API ─────────────────────────────────────
        try
        {
            var iaUrl = $"https://api.duckduckgo.com/?q={Uri.EscapeDataString(query)}&format=json&no_html=1&skip_disambig=1";
            var iaJson = await client.GetStringAsync(iaUrl, ct);
            using var doc = JsonDocument.Parse(iaJson);
            var root = doc.RootElement;

            var abstractText = root.TryGetProperty("Abstract", out var abs) ? abs.GetString() : null;
            var answer       = root.TryGetProperty("Answer",   out var ans) ? ans.GetString() : null;
            var definition   = root.TryGetProperty("Definition", out var def) ? def.GetString() : null;

            if (!string.IsNullOrWhiteSpace(answer))
                sb.AppendLine($"**Instant Answer:** {answer}").AppendLine();

            if (!string.IsNullOrWhiteSpace(abstractText))
                sb.AppendLine($"**Summary:** {abstractText}").AppendLine();

            if (!string.IsNullOrWhiteSpace(definition))
                sb.AppendLine($"**Definition:** {definition}").AppendLine();

            // Related topics
            if (root.TryGetProperty("RelatedTopics", out var topics) &&
                topics.ValueKind == JsonValueKind.Array)
            {
                var added = 0;
                sb.AppendLine("**Related Results:**");
                foreach (var topic in topics.EnumerateArray())
                {
                    if (added >= maxResults) break;
                    if (topic.TryGetProperty("Text", out var text) &&
                        topic.TryGetProperty("FirstURL", out var url))
                    {
                        var t = text.GetString();
                        var u = url.GetString();
                        if (!string.IsNullOrWhiteSpace(t) && !string.IsNullOrWhiteSpace(u))
                        {
                            sb.AppendLine($"- [{t}]({u})");
                            added++;
                        }
                    }
                }
                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WebSearch: Instant Answer API failed for query={Query}", query);
        }

        // ── DuckDuckGo HTML scrape (actual search results) ────────────────────
        try
        {
            var htmlUrl = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
            var html = await client.GetStringAsync(htmlUrl, ct);

            var results = ParseHtmlResults(html, maxResults);
            if (results.Count > 0)
            {
                sb.AppendLine("**Search Results:**");
                foreach (var (title, snippet, link) in results)
                {
                    sb.AppendLine($"**{title}**");
                    if (!string.IsNullOrWhiteSpace(snippet))
                        sb.AppendLine(snippet);
                    if (!string.IsNullOrWhiteSpace(link))
                        sb.AppendLine($"Source: {link}");
                    sb.AppendLine();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WebSearch: HTML scrape failed for query={Query}", query);
        }

        var result = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(result)
            ? $"No results found for: {query}"
            : result;
    }

    /// <summary>
    /// Parses DuckDuckGo HTML results page for titles, snippets, and URLs.
    /// </summary>
    private static List<(string Title, string Snippet, string Link)> ParseHtmlResults(
        string html, int max)
    {
        var results = new List<(string, string, string)>();

        // Match result blocks: <a class="result__a" href="...">title</a>
        var titlePattern  = new Regex(@"<a[^>]+class=""result__a""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var snippetPattern = new Regex(@"<a[^>]+class=""result__snippet""[^>]*>(.*?)</a>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var stripTags = new Regex(@"<[^>]+>");

        var titleMatches   = titlePattern.Matches(html);
        var snippetMatches = snippetPattern.Matches(html);

        for (var i = 0; i < Math.Min(titleMatches.Count, max); i++)
        {
            var link    = titleMatches[i].Groups[1].Value.Trim();
            var title   = stripTags.Replace(titleMatches[i].Groups[2].Value, "").Trim();
            var snippet = i < snippetMatches.Count
                ? stripTags.Replace(snippetMatches[i].Groups[1].Value, "").Trim()
                : "";

            // Decode HTML entities minimally
            title   = DecodeHtmlEntities(title);
            snippet = DecodeHtmlEntities(snippet);

            if (!string.IsNullOrWhiteSpace(title))
                results.Add((title, snippet, link));
        }

        return results;
    }

    private static string DecodeHtmlEntities(string s) =>
        s.Replace("&amp;", "&")
         .Replace("&lt;",  "<")
         .Replace("&gt;",  ">")
         .Replace("&quot;", "\"")
         .Replace("&#39;",  "'")
         .Replace("&nbsp;", " ");
}
