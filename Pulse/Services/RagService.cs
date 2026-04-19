using Pulse.Models;
using System.Text;
using System.Text.Json;

namespace Pulse.Services;

/// <summary>
/// Handles the full RAG pipeline:
///   Ingest   â€“ chunk text, optionally generate Gemini embeddings, persist to disk,
///              and build an in-memory SharpVector index.
///   Retrieve â€“ search the SharpVector index first (Bag-of-Words, no API call),
///              fall back to Gemini embedding cosine-search, then keyword overlap.
///   Delete   â€“ remove the document directory and invalidate the SharpVector index.
/// </summary>
public sealed class RagService(
    IWebHostEnvironment env,
    GeminiEmbeddingService embeddings,
    LlmSettingsService settingsService,
    SharpVectorIndexService sharpVector,
    ILogger<RagService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private const int   ChunkMaxChars       = 600;
    private const int   ChunkOverlapChars   = 80;
    private const int   RetrieveTopK        = 5;
    private const float SimilarityThreshold = 0.45f;
    private const int   MaxContextChars     = 4000;

    private string RagRoot()             => Path.Combine(env.ContentRootPath, "rag");
    private string DocFolder(string id)  => Path.Combine(RagRoot(), id);
    private string ChunksPath(string id) => Path.Combine(DocFolder(id), "chunks.json");
    private string ContentPath(string id)=> Path.Combine(DocFolder(id), "content.txt");

    // â”€â”€ Ingest â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public async Task<(int ChunkCount, int EmbeddedCount, string? EmbeddingError)> IngestAsync(
        string content, string docId, CancellationToken ct = default)
    {
        Directory.CreateDirectory(DocFolder(docId));
        await File.WriteAllTextAsync(ContentPath(docId), content, Encoding.UTF8, ct);

        var textChunks = ChunkText(content);
        logger.LogInformation("RAG ingest: doc {Id} â†’ {N} text chunks", docId, textChunks.Count);

        var ragChunks     = new List<RagChunk>(textChunks.Count);
        int embeddedCount = 0;
        string? firstError = null;

        foreach (var chunk in textChunks)
        {
            ct.ThrowIfCancellationRequested();
            var (vec, error) = await embeddings.EmbedAsync(chunk, ct);
            if (vec is { Length: > 0 }) { embeddedCount++; ragChunks.Add(new RagChunk { Text = chunk, Embedding = vec }); }
            else                        { firstError ??= error; ragChunks.Add(new RagChunk { Text = chunk, Embedding = [] }); }
        }

        await File.WriteAllTextAsync(ChunksPath(docId),
            JsonSerializer.Serialize(ragChunks, JsonOpts), Encoding.UTF8, ct);

        // â”€â”€ Build / rebuild the SharpVector in-memory index â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        await sharpVector.IndexRagDocumentAsync(docId, textChunks, ct);

        logger.LogInformation(
            "RAG ingest: doc {Id} â€” {Total} chunks, {Emb} embedded, SharpVector index built{Err}",
            docId, ragChunks.Count, embeddedCount,
            firstError is null ? "" : $" | embedding error: {firstError}");

        return (ragChunks.Count, embeddedCount, firstError);
    }

    public async Task<(int ChunkCount, int EmbeddedCount, string? EmbeddingError)> ReembedAsync(
        string docId, CancellationToken ct = default)
    {
        var path = ContentPath(docId);
        if (!File.Exists(path))
            return (0, 0, $"content.txt not found for document {docId} â€” please re-save the document.");
        var content = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
        return await IngestAsync(content, docId, ct);
    }

    // â”€â”€ Retrieve â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Three-tier retrieval:
    ///   1. <b>SharpVector</b> (in-memory Bag-of-Words, no API) â€” always attempted first.
    ///   2. <b>Gemini embedding cosine search</b> â€” when Gemini API is available and
    ///      chunks have stored embeddings.
    ///   3. <b>Keyword overlap fallback</b> â€” last resort, always available.
    /// </summary>
    public async Task<List<string>> RetrieveAsync(
        string query, CancellationToken ct = default)
    {
        var enabledDocs = (settingsService.Get().RagDocuments ?? [])
            .Where(d => d.IsEnabled && d.ChunkCount > 0)
            .ToList();

        if (enabledDocs.Count == 0) return [];

        // â”€â”€ Ensure every enabled doc has a SharpVector index â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        foreach (var doc in enabledDocs.Where(d => !sharpVector.IsRagIndexed(d.Id)))
        {
            var chunks = LoadChunks(doc.Id);
            if (chunks.Count == 0)
            {
                // Fall back to reading content.txt directly
                var cPath = ContentPath(doc.Id);
                if (File.Exists(cPath))
                {
                    var text = await File.ReadAllTextAsync(cPath, ct);
                    await sharpVector.IndexRagDocumentAsync(doc.Id, ChunkText(text), ct);
                }
            }
            else
            {
                await sharpVector.IndexRagDocumentAsync(doc.Id, chunks.Select(c => c.Text), ct);
            }
        }

        // â”€â”€ 1. SharpVector (Bag-of-Words, in-memory) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var svHits = sharpVector.SearchRag(query, enabledDocs.Select(d => d.Id), RetrieveTopK, MaxContextChars);
        if (svHits.Count > 0)
        {
            logger.LogInformation("RAG: SharpVector â†’ {N} chunk(s)", svHits.Count);
            return svHits;
        }

        logger.LogDebug("RAG: SharpVector returned 0 hits â€” trying Gemini embedding search");

        // â”€â”€ 2. Gemini embedding cosine search â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var allChunks = new List<RagChunk>();
        foreach (var doc in enabledDocs) allChunks.AddRange(LoadChunks(doc.Id));

        if (allChunks.Count > 0)
        {
            var (queryVec, embedError) = await embeddings.EmbedAsync(query, ct);
            if (queryVec is { Length: > 0 })
            {
                var embChunks = allChunks.Where(c => c.Embedding.Length > 0).ToList();
                if (embChunks.Count > 0)
                {
                    var semHits = embChunks
                        .Select(c => (c.Text, Score: CosineSimilarity(queryVec, c.Embedding)))
                        .Where(x => x.Score >= SimilarityThreshold)
                        .OrderByDescending(x => x.Score)
                        .Take(RetrieveTopK)
                        .Select(x => x.Text)
                        .ToList();

                    if (semHits.Count > 0)
                    {
                        logger.LogInformation("RAG: Gemini embedding â†’ {N} chunk(s)", semHits.Count);
                        return CapContext(semHits);
                    }
                }
            }
            else
            {
                logger.LogWarning("RAG: Gemini embedding failed ({Err})", embedError);
            }

            // â”€â”€ 3. Keyword fallback â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            var kwHits = KeywordRetrieve(allChunks, query);
            if (kwHits.Count > 0)
                logger.LogInformation("RAG: keyword fallback â†’ {N} chunk(s)", kwHits.Count);
            return CapContext(kwHits);
        }

        logger.LogWarning("RAG: no chunks available on disk for {N} doc(s)", enabledDocs.Count);
        return [];
    }

    // â”€â”€ Delete â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public void DeleteDocument(string docId)
    {
        sharpVector.InvalidateRagIndex(docId);
        var folder = DocFolder(docId);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private List<RagChunk> LoadChunks(string docId)
    {
        var path = ChunksPath(docId);
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<List<RagChunk>>(File.ReadAllText(path)) ?? []; }
        catch (Exception ex) { logger.LogWarning(ex, "Could not load chunks for doc {Id}", docId); return []; }
    }

    private static List<string> ChunkText(string text)
    {
        var paras = text.Replace("\r\n", "\n")
            .Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim()).Where(p => p.Length > 15).ToList();

        var chunks  = new List<string>();
        var current = new StringBuilder();

        foreach (var para in paras)
        {
            if (current.Length + para.Length + 2 > ChunkMaxChars && current.Length > 0)
            {
                chunks.Add(current.ToString().Trim());
                var overlap = current.Length > ChunkOverlapChars
                    ? current.ToString(current.Length - ChunkOverlapChars, ChunkOverlapChars)
                    : current.ToString();
                current.Clear();
                current.Append(overlap.TrimStart()).Append(' ');
            }
            current.Append(para).Append("\n\n");
        }
        if (current.Length > 15) chunks.Add(current.ToString().Trim());
        return chunks;
    }

    private List<string> KeywordRetrieve(List<RagChunk> chunks, string query)
    {
        static string[] Tokenise(string t) =>
            t.ToLowerInvariant().Split(
                [' ','\n','\r','\t','.',',','?','!','-','\'','"','(',')','[',']','/','\\',':'],
                StringSplitOptions.RemoveEmptyEntries);

        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "the","a","an","and","or","but","in","on","at","to","for","of","with","is","was",
          "are","were","be","been","have","has","had","do","does","did","will","would",
          "could","should","may","might","can","its","it","this","that","we","you","i" };

        var qTerms = Tokenise(query).Where(w => w.Length > 2 && !stop.Contains(w))
                     .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (qTerms.Count == 0) return [];

        return chunks
            .Select(c => (c.Text, Score: Tokenise(c.Text).Count(w => qTerms.Contains(w))))
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score)
            .Take(RetrieveTopK).Select(x => x.Text).ToList();
    }

    private static List<string> CapContext(List<string> chunks)
    {
        var result = new List<string>(); int total = 0;
        foreach (var c in chunks) { if (total + c.Length > MaxContextChars) break; result.Add(c); total += c.Length; }
        return result;
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

