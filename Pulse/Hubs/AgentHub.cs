using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Pulse.Hubs;

/// <summary>
/// SignalR hub for real-time agent ↔ client communication.
/// Clients join a session group to receive streamed agent output.
/// </summary>
[Authorize]
public class AgentHub : Hub
{
    /// <summary>Subscribe to a specific agent session's stream.</summary>
    public async Task JoinSession(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
    }

    /// <summary>Unsubscribe from a session's stream.</summary>
    public async Task LeaveSession(string sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);
    }
}
