using Microsoft.EntityFrameworkCore;
using Pulse.Data;
using Pulse.Models;

namespace Pulse.Services.Telegram;

public sealed class UserTelegramSettingsService(
    IDbContextFactory<ApplicationDbContext> dbFactory)
{
    public async Task<UserTelegramSettings?> GetByUserIdAsync(
        string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.UserTelegramSettings
            .FirstOrDefaultAsync(t => t.UserId == userId, ct);
    }

    public async Task<UserTelegramSettings?> GetByPairCodeAsync(
        string pairCode, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.UserTelegramSettings
            .FirstOrDefaultAsync(t =>
                t.PairCode == pairCode &&
                t.PairCodeExpiresAt > DateTime.UtcNow, ct);
    }

    public async Task<UserTelegramSettings?> GetByChatIdAsync(
        long chatId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.UserTelegramSettings
            .FirstOrDefaultAsync(t => t.ChatId == chatId && t.IsPaired, ct);
    }

    /// <summary>Save or update bot token for a user. Resets pairing if the token changes.</summary>
    public async Task<UserTelegramSettings> SaveBotTokenAsync(
        string userId, Guid tenantId, string botToken, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await db.UserTelegramSettings
            .FirstOrDefaultAsync(t => t.UserId == userId, ct);

        if (settings is null)
        {
            settings = new UserTelegramSettings { UserId = userId };
            db.UserTelegramSettings.Add(settings);
        }

        settings.TenantId = tenantId;

        if (settings.BotToken != botToken)
        {
            settings.BotToken          = botToken;
            settings.ChatId            = null;
            settings.IsPaired          = false;
            settings.PairCode          = null;
            settings.PairCodeExpiresAt = null;
        }

        settings.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return settings;
    }

    /// <summary>Generate a fresh 8-char pair code valid for 15 minutes.</summary>
    public async Task<string> GeneratePairCodeAsync(
        string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await db.UserTelegramSettings
            .FirstOrDefaultAsync(t => t.UserId == userId, ct)
            ?? throw new InvalidOperationException("No Telegram settings found. Save a bot token first.");

        settings.PairCode          = Guid.NewGuid().ToString("N")[..8].ToUpper();
        settings.PairCodeExpiresAt = DateTime.UtcNow.AddMinutes(15);
        settings.UpdatedAt         = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return settings.PairCode;
    }

    /// <summary>Complete pairing — store Chat ID and clear pair code.</summary>
    public async Task CompletePairingAsync(
        int settingsId, long chatId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await db.UserTelegramSettings.FindAsync([settingsId], ct);
        if (settings is null) return;

        settings.ChatId            = chatId;
        settings.IsPaired          = true;
        settings.PairCode          = null;
        settings.PairCodeExpiresAt = null;
        settings.UpdatedAt         = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task UnpairAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await db.UserTelegramSettings
            .FirstOrDefaultAsync(t => t.UserId == userId, ct);
        if (settings is null) return;

        settings.ChatId            = null;
        settings.IsPaired          = false;
        settings.PairCode          = null;
        settings.PairCodeExpiresAt = null;
        settings.UpdatedAt         = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<UserTelegramSettings>> GetAllWithTokenAsync(
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.UserTelegramSettings
            .Where(t => t.BotToken != null)
            .ToListAsync(ct);
    }
}
