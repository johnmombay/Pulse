namespace Pulse.Data.Entities;

/// <summary>
/// Global AgentMail configuration managed exclusively by SuperAdmin.
/// Singleton row enforced by CHECK constraint (Id = 1).
/// </summary>
public class GlobalAgentMailSettings
{
    public int Id { get; set; } = 1;

    public bool IsEnabled { get; set; }

    public string ApiBaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = string.Empty;
}
