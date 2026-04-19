using Pulse.Data;
using Pulse.Models;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Services;

public sealed class WorkflowService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<WorkflowService> logger)
{
    public async Task<List<WorkflowDefinition>> GetAllAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.WorkflowDefinitions
            .Include(w => w.Steps.OrderBy(s => s.Order))
            .Where(w => w.UserId == userId)
            .OrderByDescending(w => w.UpdatedAt)
            .ToListAsync(ct);
    }

    public async Task<WorkflowDefinition?> GetAsync(int id, string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.WorkflowDefinitions
            .Include(w => w.Steps.OrderBy(s => s.Order))
            .FirstOrDefaultAsync(w => w.Id == id && w.UserId == userId, ct);
    }

    public async Task<WorkflowDefinition> CreateAsync(WorkflowDefinition def, CancellationToken ct = default)
    {
        def.CreatedAt = def.UpdatedAt = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.WorkflowDefinitions.Add(def);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Created workflow {Id}: {Title}", def.Id, def.Title);
        return def;
    }

    public async Task UpdateAsync(WorkflowDefinition def, CancellationToken ct = default)
    {
        def.UpdatedAt = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.WorkflowSteps.Where(s => s.WorkflowDefinitionId == def.Id).ExecuteDeleteAsync(ct);
        db.WorkflowDefinitions.Update(def);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Updated workflow {Id}: {Title}", def.Id, def.Title);
    }

    public async Task DeleteAsync(int id, string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Clear any cross-workflow references before deleting (ClientSetNull doesn't cascade via DB)
        await db.WorkflowSteps
            .Where(s => s.CalledWorkflowId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CalledWorkflowId, (int?)null), ct);
        await db.WorkflowDefinitions.Where(w => w.Id == id && w.UserId == userId).ExecuteDeleteAsync(ct);
    }

    public async Task ToggleAsync(int id, string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var def = await db.WorkflowDefinitions.FirstOrDefaultAsync(w => w.Id == id && w.UserId == userId, ct);
        if (def is null) return;
        def.IsEnabled = !def.IsEnabled;
        def.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<WorkflowRun> CreateRunAsync(WorkflowDefinition def, string userId, CancellationToken ct = default)
    {
        var run = new WorkflowRun
        {
            WorkflowDefinitionId = def.Id,
            UserId               = userId,
            WorkflowTitle        = def.Title,
            Status               = "running",
            StartedAt            = DateTime.UtcNow,
        };
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.WorkflowRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    public async Task UpdateRunAsync(WorkflowRun run, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.WorkflowRuns.Update(run);
        await db.SaveChangesAsync(ct);
    }

    public async Task AddStepRunAsync(WorkflowStepRun stepRun, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.WorkflowStepRuns.Add(stepRun);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<WorkflowRun>> GetRecentRunsAsync(string userId, int limit = 20, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.WorkflowRuns
            .Include(r => r.StepRuns.OrderBy(s => s.StepOrder))
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.StartedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<WorkflowRun?> GetRunAsync(int id, string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.WorkflowRuns
            .Include(r => r.StepRuns.OrderBy(s => s.StepOrder))
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, ct);
    }

    public async Task DeleteRunAsync(int id, string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.WorkflowRuns
            .Where(r => r.Id == id && r.UserId == userId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<int> DeleteAllRunsAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.WorkflowRuns
            .Where(r => r.UserId == userId)
            .ExecuteDeleteAsync(ct);
    }
}
