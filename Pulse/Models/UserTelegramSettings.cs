using System.ComponentModel.DataAnnotations;

namespace Pulse.Models;

/// <summary>
/// Per-user Telegram integration settings stored in the database.
/// Each user registers their own bot (created via @BotFather).
/// </summary>
public class UserTelegramSettings
{
    public int Id { get; set; }

    [Required, MaxLength(450)]
    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }

    /// <summary>Bot token from @BotFather — unique per user.</summary>
    [MaxLength(200)]
    public string? BotToken { get; set; }

    /// <summary>Cached tenant ID for use in background bot handlers.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The Telegram Chat ID confirmed after the user completes the /pair flow.</summary>
    public long? ChatId { get; set; }

    /// <summary>Short-lived pair code shown in the UI; user sends it to their bot to complete pairing.</summary>
    [MaxLength(32)]
    public string? PairCode { get; set; }

    public DateTime? PairCodeExpiresAt { get; set; }

    /// <summary>True once the user has completed the /pair flow.</summary>
    public bool IsPaired { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
