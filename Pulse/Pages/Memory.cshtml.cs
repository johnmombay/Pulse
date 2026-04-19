using Pulse.Models;
using Pulse.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace Pulse.Pages;

[Authorize]
public class MemoryModel(MemoryService memoryService) : PageModel
{
    public IReadOnlyList<AgentMemory> Memories { get; private set; } = [];

    [BindProperty] public MemoryInput Input { get; set; } = new();

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    public async Task OnGetAsync()
        => Memories = await memoryService.GetAllAsync(UserId);

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
