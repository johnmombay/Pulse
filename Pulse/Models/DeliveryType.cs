namespace Pulse.Models;

public enum DeliveryType
{
    Dashboard = 0,   // store on the home dashboard
    Email     = 1,   // send via AgentMail
    Both      = 2,   // dashboard + email
}
