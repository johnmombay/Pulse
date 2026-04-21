namespace Pulse.Models;

/// <summary>
/// Runtime DTO describing a specialized agent (role + plugin subset).
/// Persisted via <c>Pulse.Data.Entities.AgentDefinitionEntity</c>; the
/// <c>Allowed*</c> lists are stored there as JSON strings.
/// </summary>
public class AgentDefinition
{
    public string Id             { get; set; } = Guid.NewGuid().ToString("N");
    public string Name           { get; set; } = "";
    public string Icon           { get; set; } = "🤖";
    public string Description    { get; set; } = "";
    public string SystemPrompt   { get; set; } = "";

    /// <summary>Optional per-agent model override. Null → use global <c>LlmSettingsModel.ModelId</c>.</summary>
    public string? ModelId       { get; set; }

    public bool   IsEnabled      { get; set; } = true;

    /// <summary>
    /// True for the single chat entry-point agent. Exactly one per tenant
    /// (enforced by filtered unique index in <c>ApplicationDbContext</c>).
    /// </summary>
    public bool   IsOrchestrator { get; set; }

    public int    SortOrder      { get; set; }

    /// <summary>Plugin keys this agent is permitted to use. See <see cref="PluginKeys"/>.</summary>
    public List<string> AllowedPluginKeys     { get; set; } = new();

    /// <summary>IDs of skills whose instructions are injected into this agent's system prompt.</summary>
    public List<string> AllowedSkillIds       { get; set; } = new();

    /// <summary>IDs of MCP servers this agent can reach.</summary>
    public List<string> AllowedMcpServerIds   { get; set; } = new();

    /// <summary>Keys of database connections this agent can query.</summary>
    public List<string> AllowedDatabaseKeys   { get; set; } = new();

    /// <summary>IDs of flat-file sources this agent can read.</summary>
    public List<string> AllowedFlatFileIds    { get; set; } = new();

    /// <summary>IDs of RAG documents this agent can retrieve from.</summary>
    public List<string> AllowedRagDocumentIds { get; set; } = new();

    /// <summary>
    /// Canonical plugin-key identifiers used in <see cref="AllowedPluginKeys"/>.
    /// Must stay in sync with the plugin registration code in
    /// <c>Pulse.Services.AgentOrchestrationService</c>.
    /// </summary>
    public static class PluginKeys
    {
        public const string Database     = "Database";
        public const string Pdf          = "Pdf";
        public const string Word         = "Word";
        public const string Excel        = "Excel";
        public const string Terminal     = "Terminal";
        public const string AgentMail    = "AgentMail";
        public const string FlatFileData = "FlatFileData";
        public const string Chart        = "Chart";
        public const string Rag          = "Rag";
        public const string Mcp          = "Mcp";
    }
}
