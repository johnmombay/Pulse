using Pulse.Data;
using Pulse.Data.Entities;
using Pulse.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;
using System.Text.Json;

namespace Pulse.Services;

/// <summary>
/// Core self-learning service that implements the Reflection Loop and Memory Loop
/// across every execution domain.
///
/// Reflection Loop:
///   EvaluateAsync  → scores the output against the goal (0.0–1.0)
///   ExtractAsync   → distils one actionable lesson and persists it
///
/// Memory Loop:
///   GetRelevantLessonsAsync → retrieves top-K past lessons by cosine similarity
///                             (SharpVector → Gemini embeddings → keyword fallback)
///   FormatLessonBlockAsync  → formats them as a system-message injection block
///
/// Closed-loop coordination is done by the caller (e.g., SpecializedAgentRunner,
/// WorkflowRunner) which calls GetRelevantLessonsAsync before acting and
/// ExtractAsync (via AgentReflectionJob) after completing.
/// </summary>
public sealed class AgentReflectionService(
    ApplicationDbContext db,
    GeminiEmbeddingService embeddings,
    LlmSettingsService llmSettings,
    SharpVectorIndexService sharpVector,
    ITenantContext tenantContext,
    IHttpClientFactory httpClientFactory,
    ILogger<AgentReflectionService> logger)
{
    private const int   TopK              = 5;
    private const float SimilarityThreshold = 0.55f;
    private const float DuplicateThreshold  = 0.88f;
    private const int   MaxLessonsPerTenant = 500;

    // ── Reflection Loop ───────────────────────────────────────────────────────

    /// <summary>
    /// Scores the agent's output against the original goal using the LLM as a judge.
    /// Returns a score between 0.0 (failure) and 1.0 (perfect), plus a critique string.
    /// Falls back to heuristics when the LLM is unavailable.
    /// </summary>
    public async Task<(double Score, string Critique)> EvaluateAsync(
        string goal,
        string output,
        bool executionSucceeded,
        CancellationToken ct = default)
    {
        // Fast heuristic: hard failures get 0.0
        if (!executionSucceeded || string.IsNullOrWhiteSpace(output))
            return (0.0, "Execution failed or produced no output.");

        var settings = await llmSettings.GetAsync(tenantContext.TenantId ?? Guid.Empty);
        if (settings.ApiKeys.Count == 0)
        {
            var heuristic = output.Length > 100 ? 0.6 : 0.3;
            return (heuristic, "Heuristic score (no LLM available for grading).");
        }

        var prompt =
            $$"""
            You are an objective evaluator. Grade how well the agent output satisfies the goal.

            Goal: {{goal[..Math.Min(600, goal.Length)]}}

            Agent Output (first 800 chars): {{output[..Math.Min(800, output.Length)]}}

            Respond ONLY with a JSON object: {"score": 0.85, "critique": "one sentence"}
            - score: float 0.0 (complete failure) to 1.0 (perfect)
            - critique: one concise sentence describing the main strength or weakness
            """;

        try
        {
            var apiKey = settings.ApiKeys.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k));
            if (apiKey is null) return (0.5, "No API key available.");

            var client = httpClientFactory.CreateClient("MemoryExtract");
            client.DefaultRequestHeaders.Remove("Authorization");
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            var body = new
            {
                model    = settings.ModelId,
                messages = new[] { new { role = "user", content = prompt } },
                max_tokens  = 128,
                temperature = 0.0
            };

            var response = await client.PostAsJsonAsync(
                OpenRouterService.BaseUrl + "/chat/completions", body, ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("ReflectionService.EvaluateAsync: OpenRouter returned {Status}",
                    (int)response.StatusCode);
                return (0.5, "Evaluation service unavailable.");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc  = JsonDocument.Parse(json);
            var text = doc.RootElement
                .GetProperty("choices")[0].GetProperty("message")
                .GetProperty("content").GetString() ?? "";

            var s = text.IndexOf('{'); var e = text.LastIndexOf('}');
            if (s < 0 || e <= s) return (0.5, text.Trim());

            var result = JsonDocument.Parse(text[s..(e + 1)]);
            var score  = result.RootElement.TryGetProperty("score",    out var sv) ? sv.GetDouble() : 0.5;
            var crit   = result.RootElement.TryGetProperty("critique", out var cv) ? cv.GetString() ?? "" : "";
            return (Math.Clamp(score, 0.0, 1.0), crit);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ReflectionService: evaluation LLM call failed");
            return (0.5, "Evaluation unavailable.");
        }
    }

    /// <summary>
    /// Extracts a lesson from the completed exchange and persists it.
    /// Deduplicates by semantic similarity so the store stays clean.
    /// </summary>
    public async Task ExtractAsync(
        string userId,
        string domain,
        string goal,
        string actionTaken,
        string outcome,
        double score,
        string critique,
        string? agentDefinitionId = null,
        CancellationToken ct = default)
    {
        var tenantId = tenantContext.TenantId ?? Guid.Empty;

        var count = await db.AgentLessons.CountAsync(
            l => l.TenantId == tenantId, ct);
        if (count >= MaxLessonsPerTenant) return;

        var settings = await llmSettings.GetAsync(tenantId);
        if (settings.ApiKeys.Count == 0) return;

        var lessonText = await SynthesiseLessonAsync(goal, actionTaken, outcome, score, critique, settings, ct);
        if (string.IsNullOrWhiteSpace(lessonText)) return;

        var (goalVec, _) = await embeddings.EmbedAsync(goal, ct);

        // Dedup: skip if a very similar lesson already exists in this domain/agent scope
        var existing = await db.AgentLessons
            .Where(l => l.TenantId == tenantId &&
                        l.Domain == domain &&
                        l.AgentDefinitionId == agentDefinitionId)
            .Select(l => new { l.Id, l.LessonText, l.EmbeddingJson, l.Score })
            .ToListAsync(ct);

        if (goalVec is { Length: > 0 })
        {
            foreach (var ex in existing)
            {
                if (ex.EmbeddingJson is null) continue;
                var ev = JsonSerializer.Deserialize<float[]>(ex.EmbeddingJson);
                if (ev is not null && CosineSimilarity(goalVec, ev) >= DuplicateThreshold)
                {
                    // Reinforce the existing lesson instead of creating a duplicate
                    var existing2 = await db.AgentLessons.FindAsync([ex.Id], ct);
                    if (existing2 is not null)
                    {
                        existing2.ReinforcementCount++;
                        existing2.Score     = Math.Max(existing2.Score, score);
                        existing2.UpdatedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(ct);
                        logger.LogDebug(
                            "ReflectionService: reinforced lesson {Id} (rc={Rc})",
                            existing2.Id, existing2.ReinforcementCount);
                    }
                    return;
                }
            }
        }

        db.AgentLessons.Add(new AgentLessonEntity
        {
            TenantId          = tenantId,
            UserId            = userId,
            Domain            = domain,
            AgentDefinitionId = agentDefinitionId,
            GoalText          = goal[..Math.Min(1000, goal.Length)],
            ActionTaken       = actionTaken[..Math.Min(2000, actionTaken.Length)],
            Outcome           = outcome[..Math.Min(4000, outcome.Length)],
            LessonText        = lessonText,
            Score             = score,
            Embedding         = goalVec,
        });

        await db.SaveChangesAsync(ct);
        sharpVector.InvalidateLessonIndex(tenantId);
        logger.LogInformation(
            "ReflectionService: saved lesson for domain={Domain} score={Score:F2} tenant={TenantId}",
            domain, score, tenantId);
    }

    // ── Memory Loop ───────────────────────────────────────────────────────────

    /// <summary>
    /// Retrieves the top-K most relevant lessons for <paramref name="goal"/> within the given
    /// domain and agent scope. Three-tier: SharpVector → embeddings → keyword.
    /// </summary>
    public async Task<List<AgentLessonEntity>> GetRelevantLessonsAsync(
        string goal,
        string domain,
        string? agentDefinitionId = null,
        CancellationToken ct = default)
    {
        var tenantId = tenantContext.TenantId ?? Guid.Empty;

        var all = await db.AgentLessons
            .Where(l => l.TenantId == tenantId && l.Domain == domain &&
                        l.AgentDefinitionId == agentDefinitionId)
            .OrderByDescending(l => l.ReinforcementCount)
            .ThenByDescending(l => l.Score)
            .ToListAsync(ct);

        if (all.Count == 0) return [];

        // 1. SharpVector (in-process Bag-of-Words, no API)
        var indexKey = $"lessons:{tenantId:N}:{domain}:{agentDefinitionId ?? "global"}";
        if (!sharpVector.IsLessonIndexed(indexKey))
            await sharpVector.IndexLessonsAsync(indexKey,
                all.Select(l => (l.Id, l.GoalText)), ct);

        var svIds = sharpVector.SearchLessons(indexKey, goal, TopK);
        if (svIds.Count > 0)
        {
            var idSet = svIds.ToHashSet();
            return all.Where(l => idSet.Contains(l.Id)).ToList();
        }

        // 2. Gemini embedding cosine search
        var (queryVec, _) = await embeddings.EmbedAsync(goal, ct);
        if (queryVec is { Length: > 0 })
        {
            var hits = all
                .Where(l => l.Embedding is { Length: > 0 })
                .Select(l => (l, Score: CosineSimilarity(queryVec, l.Embedding!)))
                .Where(x => x.Score >= SimilarityThreshold)
                .OrderByDescending(x => x.Score)
                .Take(TopK)
                .Select(x => x.l)
                .ToList();
            if (hits.Count > 0) return hits;
        }

        // 3. Keyword fallback
        return KeywordSearch(all, goal);
    }

    /// <summary>
    /// Formats retrieved lessons as a concise system message block ready for SK injection.
    /// Returns empty string when no lessons are available.
    /// </summary>
    public static string FormatLessonBlock(IReadOnlyList<AgentLessonEntity> lessons)
    {
        if (lessons.Count == 0) return "";

        var lines = lessons.Select((l, i) =>
            $"{i + 1}. [score={l.Score:F1} reinforced×{l.ReinforcementCount}] {l.LessonText}");

        return "Past lessons learned — apply these to improve your response:\n" +
               string.Join("\n", lines);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<string> SynthesiseLessonAsync(
        string goal, string action, string outcome,
        double score, string critique,
        Models.LlmSettingsModel settings,
        CancellationToken ct)
    {
        var prompt =
            $"""
            Distil ONE concise, actionable lesson (max 300 chars) from this agent experience.
            The lesson should help future executions avoid the same mistake or reinforce what worked.

            Goal: {goal[..Math.Min(400, goal.Length)]}
            Action: {action[..Math.Min(400, action.Length)]}
            Outcome snippet: {outcome[..Math.Min(400, outcome.Length)]}
            Score: {score:F2}/1.00
            Critique: {critique}

            Rules:
            - Return ONLY the lesson text, no JSON, no preamble.
            - Start with an imperative verb (e.g. "Always", "Avoid", "Prefer", "When X, do Y").
            - Return empty string if nothing useful can be learned.
            """;

        try
        {
            var apiKey = settings.ApiKeys.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k));
            if (apiKey is null) return "";

            var client = httpClientFactory.CreateClient("MemoryExtract");
            client.DefaultRequestHeaders.Remove("Authorization");
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            var body = new
            {
                model    = settings.ModelId,
                messages = new[] { new { role = "user", content = prompt } },
                max_tokens  = 128,
                temperature = 0.2
            };

            var response = await client.PostAsJsonAsync(
                OpenRouterService.BaseUrl + "/chat/completions", body, ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("ReflectionService.SynthesiseLessonAsync: OpenRouter returned {Status}",
                    (int)response.StatusCode);
                return "";
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc  = JsonDocument.Parse(json);
            return doc.RootElement
                .GetProperty("choices")[0].GetProperty("message")
                .GetProperty("content").GetString()?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ReflectionService: lesson synthesis failed");
            return "";
        }
    }

    private static List<AgentLessonEntity> KeywordSearch(List<AgentLessonEntity> lessons, string query)
    {
        static string[] Tok(string t) =>
            t.ToLowerInvariant().Split([' ', '\n', '\t', '.', ',', '?', '!', '-', '\'', '"'],
                StringSplitOptions.RemoveEmptyEntries);

        var qTerms = Tok(query).Where(w => w.Length > 2).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (qTerms.Count == 0) return [];

        return lessons
            .Select(l => (l, Score: Tok(l.GoalText).Count(w => qTerms.Contains(w))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(TopK)
            .Select(x => x.l)
            .ToList();
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
