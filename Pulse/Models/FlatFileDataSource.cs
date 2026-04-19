using System.ComponentModel.DataAnnotations;

namespace Pulse.Models;

/// <summary>
/// A named flat-file data source that the agent can read and query.
/// Stored in <see cref="LlmSettingsModel.FlatFileSources"/>.
/// </summary>
public sealed class FlatFileDataSource
{
    /// <summary>Short slug used by the agent as an identifier, e.g. "sales-csv".</summary>
    [Required, MaxLength(60)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Human-readable display name.</summary>
    [Required, MaxLength(200)]
    public string Label { get; set; } = "";

    public FlatFileFormat Format { get; set; } = FlatFileFormat.Csv;

    /// <summary>Absolute path to the file on disk.</summary>
    [Required]
    public string FilePath { get; set; } = "";

    public bool IsEnabled { get; set; } = true;

    // ── CSV / Tab / Fixed-width ───────────────────────────────────────────────

    /// <summary>Whether the first row contains column headers. Applies to CSV, Tab, and Fixed-Width.</summary>
    public bool HasHeaders { get; set; } = true;

    /// <summary>
    /// Explicit delimiter character. Leave null for auto-detect
    /// (comma for CSV, tab for TabDelimited).
    /// </summary>
    public string? Delimiter { get; set; }

    // ── Excel ─────────────────────────────────────────────────────────────────

    /// <summary>Sheet name to read. Null = first sheet.</summary>
    public string? SheetName { get; set; }

    // ── Encoding ──────────────────────────────────────────────────────────────

    /// <summary>IANA/dotnet encoding name, e.g. "UTF-8", "windows-1252". Defaults to UTF-8.</summary>
    public string Encoding { get; set; } = "UTF-8";

    // ── Safety / limits ───────────────────────────────────────────────────────

    /// <summary>Maximum rows the agent may load in a single read call. Default 1 000.</summary>
    public int MaxRows { get; set; } = 1_000;

    // ── Fixed-width ───────────────────────────────────────────────────────────

    /// <summary>
    /// Column definitions for fixed-width files, stored as "Name:Start:Length" per entry,
    /// e.g. ["FirstName:0:20","LastName:20:20","Age:40:3"].
    /// </summary>
    public List<string> FixedWidthColumns { get; set; } = [];
}
