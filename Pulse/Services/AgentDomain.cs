namespace Pulse.Services;

/// <summary>
/// Execution domains used to scope lessons.
/// </summary>
public static class AgentDomain
{
    public const string Chat      = "Chat";
    public const string SubAgent  = "SubAgent";
    public const string Workflow  = "Workflow";
    public const string Scheduler = "Scheduler";
    public const string Telegram  = "Telegram";
}
