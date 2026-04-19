using Pulse.Data;
using Pulse.Models;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;
using System.Text.Json;

namespace Pulse.Services;

/// <summary>
/// Manages persistent agent memories stored in SQL Server.
///
/// Retrieval strategy:
///   1. <b>SharpVector</b> (in-memory Bag-of-Words) â€” instant, no API call.
///   2. <b>Gemini embedding cosine search</b> â€” when API available.
///   3. <b>Keyword fallback</b> â€” always available.
///
/// The SharpVector index is built lazily on first query and invalidated on
/// any write (add / update / delete).
/// </summary>
public sealed class MemoryService(
    ApplicationDbContext db,
    GeminiEmbeddingService embeddings,
    SharpVectorIndexService sharpVector,
    LlmSettingsService llmSettings,
    IHttpClientFactory httpClientFactory,
    ILogger<MemoryService> logger)
{
    private const int   TopK              = 8;
    private const float SimilarityThreshold = 0.55f;
    private const float DuplicateThreshold  = 0.92f;
    private const int   MaxMemoriesPerUser  = 200;

    // â”€â”€ Read â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public async Task<List<AgentMemory>> GetAllAsync(string userId)
        => await db.AgentMemories
               .Where(m => m.UserId == userId)
               .OrderByDescending(m => m.Importance)
               .ThenByDescending(m => m.UpdatedAt)
               .ToListAsync();

    /// <summary>
    /// Returns the top-K memories most relevant to <paramref name="query"/>.
    /// Three-tier: SharpVector â†’ Gemini embeddings â†’ keyword overlap.
    /// </summary>
    public async Task<List<AgentMemory>> GetRelevantAsync(
        string userId, string query, CancellationToken ct = default)
    {
        var all = await db.AgentMemories
            .Where(m => m.UserId == userId && m.IsActive)
            .ToListAsync(ct);

        if (all.Count == 0) return [];

        // â”€â”€ Ensure SharpVector index is up to date â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        if (!sharpVector.IsMemoryIndexed(userId))
            await sharpVector.IndexMemoriesAsync(userId,
                all.Select(m => (m.Id, m.Content)), ct);

        // â”€â”€ 1. SharpVector â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var svIds = sharpVector.SearchMemories(userId, query, TopK);
        if (svIds.Count > 0)
        {
            var idSet  = svIds.ToHashSet();
            var svHits = all.Where(m => idSet.Contains(m.Id)).ToList();
            logger.LogDebug("Memory: SharpVector â†’ {N} hit(s) for user {UserId}", svHits.Count, userId);
            return svHits;
        }

        // â”€â”€ 2. Gemini embedding cosine search â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var (queryVec, _) = await embeddings.EmbedAsync(query, ct);
        if (queryVec is { Length: > 0 })
        {
            var embHits = all
                .Where(m => m.Embedding is { Length: > 0 })
                .Select(m => (m, Score: CosineSimilarity(queryVec, m.Embedding!)))
                .Where(x => x.Score >= SimilarityThreshold)
                .OrderByDescending(x => x.Score)
                .Take(TopK)
                .Select(x => x.m)
                .ToList();

            if (embHits.Count > 0) return embHits;
        }

        // â”€â”€ 3. Keyword fallback â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        return KeywordSearch(all, query);
    }

    public async Task<List<AgentMemory>> GetSessionContextAsync(
        string userId, CancellationToken ct = default)
        => await db.AgentMemories
               .Where(m => m.UserId == userId && m.IsActive)
               .OrderByDescending(m => m.Importance)
               .ThenByDescending(m => m.UpdatedAt)
               .Take(20)
               .ToListAsync(ct);

    // â”€â”€ Write â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public async Task<AgentMemory> AddManualAsync(
        string userId, string content, string? category, int importance,
        CancellationToken ct = default)
    {
        var mem = new AgentMemory
        {
            UserId     = userId, Content   = content.Trim(),
            Category   = category?.Trim(), Importance = importance, Source = "manual"
        };
        var (vec, _) = await embeddings.EmbedAsync(content, ct);
        mem.Embedding = vec;
        db.AgentMemories.Add(mem);
        await db.SaveChangesAsync(ct);
        sharpVector.InvalidateMemoryIndex(userId); // force rebuild on next query
        return mem;
    }

    public async Task<bool> UpdateAsync(
        int id, string userId, string content, string? category, int importance,
        CancellationToken ct = default)
    {
        var mem = await db.AgentMemories.FirstOrDefaultAsync(m => m.Id == id && m.UserId == userId, ct);
        if (mem is null) return false;
        mem.Content = content.Trim(); mem.Category = category?.Trim();
        mem.Importance = importance;  mem.UpdatedAt = DateTime.UtcNow;
        var (vec, _) = await embeddings.EmbedAsync(content, ct);
        mem.Embedding = vec;
        await db.SaveChangesAsync(ct);
        sharpVector.InvalidateMemoryIndex(userId);
        return true;
    }

    public async Task<bool> DeleteAsync(int id, string userId, CancellationToken ct = default)
    {
        var mem = await db.AgentMemories.FirstOrDefaultAsync(m => m.Id == id && m.UserId == userId, ct);
        if (mem is null) return false;
        db.AgentMemories.Remove(mem);
        await db.SaveChangesAsync(ct);
        sharpVector.InvalidateMemoryIndex(userId);
        return true;
    }

    /// <summary>
    /// Bulk-deletes memories for a user.
    /// When <paramref name="since"/> is provided, only memories created on or after
    /// that date are removed; otherwise every memory for the user is deleted.
    /// </summary>
    public async Task<int> DeleteBulkAsync(
        string userId, DateTime? since = null, CancellationToken ct = default)
    {
        var query = db.AgentMemories.Where(m => m.UserId == userId);
        if (since.HasValue)
            query = query.Where(m => m.CreatedAt >= since.Value);

        var items = await query.ToListAsync(ct);
        if (items.Count == 0) return 0;

        db.AgentMemories.RemoveRange(items);
        await db.SaveChangesAsync(ct);
        sharpVector.InvalidateMemoryIndex(userId);
        return items.Count;
    }

    // â”€â”€ Auto-extraction â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public async Task ExtractFromExchangeAsync(
        string userId, string userMessage, string assistantResponse,
        CancellationToken ct = default)
    {
        var settings = llmSettings.Get();
        if (settings.ApiKeys.Count == 0) return;

        var count = await db.AgentMemories.CountAsync(m => m.UserId == userId, ct);
        if (count >= MaxMemoriesPerUser) return;

        var prompt =
            $"""
            Analyse the conversation exchange below and extract facts, preferences, or key
            information worth remembering for future conversations with this user.

            User said: {userMessage}
            Assistant said: {assistantResponse[..Math.Min(800, assistantResponse.Length)]}

            Rules:
            - Return ONLY a raw JSON array of strings (no markdown, no explanation).
            - Each string must be one short, concrete fact (max 200 chars).
            - Focus on: user preferences, personal details, project names, recurring topics.
            - Skip generic facts, pleasantries, and assistant capabilities.
            - Return [] if nothing is worth remembering.

            Example output: ["User prefers bullet-point answers","User is building a Razor Pages app"]
            """;

        try
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/" +
                      $"{settings.ModelId}:generateContent?key={Uri.EscapeDataString(settings.ApiKeys[0])}";

            var body = new
            {
                contents         = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
                generationConfig = new { temperature = 0.1, maxOutputTokens = 512 }
            };

            var client   = httpClientFactory.CreateClient("MemoryExtract");
            var response = await client.PostAsJsonAsync(url, body, ct);
            if (!response.IsSuccessStatusCode) return;

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc  = JsonDocument.Parse(json);
            var text = doc.RootElement
                .GetProperty("candidates")[0].GetProperty("content")
                .GetProperty("parts")[0].GetProperty("text").GetString() ?? "";

            var s = text.IndexOf('['); var e = text.LastIndexOf(']');
            if (s < 0 || e <= s) return;

            var facts    = JsonSerializer.Deserialize<string[]>(text[s..(e + 1)]) ?? [];
            var existing = await db.AgentMemories
                .Where(m => m.UserId == userId && m.IsActive)
                .Select(m => new { m.Content, m.EmbeddingJson })
                .ToListAsync(ct);

            int saved = 0;
            foreach (var fact in facts.Where(f => !string.IsNullOrWhiteSpace(f) && f.Length <= 2000))
            {
                var trimmed = fact.Trim();
                if (existing.Any(x => string.Equals(x.Content, trimmed, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var (vec, _) = await embeddings.EmbedAsync(trimmed, ct);

                if (vec is { Length: > 0 })
                {
                    var isDup = existing
                        .Where(x => x.EmbeddingJson is not null)
                        .Select(x => JsonSerializer.Deserialize<float[]>(x.EmbeddingJson!))
                        .Any(ev => ev is not null && CosineSimilarity(vec, ev) >= DuplicateThreshold);
                    if (isDup) continue;
                }

                db.AgentMemories.Add(new AgentMemory
                { UserId = userId, Content = trimmed, Source = "auto", Importance = 3, Embedding = vec });
                saved++;
            }

            if (saved > 0)
            {
                await db.SaveChangesAsync(ct);
                sharpVector.InvalidateMemoryIndex(userId); // fresh facts â†’ rebuild index on next query
                logger.LogInformation("Memory: extracted {N} new fact(s) for user {UserId}", saved, userId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Memory extraction failed for user {UserId}", userId);
        }
    }

    // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static List<AgentMemory> KeywordSearch(List<AgentMemory> memories, string query)
    {
        static string[] Tok(string t) =>
            t.ToLowerInvariant().Split([' ','\n','\t','.',',','?','!','-','\'','"'],
                StringSplitOptions.RemoveEmptyEntries);

        var qTerms = Tok(query).Where(w => w.Length > 2).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (qTerms.Count == 0) return [];

        return memories
            .Select(m => (m, Score: Tok(m.Content).Count(w => qTerms.Contains(w))))
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score)
            .Take(TopK).Select(x => x.m).ToList();
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0f;
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        var d = MathF.Sqrt(na) * MathF.Sqrt(nb);
        return d == 0 ? 0f : dot / d;
    }
}


