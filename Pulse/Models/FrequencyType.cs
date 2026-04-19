namespace Pulse.Models;

public enum FrequencyType
{
    Minutes = 0,   // every N minutes
    Hours   = 1,   // every N hours
    Days    = 2,   // every N days
    Weeks   = 3,   // every N weeks
    Months  = 4,   // every N months
    OneTime = 5,   // run once at a scheduled time
}
