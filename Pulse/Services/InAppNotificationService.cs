using Microsoft.EntityFrameworkCore;
using Pulse.Data;
using Pulse.Data.Entities;

namespace Pulse.Services;

public interface IInAppNotificationService
{
    Task NotifyAsync(string userId, string title, string body, string? actionUrl = null, CancellationToken ct = default);
    Task<List<InAppNotification>> GetUnreadAsync(string userId, CancellationToken ct = default);
    Task<int> GetUnreadCountAsync(string userId, CancellationToken ct = default);
    Task MarkReadAsync(int notificationId, string userId, CancellationToken ct = default);
    Task MarkAllReadAsync(string userId, CancellationToken ct = default);
}

public sealed class InAppNotificationService(
    IDbContextFactory<ApplicationDbContext> factory) : IInAppNotificationService
{
    public async Task NotifyAsync(
        string userId, string title, string body,
        string? actionUrl = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.InAppNotifications.Add(new InAppNotification
        {
            UserId    = userId,
            Title     = title,
            Body      = body,
            ActionUrl = actionUrl,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<InAppNotification>> GetUnreadAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.InAppNotifications
            .Where(n => n.UserId == userId && !n.IsRead)
            .OrderByDescending(n => n.CreatedUtc)
            .ToListAsync(ct);
    }

    public async Task<int> GetUnreadCountAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.InAppNotifications
            .CountAsync(n => n.UserId == userId && !n.IsRead, ct);
    }

    public async Task MarkReadAsync(int notificationId, string userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.InAppNotifications
            .Where(n => n.Id == notificationId && n.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), ct);
    }

    public async Task MarkAllReadAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.InAppNotifications
            .Where(n => n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), ct);
    }
}
