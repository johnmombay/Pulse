using Pulse.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Controllers;

/// <summary>
/// Lightweight AJAX API for the flat-file data sources section of the Settings page.
/// </summary>
[Authorize]
[Route("api/flatfile")]
public class FlatFileController(LlmSettingsService settingsService) : Controller
{
    /// <summary>Returns a single flat-file source by ID for the edit modal.</summary>
    [HttpGet("source/{id}")]
    public IActionResult GetSource(string id)
    {
        var src = (settingsService.Get().FlatFileSources ?? [])
            .FirstOrDefault(s => s.Id == id);

        if (src is null) return NotFound();

        return Ok(new
        {
            src.Id,
            src.Label,
            src.Format,
            src.FilePath,
            src.IsEnabled,
            src.HasHeaders,
            src.Delimiter,
            src.SheetName,
            src.Encoding,
            src.MaxRows,
            src.FixedWidthColumns,
        });
    }
}
