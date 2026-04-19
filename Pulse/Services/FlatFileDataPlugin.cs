using Pulse.Models;
using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.Text.Json;

namespace Pulse.Services;

/// <summary>
/// Exposes flat-file data sources (CSV, Excel, JSON, XML, YAML, Fixed-width) as
/// Semantic Kernel <see cref="KernelFunction"/>s so the AI agent can list, inspect,
/// and read data from them exactly like database tables.
/// </summary>
public sealed class FlatFileDataPlugin(
    FlatFileDataService dataService,
    ILogger<FlatFileDataPlugin> logger)
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = false };

    // ── Tool: list all sources ────────────────────────────────────────────────

    [KernelFunction("flatfile_list_sources")]
    [Description(
        "Lists all configured flat-file data sources (CSV, Excel, JSON, XML, YAML, etc.) " +
        "with their IDs, labels, file formats, and file paths. " +
        "Call this first to discover which sourceId values are available.")]
    public string ListSources()
    {
        logger.LogInformation("Agent called flatfile_list_sources");

        var sources = dataService.GetAllSources()
            .Where(s => s.IsEnabled)
            .Select(s => new
            {
                id     = s.Id,
                label  = s.Label,
                format = s.Format.ToString(),
                path   = s.FilePath,
                maxRows= s.MaxRows,
            });

        return JsonSerializer.Serialize(sources, JsonOpts);
    }

    // ── Tool: schema ──────────────────────────────────────────────────────────

    [KernelFunction("flatfile_schema")]
    [Description(
        "Returns the column names (and inferred data types) plus the total row count " +
        "of a flat-file data source. Use this to understand the structure before reading data, " +
        "especially for large files.")]
    public async Task<string> GetSchemaAsync(
        [Description("Data source ID from flatfile_list_sources.")]
        string sourceId,
        CancellationToken ct = default)
    {
        logger.LogInformation("Agent called flatfile_schema({Id})", sourceId);

        var (rows, error) = await dataService.ReadAsync(sourceId, maxRows: 200, ct: ct);
        if (error is not null)
            return JsonSerializer.Serialize(new { error }, JsonOpts);

        if (rows.Count == 0)
            return JsonSerializer.Serialize(new { rowCount = 0, columns = Array.Empty<object>() }, JsonOpts);

        var columns = rows[0].Keys.Select(col =>
        {
            var values   = rows.Select(r => r.TryGetValue(col, out var v) ? v : "").ToList();
            var nonEmpty = values.Where(v => !string.IsNullOrEmpty(v)).ToList();
            var type     = InferType(nonEmpty);
            return new { name = col, type, sample = nonEmpty.Take(3).ToArray() };
        }).ToArray();

        return JsonSerializer.Serialize(new
        {
            rowCount = rows.Count,
            columns,
        }, JsonOpts);
    }

    // ── Tool: read data ───────────────────────────────────────────────────────

    [KernelFunction("flatfile_read")]
    [Description(
        "Reads a flat-file data source and returns the data as a JSON array of row objects. " +
        "Use maxRows to control how much data is loaded (default 100). " +
        "Use offset to paginate through large files (0-based row index). " +
        "Call flatfile_schema first to check total row count and column names.")]
    public async Task<string> ReadDataAsync(
        [Description("Data source ID from flatfile_list_sources.")]
        string sourceId,
        [Description("Maximum number of rows to return. Default 100. Increase only when needed.")]
        int maxRows = 100,
        [Description("Number of data rows to skip before returning (0 = start from beginning).")]
        int offset = 0,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "Agent called flatfile_read({Id}, maxRows={Max}, offset={Off})",
            sourceId, maxRows, offset);

        maxRows = Math.Clamp(maxRows, 1, 5_000);

        var (rows, error) = await dataService.ReadAsync(sourceId, maxRows, offset, ct);
        if (error is not null)
            return JsonSerializer.Serialize(new { error }, JsonOpts);

        return JsonSerializer.Serialize(new
        {
            sourceId,
            rowCount = rows.Count,
            offset,
            data = rows,
        }, JsonOpts);
    }

    // ── Type inference ────────────────────────────────────────────────────────

    private static string InferType(List<string> values)
    {
        if (values.Count == 0) return "string";
        if (values.All(v => int.TryParse(v, out _)))                        return "integer";
        if (values.All(v => decimal.TryParse(v, out _)))                    return "decimal";
        if (values.All(v => DateTime.TryParse(v, out _)))                   return "datetime";
        if (values.All(v => bool.TryParse(v, out _)
            || v is "0" or "1" or "yes" or "no" or "y" or "n"))             return "boolean";
        return "string";
    }
}
