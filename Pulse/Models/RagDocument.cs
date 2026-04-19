namespace Pulse.Models;

/// <summary>
/// Metadata for a RAG knowledge-base document.
/// The actual text content and embeddings live in {ContentRoot}/rag/{Id}/.
/// </summary>
public class RagDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsEnabled { get; set; } = true;

    /// <summary>Original filename when the source was a file upload.</summary>
    public string? OriginalFileName { get; set; }

    /// <summary>Number of embedded chunks currently stored on disk.</summary>
    public int ChunkCount { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
