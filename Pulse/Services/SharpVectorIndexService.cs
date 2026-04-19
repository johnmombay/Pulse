using Build5Nines.SharpVector;
using System.Collections.Concurrent;

namespace Pulse.Services;

/// <summary>
/// Singleton that maintains per-document (RAG) and per-user (Memory)
/// in-memory SharpVector indexes for fast, API-free text similarity search.
///
/// Build5Nines.SharpVector uses Bag-of-Words + cosine similarity internally;
/// no external embedding API calls are needed for search.
///
/// Search API: Search(queryText, threshold?, pageIndex, pageCount?, filter?)
///   - threshold = null  â†’ return all ranked results
///   - pageIndex  = 0    â†’ first page
///   - pageCount  = topK â†’ how many results to return
/// </summary>
public sealed class SharpVectorIndexService(ILogger<SharpVectorIndexService> logger)
{
    private readonly ConcurrentDictionary<string, BasicMemoryVectorDatabase> _ragDbs  = new();
    private readonly ConcurrentDictionary<string, BasicMemoryVectorDatabase> _memDbs  = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim>             _locks   = new();

    private SemaphoreSlim Lock(string key) =>
        _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // RAG
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    public bool IsRagIndexed(string docId) => _ragDbs.ContainsKey(docId);

    /// <summary>Indexes all chunks for one RAG document. Thread-safe rebuild.</summary>
    public async Task IndexRagDocumentAsync(
        string docId, IEnumerable<string> chunks, CancellationToken ct = default)
    {
        var sem = Lock($"rag:{docId}");
        await sem.WaitAsync(ct);
        try
        {
            var db    = new BasicMemoryVectorDatabase();
            int count = 0;
            foreach (var chunk in chunks.Where(c => !string.IsNullOrWhiteSpace(c)))
            {
                db.AddText(chunk, chunk); // metadata = the passage text (returned on search)
                count++;
            }
            _ragDbs[docId] = db;
            logger.LogDebug("SharpVector RAG: indexed {N} chunk(s) for doc {Id}", count, docId);
        }
        finally { sem.Release(); }
    }

    /// <summary>
    /// Searches all enabled RAG document indexes.
    /// Returns top-K passages de-duplicated and capped at <paramref name="maxChars"/>.
    /// </summary>
    public List<string> SearchRag(
        string query, IEnumerable<string> enabledDocIds,
        int topK = 5, int maxChars = 4000)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var seen      = new HashSet<string>(StringComparer.Ordinal);
        var results   = new List<string>();
        int totalChars = 0;

        foreach (var docId in enabledDocIds)
        {
            if (!_ragDbs.TryGetValue(docId, out var db)) continue;

            // threshold=null (accept all), pageIndex=0, pageCount=topK, filter=null
            var result = db.Search(query, null, 0, topK, null);
            if (result.IsEmpty) continue;

            foreach (var item in result.Texts.OrderByDescending(i => i.Similarity))
            {
                var passage = item.Metadata ?? item.Text;
                if (string.IsNullOrWhiteSpace(passage) || !seen.Add(passage)) continue;
                if (totalChars + passage.Length > maxChars) continue;
                results.Add(passage);
                totalChars += passage.Length;
                if (results.Count >= topK) goto done;
            }
        }
        done:
        return results;
    }

    public void InvalidateRagIndex(string docId)
    {
        _ragDbs.TryRemove(docId, out _);
        logger.LogDebug("SharpVector RAG: invalidated index for doc {Id}", docId);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // Memory
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    public bool IsMemoryIndexed(string userId) => _memDbs.ContainsKey(userId);

    /// <summary>Builds or rebuilds the user's memory index. Thread-safe.</summary>
    public async Task IndexMemoriesAsync(
        string userId,
        IEnumerable<(int Id, string Content)> memories,
        CancellationToken ct = default)
    {
        var sem = Lock($"mem:{userId}");
        await sem.WaitAsync(ct);
        try
        {
            var db    = new BasicMemoryVectorDatabase();
            int count = 0;
            foreach (var (id, content) in memories.Where(m => !string.IsNullOrWhiteSpace(m.Content)))
            {
                db.AddText(content, id.ToString()); // metadata = memory id
                count++;
            }
            _memDbs[userId] = db;
            logger.LogDebug("SharpVector Memory: indexed {N} item(s) for user {UserId}", count, userId);
        }
        finally { sem.Release(); }
    }

    /// <summary>Returns the IDs of the top-K most relevant memories.</summary>
    public List<int> SearchMemories(string userId, string query, int topK = 8)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        if (!_memDbs.TryGetValue(userId, out var db)) return [];

        var result = db.Search(query, null, 0, topK, null);
        if (result.IsEmpty) return [];

        return result.Texts
            .OrderByDescending(i => i.Similarity)
            .Select(item => item.Metadata)
            .Where(m => int.TryParse(m, out _))
            .Select(int.Parse!)
            .Distinct()
            .Take(topK)
            .ToList();
    }

    public void InvalidateMemoryIndex(string userId)
    {
        _memDbs.TryRemove(userId, out _);
        logger.LogDebug("SharpVector Memory: invalidated index for user {UserId}", userId);
    }
}


/// <summary>
/// Singleton that maintains per-document (RAG) and per-user (Memory)
/// in-memory SharpVector indexes for fast, API-free text similarity search.
///
/// Strategy:
///   Build5Nines.SharpVector uses Bag-of-Words + cosine similarity internally â€”
///   no external embedding API calls are needed for search.
