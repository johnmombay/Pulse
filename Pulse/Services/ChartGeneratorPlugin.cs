using Pulse.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.Text.Json;

namespace Pulse.Services;

/// <summary>
/// Semantic Kernel plugin that renders interactive charts directly inside the chat window
/// via a SignalR <c>RenderChart</c> event — bypassing the LLM entirely so the chart
/// always appears regardless of whether Gemini echoes the spec in its text response.
///
/// One instance is created per agent execution with the current session ID already bound.
/// </summary>
public sealed class ChartGeneratorPlugin(
    string sessionId,
    string userId,
    IHubContext<AgentHub> hubContext,
    ChatHistoryService chatHistory,
    ILogger logger)
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    [KernelFunction("generate_chart")]
    [Description(
        "Renders an interactive Chart.js chart directly inside the chat window. " +
        "YOU MUST CALL THIS FUNCTION whenever the user asks for a chart, graph, plot, " +
        "or any visual representation of data. " +
        "NEVER respond with ASCII art, text-based charts, fenced code blocks, markdown tables, " +
        "or descriptions of a chart in place of calling this function — those are not charts. " +
        "Supported types: bar, line, pie, doughnut, radar, scatter. " +
        "Multiple datasets produce grouped or multi-series charts. " +
        "For area charts use type='line' and set fill=true on the dataset.")]
    public async Task<string> GenerateChart(
        [Description("Chart type: bar | line | pie | doughnut | radar | scatter")]
        string type,

        [Description("Title displayed above the chart.")]
        string title,

        [Description(
            "JSON string array of category labels. " +
            "Example: [\"Jan\",\"Feb\",\"Mar\",\"Apr\"]")]
        string labelsJson,

        [Description(
            "JSON array of dataset objects. Each needs 'label' (string) and 'data' (number[]). " +
            "Optional: 'color' (CSS hex) and 'fill' (bool, true = area chart). " +
            "Single series:  [{\"label\":\"Revenue ($K)\",\"data\":[120,180,150,210]}] " +
            "Multi-series:   [{\"label\":\"Revenue\",\"data\":[120,180,150]},{\"label\":\"Costs\",\"data\":[90,130,110]}]")]
        string datasetsJson,

        [Description("Set true to stack bars or area series. Default false.")]
        bool stacked = false)
    {
        try
        {
            var labels   = JsonDocument.Parse(labelsJson).RootElement.Clone();
            var datasets = JsonDocument.Parse(datasetsJson).RootElement.Clone();

            var spec = JsonSerializer.Serialize(new
            {
                type    = type.Trim().ToLowerInvariant(),
                title,
                stacked,
                labels,
                datasets,
            }, Compact);

            // Push directly to the browser — no LLM echo required
            await hubContext.Clients.Group(sessionId).SendAsync("RenderChart", spec);

            // Persist so the chart re-renders on page reload / history load
            chatHistory.AddDisplayMessage(sessionId, userId, "chart", spec);

            logger.LogInformation(
                "Chart pushed via SignalR: type={Type} title={Title} session={S}",
                type, title, sessionId[..Math.Min(8, sessionId.Length)]);

            return $"Chart \"{title}\" is now rendered in the chat window.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Chart generation failed for title: {Title}", title);
            return $"Chart generation failed: {ex.Message}. " +
                   "Check that labelsJson and datasetsJson are valid JSON arrays.";
        }
    }
}
