using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Pulse.Data;
using Pulse.Data.Entities;

namespace Pulse.Services;

/// <summary>
/// Singleton service managing the one global <see cref="GlobalAgentMailSettings"/> row.
/// Accessible only by SuperAdmin. Uses <see cref="IMemoryCache"/> with a 5-minute
/// sliding expiration.
/// </summary>
public sealed class GlobalAgentMailSettingsService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory;
    private readonly IMemoryCache _cache;
    private const string CacheKey = "GlobalAgentMailSettings";

    public GlobalAgentMailSettingsService(
        IDbContextFactory<ApplicationDbContext> factory,
        IMemoryCache cache)
    {
        _factory = factory;
        _cache   = cache;
    }

    public async Task<GlobalAgentMailSettings> GetAsync()
    {
        if (_cache.TryGetValue(CacheKey, out GlobalAgentMailSettings? cached) && cached != null)
            return cached;

        await using var db = await _factory.CreateDbContextAsync();
        var settings = await db.GlobalAgentMailSettings.FindAsync(1)
                       ?? new GlobalAgentMailSettings();

        _cache.Set(CacheKey, settings, TimeSpan.FromMinutes(5));
        return settings;
    }

    public async Task SaveAsync(GlobalAgentMailSettings settings)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var existing = await db.GlobalAgentMailSettings.FindAsync(1);
        if (existing is null)
        {
            settings.Id = 1;
            db.GlobalAgentMailSettings.Add(settings);
        }
        else
        {
            existing.IsEnabled    = settings.IsEnabled;
            existing.ApiBaseUrl   = settings.ApiBaseUrl;
            existing.ApiKey       = settings.ApiKey;
            existing.FromAddress  = settings.FromAddress;
            existing.FromName     = settings.FromName;
            existing.DefaultInbox = settings.DefaultInbox;
        }
        await db.SaveChangesAsync();
        _cache.Remove(CacheKey);
    }
}
