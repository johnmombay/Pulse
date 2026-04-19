namespace Pulse.Models;

/// <summary>
/// Configuration for the AgentMail integration (uspark@agentmail.to).
/// Stored in llm-settings.json alongside all other agent settings.
/// </summary>
public class AgentMailSettings
{
    public bool   IsEnabled    { get; set; } = false;
    public string ApiKey       { get; set; } = "";
    public string BaseUrl      { get; set; } = "https://api.agentmail.to/v0";
    public string DefaultInbox { get; set; } = "";
}
