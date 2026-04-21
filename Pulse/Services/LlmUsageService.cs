using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using Pulse.Data;
using Pulse.Data.Entities;

namespace Pulse.Services;

/// <summary>
/// Persists per-call LLM token usage and serves the dashboards.
/// All queries bypass tenant filters (<see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TEntity}"/>)
/// and are scoped manually so SuperAdmin can fan out across tenants while tenant-scoped
/// callers always pass their own <c>tenantId</c>.
/// </summary>
public sealed class LlmUsageService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    /// <summary>
    /// Records a single LLM call. Failures are swallowed and logged by the caller —
    /// usage tracking must never break the agent flow.
    /// </summary>
    public async Task RecordAsync(
        Guid tenantId,
        string userId,
        string agentName,
        string modelId,
        int promptTokens,
        int completionTokens,
        int totalTokens,
        CancellationToken ct = default)
    {
        if (promptTokens == 0 && completionTokens == 0 && totalTokens == 0) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.LlmUsage.Add(new LlmUsageEntity
        {
            TenantId         = tenantId,
            UserId           = userId ?? string.Empty,
            AgentName        = agentName ?? string.Empty,
            ModelId          = modelId ?? string.Empty,
            PromptTokens     = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens      = totalTokens > 0 ? totalTokens : promptTokens + completionTokens,
            CreatedUtc       = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    // ── Aggregations ────────────────────────────────────────────────────────────

    public record MonthlyTotal(int Year, int Month, long PromptTokens, long CompletionTokens, long TotalTokens, int Calls);
    public record GroupTotal(string Key, long PromptTokens, long CompletionTokens, long TotalTokens, int Calls);
    public record TenantTotal(Guid TenantId, string TenantName, long PromptTokens, long CompletionTokens, long TotalTokens, int Calls);

    /// <summary>
    /// Monthly totals for the given tenant over the last <paramref name="months"/> months
    /// (inclusive of the current month). Pass <see cref="Guid.Empty"/> for cross-tenant totals
    /// (SuperAdmin only — caller is expected to authorize).
    /// </summary>
    public async Task<List<MonthlyTotal>> GetMonthlyAsync(Guid tenantId, int months = 12, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var since = StartOfMonthUtc(DateTime.UtcNow.AddMonths(-(months - 1)));
        var query = db.LlmUsage.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.CreatedUtc >= since);
        if (tenantId != Guid.Empty)
            query = query.Where(u => u.TenantId == tenantId);

        var rows = await query
            .GroupBy(u => new { u.CreatedUtc.Year, u.CreatedUtc.Month })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                Prompt = g.Sum(x => (long)x.PromptTokens),
                Comp   = g.Sum(x => (long)x.CompletionTokens),
                Total  = g.Sum(x => (long)x.TotalTokens),
                Calls  = g.Count(),
            })
            .ToListAsync(ct);

        var mapped = rows
            .Select(r => new MonthlyTotal(r.Year, r.Month, r.Prompt, r.Comp, r.Total, r.Calls))
            .ToList();

        return FillMissingMonths(mapped, months);
    }

    /// <summary>Totals for a tenant for the current calendar month (UTC).</summary>
    public async Task<MonthlyTotal> GetCurrentMonthAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var start = StartOfMonthUtc(DateTime.UtcNow);
        var query = db.LlmUsage.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.CreatedUtc >= start);
        if (tenantId != Guid.Empty)
            query = query.Where(u => u.TenantId == tenantId);

        var totals = await query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Prompt = g.Sum(x => (long)x.PromptTokens),
                Comp   = g.Sum(x => (long)x.CompletionTokens),
                Total  = g.Sum(x => (long)x.TotalTokens),
                Calls  = g.Count(),
            })
            .FirstOrDefaultAsync(ct);

        return totals is null
            ? new MonthlyTotal(start.Year, start.Month, 0, 0, 0, 0)
            : new MonthlyTotal(start.Year, start.Month, totals.Prompt, totals.Comp, totals.Total, totals.Calls);
    }

    /// <summary>
    /// Per-tenant totals over the last <paramref name="months"/> months, joined with the
    /// tenant name. SuperAdmin view only.
    /// </summary>
    public async Task<List<TenantTotal>> GetByTenantAsync(int months = 1, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var since = StartOfMonthUtc(DateTime.UtcNow.AddMonths(-(months - 1)));

        var totals = await db.LlmUsage.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.CreatedUtc >= since)
            .GroupBy(u => u.TenantId)
            .Select(g => new
            {
                TenantId = g.Key,
                Prompt   = g.Sum(x => (long)x.PromptTokens),
                Comp     = g.Sum(x => (long)x.CompletionTokens),
                Total    = g.Sum(x => (long)x.TotalTokens),
                Calls    = g.Count(),
            })
            .ToListAsync(ct);

        var tenantIds = totals.Select(t => t.TenantId).ToList();
        var tenantNames = await db.Tenants.IgnoreQueryFilters().AsNoTracking()
            .Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        return totals
            .Select(t => new TenantTotal(
                t.TenantId,
                t.TenantId == Guid.Empty
                    ? "(SuperAdmin / no tenant)"
                    : tenantNames.GetValueOrDefault(t.TenantId, "(deleted)"),
                t.Prompt, t.Comp, t.Total, t.Calls))
            .OrderByDescending(t => t.TotalTokens)
            .ToList();
    }

    /// <summary>
    /// Group-by helper used for the by-model / by-agent / by-user breakdowns.
    /// Pass <see cref="Guid.Empty"/> for SuperAdmin cross-tenant aggregation.
    /// </summary>
    public async Task<List<GroupTotal>> GetGroupedAsync(
        Guid tenantId, string groupBy, int months = 1, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var since = StartOfMonthUtc(DateTime.UtcNow.AddMonths(-(months - 1)));
        IQueryable<LlmUsageEntity> q = db.LlmUsage.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.CreatedUtc >= since);
        if (tenantId != Guid.Empty)
            q = q.Where(u => u.TenantId == tenantId);

        IQueryable<IGrouping<string, LlmUsageEntity>> grouped = groupBy switch
        {
            "model" => q.GroupBy(u => u.ModelId),
            "agent" => q.GroupBy(u => u.AgentName),
            "user"  => q.GroupBy(u => u.UserId),
            _       => q.GroupBy(u => u.AgentName),
        };

        var rows = await grouped
            .Select(g => new
            {
                Key    = g.Key,
                Prompt = g.Sum(x => (long)x.PromptTokens),
                Comp   = g.Sum(x => (long)x.CompletionTokens),
                Total  = g.Sum(x => (long)x.TotalTokens),
                Calls  = g.Count(),
            })
            .ToListAsync(ct);

        return rows
            .Select(r => new GroupTotal(r.Key ?? string.Empty, r.Prompt, r.Comp, r.Total, r.Calls))
            .OrderByDescending(g => g.TotalTokens)
            .ToList();
    }

    // ── Token extraction from a Semantic Kernel streaming chunk ────────────────

    /// <summary>
    /// Best-effort extraction of Gemini token counts from the <see cref="StreamingKernelContent.Metadata"/>
    /// dictionary. The Google connector exposes <c>PromptTokenCount</c>, <c>CandidatesTokenCount</c>,
    /// and <c>TotalTokenCount</c> in the final chunk, but key casing varies across SK versions —
    /// we probe several spellings and keep the maximum value seen across the stream.
    /// </summary>
    public static (int Prompt, int Completion, int Total) ExtractTokens(
        IReadOnlyDictionary<string, object?>? metadata,
        (int Prompt, int Completion, int Total) running = default)
    {
        if (metadata is null || metadata.Count == 0) return running;

        int prompt = Math.Max(running.Prompt,
            ReadInt(metadata, "PromptTokenCount", "promptTokenCount", "prompt_tokens", "PromptTokens"));
        int comp = Math.Max(running.Completion,
            ReadInt(metadata, "CandidatesTokenCount", "candidatesTokenCount", "completion_tokens", "CompletionTokens"));
        int total = Math.Max(running.Total,
            ReadInt(metadata, "TotalTokenCount", "totalTokenCount", "total_tokens", "TotalTokens"));

        // Some connector versions wrap the counts in a nested "Usage" / "UsageMetadata" object.
        foreach (var key in new[] { "Usage", "UsageMetadata", "usageMetadata" })
        {
            if (metadata.TryGetValue(key, out var nested) && nested is IReadOnlyDictionary<string, object?> dict)
            {
                var (p, c, t) = ExtractTokens(dict);
                if (p > prompt) prompt = p;
                if (c > comp)   comp   = c;
                if (t > total)  total  = t;
            }
        }

        return (prompt, comp, total);
    }

    private static int ReadInt(IReadOnlyDictionary<string, object?> map, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (!map.TryGetValue(k, out var v) || v is null) continue;
            try
            {
                return v switch
                {
                    int i      => i,
                    long l     => (int)l,
                    short s    => s,
                    double d   => (int)d,
                    float f    => (int)f,
                    string str => int.TryParse(str, out var parsed) ? parsed : 0,
                    _          => Convert.ToInt32(v),
                };
            }
            catch { /* try next key */ }
        }
        return 0;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static DateTime StartOfMonthUtc(DateTime t) =>
        new DateTime(t.Year, t.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<MonthlyTotal> FillMissingMonths(List<MonthlyTotal> rows, int months)
    {
        var byKey = rows.ToDictionary(r => (r.Year, r.Month));
        var result = new List<MonthlyTotal>(months);
        var cursor = StartOfMonthUtc(DateTime.UtcNow.AddMonths(-(months - 1)));
        for (int i = 0; i < months; i++)
        {
            var key = (cursor.Year, cursor.Month);
            result.Add(byKey.TryGetValue(key, out var row)
                ? row
                : new MonthlyTotal(cursor.Year, cursor.Month, 0, 0, 0, 0));
            cursor = cursor.AddMonths(1);
        }
        return result;
    }
}
