namespace Pulse.Models;

/// <summary>
/// Login security settings persisted in llm-settings.json.
/// </summary>
public class SecuritySettings
{
    /// <summary>Number of failed login attempts before the account is locked (default 5).</summary>
    public int MaxFailedLoginAttempts { get; set; } = 5;

    /// <summary>How long the account stays locked after reaching the attempt limit (default 1 hour).</summary>
    public int LoginLockoutHours { get; set; } = 1;
}
