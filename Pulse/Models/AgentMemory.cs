using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Pulse.Data.Entities;

namespace Pulse.Models;

/// <summary>
/// A single persisted memory fact for a user, stored in SQL Server.
/// Embeddings are serialised as JSON alongside the text so the service
/// can do in-memory cosine search without a dedicated vector store.
/// </summary>
public class AgentMemory : ITenantOwned
{
    public int Id { get; set; }

    public Guid TenantId { get; set; }

    [Required, MaxLength(450)]
    public string UserId { get; set; } = "";

    [Required, MaxLength(2000)]
    public string Content { get; set; } = "";

    [MaxLength(60)]
    public string? Category { get; set; }

    /// <summary>1 (low) … 5 (critical). Auto-extracted memories default to 3.</summary>
    public int Importance { get; set; } = 3;

    /// <summary>"auto" = extracted by the agent; "manual" = added by the user.</summary>
    [MaxLength(10)]
    public string Source { get; set; } = "auto";

    /// <summary>JSON-serialised float[] for semantic retrieval. Null until embedded.</summary>
    public string? EmbeddingJson { get; set; }

    /// <summary>Deserialised view of <see cref="EmbeddingJson"/>. Not mapped to a column.</summary>
    [NotMapped]
    public float[]? Embedding
    {
        get => EmbeddingJson is null ? null
            : JsonSerializer.Deserialize<float[]>(EmbeddingJson);
        set => EmbeddingJson = value is null ? null
            : JsonSerializer.Serialize(value);
    }

    public DateTime CreatedAt  { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt  { get; set; } = DateTime.UtcNow;
    public bool     IsActive   { get; set; } = true;
}
