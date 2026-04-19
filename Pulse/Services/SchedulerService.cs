using Pulse.Data;
using Pulse.Jobs;
using Pulse.Models;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Services;

/// <summary>
/// Manages <see cref="ScheduledTask"/> records and their corresponding Hangfire jobs.
/// Singleton — uses <see cref="IDbContextFactory{T}"/> so every operation gets a
/// short-lived DbContext and is thread-safe.
/// </summary>
public sealed class SchedulerService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IRecurringJobManager recurringJobs,
    IBackgroundJobClient backgroundJobs,
    ILogger<SchedulerService> logger)
{
    // ── Task CRUD ─────────────────────────────────────────────────────────────

    public async Task<List<ScheduledTask>> GetAllAsync(
        string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ScheduledTasks
            .Include(t => t.WorkflowDefinition)
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<ScheduledTask?> GetAsync(
        int id, string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ScheduledTasks
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, ct);
    }

    /// <summary>Used by jobs — no userId guard.</summary>
    public async Task<ScheduledTask?> GetByIdAsync(
        int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ScheduledTasks.FindAsync([id], ct);
    }

    public async Task<ScheduledTask> CreateAsync(
        ScheduledTask task, CancellationToken ct = default)
    {
        task.CreatedAt = task.UpdatedAt = DateTime.UtcNow;
        task.NextRunAt = ComputeNextRun(task, DateTime.UtcNow);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.ScheduledTasks.Add(task);
        await db.SaveChangesAsync(ct);

        if (task.IsEnabled)
            RegisterHangfireJob(task);

        logger.LogInformation("Created scheduled task {Id}: {Title}", task.Id, task.Title);
        return task;
    }

    public async Task UpdateAsync(ScheduledTask task, CancellationToken ct = default)
    {
        task.UpdatedAt = DateTime.UtcNow;
        task.NextRunAt = ComputeNextRun(task, DateTime.UtcNow);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.ScheduledTasks.Update(task);
        await db.SaveChangesAsync(ct);

        UnregisterHangfireJob(task.Id);
        if (task.IsEnabled)
            RegisterHangfireJob(task);

        logger.LogInformation("Updated scheduled task {Id}: {Title}", task.Id, task.Title);
    }

    public async Task DeleteAsync(int id, string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.ScheduledTasks
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, ct);
        if (task is null) return;

        UnregisterHangfireJob(id);
        db.ScheduledTasks.Remove(task);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Deleted scheduled task {Id}", id);
    }

    public async Task ToggleAsync(int id, string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.ScheduledTasks
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, ct);
        if (task is null) return;

        task.IsEnabled = !task.IsEnabled;
        task.UpdatedAt = DateTime.UtcNow;

        if (task.IsEnabled)
            task.NextRunAt = ComputeNextRun(task, DateTime.UtcNow);

        await db.SaveChangesAsync(ct);

        if (task.IsEnabled)
            RegisterHangfireJob(task);
        else
            UnregisterHangfireJob(task.Id);
    }

    /// <summary>Called by the job after execution to update status and next run time.</summary>
    public async Task MarkLastRunAsync(
        int taskId, string status, string? error, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var task = await db.ScheduledTasks.FindAsync([taskId], ct);
        if (task is null) return;

        task.LastRunAt  = DateTime.UtcNow;
        task.LastStatus = status;
        task.LastError  = error;
        task.UpdatedAt  = DateTime.UtcNow;

        if (task.FrequencyType == FrequencyType.OneTime)
        {
            task.IsEnabled = false;
            task.NextRunAt = null;
            UnregisterHangfireJob(task.Id);
        }
        else
        {
            task.NextRunAt = ComputeNextRun(task, DateTime.UtcNow);
        }

        await db.SaveChangesAsync(ct);
    }

    // ── Result CRUD ───────────────────────────────────────────────────────────

    public async Task SaveResultAsync(
        ScheduledTaskResult result, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.ScheduledTaskResults.Add(result);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<ScheduledTaskResult>> GetRecentResultsAsync(
        string userId, int limit = 20, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ScheduledTaskResults
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.RunAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<int> GetUnreadCountAsync(
        string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ScheduledTaskResults
            .CountAsync(r => r.UserId == userId && !r.IsRead, ct);
    }

    public async Task MarkResultReadAsync(
        int resultId, string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var result = await db.ScheduledTaskResults
            .FirstOrDefaultAsync(r => r.Id == resultId && r.UserId == userId, ct);
        if (result is null) return;
        result.IsRead = true;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkAllResultsReadAsync(
        string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ScheduledTaskResults
            .Where(r => r.UserId == userId && !r.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsRead, true), ct);
    }

    public async Task DeleteResultAsync(
        int resultId, string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ScheduledTaskResults
            .Where(r => r.Id == resultId && r.UserId == userId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task DeleteAllResultsAsync(
        string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ScheduledTaskResults
            .Where(r => r.UserId == userId)
            .ExecuteDeleteAsync(ct);
    }

    // ── Hangfire registration ─────────────────────────────────────────────────

    public void RegisterHangfireJob(ScheduledTask task)
    {
        if (task.FrequencyType == FrequencyType.OneTime)
        {
            var delay = task.NextRunAt.HasValue
                ? task.NextRunAt.Value - DateTime.UtcNow
                : TimeSpan.Zero;
            if (delay < TimeSpan.Zero) delay = TimeSpan.FromSeconds(5);

            backgroundJobs.Schedule<ScheduledTaskJob>(
                j => j.RunAsync(task.Id, JobCancellationToken.Null),
                delay);
        }
        else
        {
            recurringJobs.AddOrUpdate<ScheduledTaskJob>(
                HangfireJobId(task.Id),
                j => j.RunAsync(task.Id, JobCancellationToken.Null),
                BuildCron(task),
                new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        }

        logger.LogInformation(
            "Registered Hangfire job for task {Id} ({Title}) — {Freq} every {Val}",
            task.Id, task.Title, task.FrequencyType, task.FrequencyValue);
    }

    public void UnregisterHangfireJob(int taskId)
        => recurringJobs.RemoveIfExists(HangfireJobId(taskId));

    public static string HangfireJobId(int taskId) => $"sched-{taskId}";

    // ── Cron + next-run helpers ───────────────────────────────────────────────

    internal static string BuildCron(ScheduledTask task)
    {
        var n = Math.Max(1, task.FrequencyValue);
        return task.FrequencyType switch
        {
            FrequencyType.Minutes => $"*/{Math.Min(n, 59)} * * * *",
            FrequencyType.Hours   => $"0 */{Math.Min(n, 23)} * * *",
            FrequencyType.Days    => n == 1 ? "0 9 * * *"  : $"0 9 */{n} * *",
            FrequencyType.Weeks   => "0 9 * * 1",   // every Monday; job checks if N weeks elapsed
            FrequencyType.Months  => n == 1 ? "0 9 1 * *"  : $"0 9 1 */{n} *",
            _                     => "0 9 * * *"
        };
    }

    public static DateTime ComputeNextRun(ScheduledTask task, DateTime from) =>
        task.FrequencyType switch
        {
            FrequencyType.OneTime  => task.ScheduledAt ?? from.AddMinutes(1),
            FrequencyType.Minutes  => from.AddMinutes(task.FrequencyValue),
            FrequencyType.Hours    => from.AddHours(task.FrequencyValue),
            FrequencyType.Days     => from.AddDays(task.FrequencyValue),
            FrequencyType.Weeks    => from.AddDays(task.FrequencyValue * 7),
            FrequencyType.Months   => from.AddMonths(task.FrequencyValue),
            _                      => from.AddDays(1)
        };
}
