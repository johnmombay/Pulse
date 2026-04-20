using System.ComponentModel.DataAnnotations;

namespace Pulse.Data.Entities;

/// <summary>
/// Global AgentMail configuration managed exclusively by SuperAdmin.
/// Singleton row enforced by CHECK constraint (Id = 1).
/// </summary>
public class GlobalAgentMailSettings
{
    public int Id { get; set; } = 1;

    public bool IsEnabled { get; set; }

    [MaxLength(500)]
    public string ApiBaseUrl { get; set; } = string.Empty;

    [MaxLength(500)]
    public string ApiKey { get; set; } = string.Empty;

    [MaxLength(200)]
    public string FromAddress { get; set; } = string.Empty;

    [MaxLength(200)]
    public string FromName { get; set; } = string.Empty;
}
