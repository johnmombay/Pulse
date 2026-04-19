using System.ComponentModel.DataAnnotations;

namespace Pulse.Models;

public class LlmSettingsModel
{
    /// <summary>Display name shown in the browser title, sidebar, and outgoing emails.</summary>
    public string AppName { get; set; } = "Pulse";

    [Required(ErrorMessage = "Model ID is required.")]
    public string ModelId { get; set; } = "gemini-2.0-flash";

    public List<string> ApiKeys { get; set; } = [];

    public List<McpServerConfig> McpServers { get; set; } = [];

    public List<SkillConfig> Skills { get; set; } = [];

    public List<RagDocument> RagDocuments { get; set; } = [];

    /// <summary>
    /// Database connections managed through the Settings UI.
    /// Key = logical connection ID passed to db_* agent tools (e.g. "sales-db").
    /// </summary>
    public Dictionary<string, DatabaseConnectionEntry> DatabaseConnections { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Terminal (shell execution) settings.</summary>
    public TerminalSettings TerminalSettings { get; set; } = new();

    /// <summary>AgentMail email integration settings.</summary>
    public AgentMailSettings AgentMail { get; set; } = new();

    /// <summary>
    /// Flat-file data sources (CSV, Excel, JSON, XML, YAML, Fixed-Width).
    /// The agent can read these via the FlatFile plugin tools.
    /// </summary>
    public List<FlatFileDataSource> FlatFileSources { get; set; } = [];

    /// <summary>Login lockout / security settings.</summary>
    public SecuritySettings Security { get; set; } = new();

    /// <summary>Filename of the uploaded sidebar logo stored under wwwroot/images/.</summary>
    public string? LogoFileName { get; set; }

    /// <summary>Cache-buster token refreshed on every logo upload.</summary>
    public string? LogoVersion { get; set; }
}
