using Pulse.Models;

namespace Pulse.Services.Telegram;

public enum WizardStep
{
    Idle,
    AwaitingTitle,
    AwaitingInstructions,
    AwaitingFrequencyType,
    AwaitingFrequencyValue,
    AwaitingScheduledAt,
    AwaitingDeliveryType,
    AwaitingDeliveryEmail,
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
