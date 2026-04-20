using Pulse.Models;

namespace Pulse.Data.Entities;

/// <summary>
/// Per-tenant settings row holding scalar + owned-type settings that used to live in llm-settings.json.
/// Collections (Skills, McpServers, RagDocuments, FlatFileSources, DatabaseConnections, ApiKeys)
/// are stored in their own tables.
/// </summary>
public class AppSettingsEntity : ITenantOwned
{
    public int Id { get; set; }

    public Guid TenantId { get; set; }

    public string AppName { get; set; } = "Pulse";
    public string ModelId { get; set; } = "gemini-2.0-flash";

    public string? LogoFileName { get; set; }
    public string? LogoVersion  { get; set; }

    public TerminalSettings Terminal  { get; set; } = new();
    public SecuritySettings Security  { get; set; } = new();

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
