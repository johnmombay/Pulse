using Pulse.Models;
using Microsoft.SemanticKernel;
using System.Text.RegularExpressions;

namespace Pulse.Services;

/// <summary>
/// Builds a <see cref="KernelPlugin"/> that exposes one <see cref="KernelFunction"/> per
/// enabled specialist agent. The orchestrator uses the LLM's auto-invoke to route tasks to
/// the right specialist via <c>DelegateTo{Name}(task)</c>.
///
/// NOT DI-registered — created per-turn via <see cref="Build"/>.
/// </summary>
public static class AgentDelegationPlugin
{
    /// <summary>
    /// Creates a <see cref="KernelPlugin"/> containing one delegation function per specialist.
    /// </summary>
    /// <param name="specialists">Enabled non-orchestrator agents.</param>
    /// <param name="runner">Runner that executes each specialist in its own scoped kernel.</param>
    /// <param name="userId">Current user — forwarded to the runner for memory injection.</param>
    /// <param name="parentSessionId">Parent SignalR session — sub-agent chunks stream here.</param>
    public static KernelPlugin Build(
        IReadOnlyList<AgentDefinition> specialists,
        SpecializedAgentRunner runner,
        string userId,
        string parentSessionId)
    {
        var functions = specialists.Select(agent => BuildFunction(agent, runner, userId, parentSessionId));
        return KernelPluginFactory.CreateFromFunctions("Delegation", "Delegate tasks to specialist agents", functions);
    }

    private static KernelFunction BuildFunction(
        AgentDefinition agent,
        SpecializedAgentRunner runner,
        string userId,
        string parentSessionId)
    {
        var fnName = "DelegateTo" + SanitizeName(agent.Name);

        // Description drives the LLM's routing decision — include system-prompt excerpt so
        // the model understands what each specialist is best at.
        var excerpt = agent.SystemPrompt.Length > 200
            ? agent.SystemPrompt[..200] + "…"
            : agent.SystemPrompt;
        var description = string.IsNullOrWhiteSpace(agent.Description)
            ? excerpt
            : $"{agent.Description}\n\nApproach: {excerpt}";

        return KernelFunctionFactory.CreateFromMethod(
            method: async (string task, CancellationToken ct) =>
            {
                try
                {
                    return await runner.RunAsync(agent, task, userId, parentSessionId, ct);
                }
                catch (Exception ex)
                {
                    return $"Sub-agent {agent.Name} failed: {ex.Message}";
                }
            },
            functionName: fnName,
            description: description,
            parameters:
            [
                new KernelParameterMetadata("task")
                {
                    Description = "The complete, self-contained task for this specialist. " +
                                  "Include all context needed — the specialist has no memory of the current conversation.",
                    ParameterType = typeof(string),
                }
            ],
            returnParameter: new KernelReturnParameterMetadata
            {
                Description = "The specialist's full response text.",
                ParameterType = typeof(string),
            });
    }

    private static string SanitizeName(string name)
    {
        // Strip non-alphanumeric chars, collapse to PascalCase-safe identifier
        var clean = Regex.Replace(name.Trim(), @"[^a-zA-Z0-9]", "");
        return clean.Length > 0 ? clean : "Agent";
    }
}
