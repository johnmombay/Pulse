using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.Text.Json;

namespace Pulse.Services;

/// <summary>
/// Lightweight chart plugin for scheduled (background) agent runs.
/// Instead of pushing via SignalR (no live browser session exists), it
/// captures each chart spec in memory.  After the run, call
/// <see cref="DrainSpecs"/> and embed the results as
/// <c>[CHART_SPEC]…[/CHART_SPEC]</c> markers appended to the output text.
/// The Dashboard page then parses those markers and renders them with Chart.js.
/// </summary>
public sealed class ScheduledChartPlugin
{
    public const string MarkerStart = "[CHART_SPEC]";
    public const string MarkerEnd   = "[/CHART_SPEC]";

    private readonly List<string> _specs = [];
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    [KernelFunction("generate_chart")]
    [Description(
        "Renders an interactive Chart.js chart in the scheduled report. " +
        "YOU MUST CALL THIS FUNCTION whenever the task asks for a chart, graph, plot, " +
        "or any visual representation of data. " +
        "NEVER respond with ASCII art, text-based charts, fenced code blocks, markdown tables, " +
        "or descriptions of a chart in place of calling this function — those are not charts. " +
        "Supported types: bar, line, pie, doughnut, radar, scatter. " +
        "Multiple datasets produce grouped or multi-series charts. " +
        "For area charts use type='line' and set fill=true on the dataset.")]
    public string GenerateChart(
        [Description("Chart type: bar | line | pie | doughnut | radar | scatter")]
        string type,

        [Description("Title displayed above the chart.")]
        string title,

        [Description("JSON array of category labels. Example: [\"Jan\",\"Feb\",\"Mar\"]")]
        string labelsJson,

        [Description(
            "JSON array of dataset objects. Each needs 'label' (string) and 'data' (number[]). " +
            "Optional: 'color' (CSS hex) and 'fill' (bool, true = area chart). " +
            "Example: [{\"label\":\"Temp °C\",\"data\":[29,28,27,31,29]}]")]
        string datasetsJson,

        [Description("Set true to stack bars or area series. Default false.")]
        bool stacked = false)
    {
        try
        {
            var labels   = JsonDocument.Parse(labelsJson).RootElement.Clone();
            var datasets = JsonDocument.Parse(datasetsJson).RootElement.Clone();

            var spec = JsonSerializer.Serialize(
                new { type = type.Trim().ToLowerInvariant(), title, stacked, labels, datasets },
                Compact);

            _specs.Add(spec);

            return $"Chart \"{title}\" generated — it will be rendered in the dashboard.";
        }
        catch (Exception ex)
        {
            return $"Chart generation failed: {ex.Message}. " +
                   "Ensure labelsJson and datasetsJson are valid JSON arrays.";
        }
    }

    /// <summary>Returns all captured specs and clears the internal list.</summary>
    public IReadOnlyList<string> DrainSpecs()
    {
        var list = _specs.ToList();
        _specs.Clear();
        return list;
    }

    /// <summary>
    /// Appends <c>[CHART_SPEC]…[/CHART_SPEC]</c> blocks to <paramref name="text"/>
    /// for every spec captured during the run.
    /// </summary>
    public string EmbedIn(string text)
    {
        var specs = DrainSpecs();
        if (specs.Count == 0) return text;
        return text + string.Concat(specs.Select(s => $"\n{MarkerStart}{s}{MarkerEnd}"));
    }
}
