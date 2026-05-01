namespace Pulse.Models;

public enum DeliveryType
{
    Dashboard         = 0,   // store on the home dashboard
    Email             = 1,   // send via AgentMail
    Both              = 2,   // dashboard + email  (kept for backwards compat)
    Telegram          = 3,   // send via Telegram bot
    DashboardEmail    = 4,   // dashboard + email
    DashboardTelegram = 5,   // dashboard + telegram
    EmailTelegram     = 6,   // email + telegram
    All               = 7,   // dashboard + email + telegram
}
