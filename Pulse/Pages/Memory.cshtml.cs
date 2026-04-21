using Pulse.Infrastructure;
using Pulse.Models;
using Pulse.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace Pulse.Pages;

[Authorize]
public class MemoryModel(
    MemoryService memoryService,
    LlmSettingsService llmSettings,
    ITenantContext tenantContext) : PageModel
{
    public IReadOnlyList<AgentMemory> Memories { get; private set; } = [];

    /// <summary>Agent definitions available for the filter dropdown.</summary>
    public IReadOnlyList<AgentDefinition> AgentDefinitions { get; private set; } = [];

    /// <summary>
    /// Current filter value bound from the query string.
    /// <c>null</c> or empty = All; <c>"global"</c> = Global (null AgentDefinitionId);
    /// any other value = specific AgentDefinitionId.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string? AgentFilter { get; set; }

    [BindProperty] public MemoryInput Input { get; set; } = new();

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    public async Task OnGetAsync()
    {
        var tenantId = tenantContext.TenantId ?? Guid.Empty;
        var settings = await llmSettings.GetAsync(tenantId);
        AgentDefinitions = settings.AgentDefinitions ?? [];

        var all = await memoryService.GetAllAsync(UserId);

        Memories = AgentFilter switch
        {
            null or "" => all,
            "global"   => all.Where(m => m.AgentDefinitionId == null).ToList(),
            var id     => all.Where(m => m.AgentDefinitionId == id).ToList(),
        };
    }

    // ── Add / Update (AJAX) ───────────────────────────────────────────────────
    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Input?.Content))
            return new JsonResult(new { success = false, error = "Content is required." });

        try
        {
            if (string.IsNullOrWhiteSpace(Input.Id))
            {
                await memoryService.AddManualAsync(
                    UserId, Input.Content, Input.Category?.Trim(), Input.Importance);
            }
            else if (!int.TryParse(Input.Id, out var id) ||
                     !await memoryService.UpdateAsync(id, UserId, Input.Content, Input.Category?.Trim(), Input.Importance))
            {
                return new JsonResult(new { success = false, error = "Memory not found." });
            }

            return new JsonResult(new { success = true });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, error = ex.Message }) { StatusCode = 500 };
        }
    }

    // ── Delete (AJAX) ─────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        await memoryService.DeleteAsync(id, UserId);
        return new JsonResult(new { success = true });
    }

    // ── Input model ───────────────────────────────────────────────────────────
    public class MemoryInput
    {
        public string? Id { get; set; }

        [Required, MaxLength(2000)]
        public string Content { get; set; } = "";

        [MaxLength(60)]
        public string? Category { get; set; }

        [Range(1, 5)]
        public int Importance { get; set; } = 3;
    }
}
