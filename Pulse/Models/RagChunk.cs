namespace Pulse.Models;

/// <summary>A single text chunk with its pre-computed embedding vector.</summary>
public class RagChunk
{
    public string Text { get; set; } = "";

    /// <summary>
    /// 768-dimensional vector produced by Gemini text-embedding-004.
    /// Empty when embedding failed or has not been computed yet.
    /// </summary>
    public float[] Embedding { get; set; } = [];
}
