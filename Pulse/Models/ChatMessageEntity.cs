using Pulse.Data.Entities;

namespace Pulse.Models;

/// <summary>
/// Represents a single display message (user, assistant, or chart) in a chat session.
/// Persisted to the database so conversations survive server restarts and navigation.
/// </summary>
public class ChatMessageEntity : ITenantOwned
{
    public Guid TenantId { get; set; }
    public int      Id        { get; set; }
    public string   SessionId { get; set; } = "";
    /// <summary>ASP.NET Identity user ID that owns this message.</summary>
    public string   UserId    { get; set; } = "";
    /// <summary>user | assistant | chart</summary>
    public string   Role      { get; set; } = "";
    public string   Content   { get; set; } = "";
    public DateTime Timestamp { get; set; }
}
