using Pulse.Models;

namespace Pulse.Services.Telegram;

public enum WizardStep
{
    Idle,
    AwaitingTitle,
    AwaitingInstructions,
    AwaitingFrequency,       // natural language: "every day", "every 3 hours", "once", "weekly"
    AwaitingScheduledAt,     // natural language: "tomorrow at 9am", "next Monday", "in 2 hours"
    AwaitingDeliveryType,    // natural language: "email", "dashboard and telegram", "all"
    AwaitingDeliveryEmail,

    // kept for any in-flight sessions
    AwaitingFrequencyType,
    AwaitingFrequencyValue,
}

public sealed class TelegramConversationState
{
    public WizardStep    Step           { get; set; } = WizardStep.Idle;
    public string        UserId         { get; set; } = "";
    public Guid          TenantId       { get; set; }
    public string        Title          { get; set; } = "";
    public string        Instructions   { get; set; } = "";
    public FrequencyType FrequencyType  { get; set; } = FrequencyType.Days;
    public int           FrequencyValue { get; set; } = 1;
    public DateTime?     ScheduledAt    { get; set; }
    public DeliveryType  DeliveryType   { get; set; } = DeliveryType.Dashboard;
    public string?       DeliveryEmail  { get; set; }
}
