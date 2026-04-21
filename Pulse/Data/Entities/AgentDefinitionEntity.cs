namespace Pulse.Data.Entities;

public class AgentDefinitionEntity : ITenantOwned
{
    public Guid   TenantId       { get; set; }
    public string Id             { get; set; } = Guid.NewGuid().ToString("N");
    public string Name           { get; set; } = "";
    public string Icon           { get; set; } = "🤖";
    public string Description    { get; set; } = "";
    public string SystemPrompt   { get; set; } = "";
    public string? ModelId       { get; set; }
    public bool   IsEnabled      { get; set; } = true;
    public bool   IsOrchestrator { get; set; }
    public int    SortOrder      { get; set; }

    /// <summary>JSON-serialised <c>List&lt;string&gt;</c> of plugin keys (e.g. "Database", "Pdf").</summary>
    public string AllowedPluginKeysJson    { get; set; } = "[]";
    /// <summary>JSON-serialised <c>List&lt;string&gt;</c> of <see cref="SkillEntity.Id"/> values.</summary>
    public string AllowedSkillIdsJson      { get; set; } = "[]";
    /// <summary>JSON-serialised <c>List&lt;string&gt;</c> of <see cref="McpServerEntity.Id"/> values.</summary>
    public string AllowedMcpServerIdsJson  { get; set; } = "[]";
    /// <summary>JSON-serialised <c>List&lt;string&gt;</c> of <see cref="DatabaseConnectionEntity.Id"/> values.</summary>
    public string AllowedDatabaseKeysJson  { get; set; } = "[]";
    /// <summary>JSON-serialised <c>List&lt;string&gt;</c> of <see cref="FlatFileSourceEntity.Id"/> values.</summary>
    public string AllowedFlatFileIdsJson   { get; set; } = "[]";
    /// <summary>JSON-serialised <c>List&lt;string&gt;</c> of <see cref="RagDocumentEntity.Id"/> values.</summary>
    public string AllowedRagDocumentIdsJson { get; set; } = "[]";
}
