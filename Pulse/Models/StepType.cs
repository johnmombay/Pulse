namespace Pulse.Models;

public enum StepType
{
    /// <summary>Agent executes the step and passes output to the next step.</summary>
    Execute = 0,
    /// <summary>Agent evaluates context and outputs [CONTINUE] or [STOP].</summary>
    Decision = 1,
    /// <summary>Calls an external n8n webhook and uses its response as step output.</summary>
    Webhook = 2,
    /// <summary>Calls another workflow and injects its full output as step context.</summary>
    CallWorkflow = 3,
}
