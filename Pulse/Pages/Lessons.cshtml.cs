using Pulse.Data;
using Pulse.Data.Entities;
using Pulse.Infrastructure;
using Pulse.Models;
using Pulse.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Pulse.Pages;

[Authorize(Roles = "TenantAdmin,SuperAdmin")]
public class LessonsModel(
    ApplicationDbContext db,
    AgentReflectionService reflectionService,
    LlmSettingsService llmSettings,
    ITenantContext tenantContext) : PageModel
{
    // ── Display data ──────────────────────────────────────────────────────────
    public IReadOnlyList<AgentLessonEntity> Lessons { get; private set; } = [];
    public IReadOnlyList<string> Domains { get; private set; } = [];
    public IReadOnlyList<AgentDefinition> AgentDefinitions { get; private set; } = [];
    public LessonStats Stats { get; private set; } = new();

    // ── Filters ───────────────────────────────────────────────────────────────
    [BindProperty(SupportsGet = true)] public string? DomainFilter   { get; set; }
    [BindProperty(SupportsGet = true)] public string? AgentFilter    { get; set; }
    [BindProperty(SupportsGet = true)] public string? OutcomeFilter  { get; set; }   // "success" | "failure" | ""

    // ── Test reflection form ──────────────────────────────────────────────────
    [BindProperty] public string TestGoal       { get; set; } = "";
    [BindProperty] public string TestAction     { get; set; } = "";
    [BindProperty] public string TestOutcome    { get; set; } = "";
    [BindProperty] public string TestDomain     { get; set; } = AgentDomain.Chat;
    [BindProperty] public bool   TestSucceeded  { get; set; } = true;

    public string? TestResultMessage { get; set; }

    private Guid   TenantId => tenantContext.TenantId ?? Guid.Empty;
    private string UserId   => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    // ── GET ───────────────────────────────────────────────────────────────────
    public async Task OnGetAsync()
    {
        await LoadPageDataAsync();
    }

    // ── Delete single lesson ──────────────────────────────────────────────────
    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var lesson = await db.AgentLessons
            .FirstOrDefaultAsync(l => l.Id == id && l.TenantId == TenantId);
        if (lesson is not null)
        {
            db.AgentLessons.Remove(lesson);
            await db.SaveChangesAsync();
        }
        return RedirectToPage();
    }

    // ── Clear all lessons for tenant ──────────────────────────────────────────
    public async Task<IActionResult> OnPostClearAsync()
    {
        var all = await db.AgentLessons
            .Where(l => l.TenantId == TenantId)
            .ToListAsync();
        db.AgentLessons.RemoveRange(all);
        await db.SaveChangesAsync();
        return RedirectToPage();
    }

    // ── Manual test reflection ────────────────────────────────────────────────
    public async Task<IActionResult> OnPostTestReflectAsync()
    {
        if (string.IsNullOrWhiteSpace(TestGoal) || string.IsNullOrWhiteSpace(TestOutcome))
        {
            TestResultMessage = "Goal and Outcome are required.";
            await LoadPageDataAsync();
            return Page();
        }

        try
        {
            var (score, critique) = await reflectionService.EvaluateAsync(
                TestGoal, TestOutcome, TestSucceeded);

            await reflectionService.ExtractAsync(
                UserId, TestDomain, TestGoal, TestAction, TestOutcome, score, critique, null);

            TestResultMessage = $"✓ Reflection complete. Score: {score:P0}. Lesson saved.";
        }
        catch (Exception ex)
        {
            TestResultMessage = $"Error: {ex.Message}";
        }

        await LoadPageDataAsync();
        return Page();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private async Task LoadPageDataAsync()
    {
        var settings = await llmSettings.GetAsync(TenantId);
        AgentDefinitions = settings.AgentDefinitions ?? [];
        Domains = [AgentDomain.Chat, AgentDomain.SubAgent, AgentDomain.Workflow,
                   AgentDomain.Scheduler, AgentDomain.Telegram];

        var q = db.AgentLessons.Where(l => l.TenantId == TenantId).AsQueryable();

        if (!string.IsNullOrEmpty(DomainFilter))
            q = q.Where(l => l.Domain == DomainFilter);

        if (!string.IsNullOrEmpty(AgentFilter))
            q = q.Where(l => AgentFilter == "global"
                ? l.AgentDefinitionId == null
                : l.AgentDefinitionId == AgentFilter);

        if (OutcomeFilter == "success")
            q = q.Where(l => l.Score >= 0.7);
        else if (OutcomeFilter == "failure")
            q = q.Where(l => l.Score < 0.7);

        Lessons = await q.OrderByDescending(l => l.UpdatedAt).Take(200).ToListAsync();

        var all = await db.AgentLessons.Where(l => l.TenantId == TenantId).ToListAsync();
        Stats = new LessonStats
        {
            Total           = all.Count,
            AvgScore        = all.Count > 0 ? all.Average(l => l.Score) : 0,
            SuccessCount    = all.Count(l => l.Score >= 0.7),
            FailureCount    = all.Count(l => l.Score < 0.7),
            DomainsLearned  = all.Select(l => l.Domain).Distinct().Count(),
            TopDomain       = all.GroupBy(l => l.Domain)
                                 .OrderByDescending(g => g.Count())
                                 .FirstOrDefault()?.Key ?? "—",
        };
    }

    public record LessonStats
    {
        public int    Total          { get; init; }
        public double AvgScore       { get; init; }
        public int    SuccessCount   { get; init; }
        public int    FailureCount   { get; init; }
        public int    DomainsLearned { get; init; }
        public string TopDomain     { get; init; } = "—";
    }
}
