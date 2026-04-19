namespace Pulse.Data.Entities;

public class RagDocumentEntity
{
    public string   Id               { get; set; } = Guid.NewGuid().ToString("N");
    public string   Name             { get; set; } = "";
    public string   Description      { get; set; } = "";
    public bool     IsEnabled        { get; set; } = true;
    public string?  OriginalFileName { get; set; }
    public int      ChunkCount       { get; set; }
    public DateTime UpdatedAt        { get; set; } = DateTime.UtcNow;
}
