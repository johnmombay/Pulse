using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Pulse.Data;
using Pulse.Data.Entities;

namespace Pulse.Services;

/// <summary>
/// Singleton service managing the one global <see cref="GlobalLlmSettings"/> row used
/// by every tenant. Only SuperAdmin may modify it. Uses <see cref="IMemoryCache"/>
/// with a 5-minute sliding expiration.
/// </summary>
public sealed class GlobalLlmSettingsService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory;
    private readonly IMemoryCache _cache;
    private const string CacheKey = "GlobalLlmSettings";

    public GlobalLlmSettingsService(
        IDbContextFactory<ApplicationDbContext> factory,
        IMemoryCache cache)
    {
        _factory = factory;
        _cache   = cache;
    }

    public async Task<GlobalLlmSettings> GetAsync()
    {
        if (_cache.TryGetValue(CacheKey, out GlobalLlmSettings? cached) && cached != null)
            return cached;

        await using var db = await _factory.CreateDbContextAsync();
        var settings = await db.GlobalLlmSettings.FindAsync(1)
                       ?? new GlobalLlmSettings();

        _cache.Set(CacheKey, settings, TimeSpan.FromMinutes(5));
        return settings;
    }

    public async Task SaveAsync(GlobalLlmSettings settings)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var existing = await db.GlobalLlmSettings.FindAsync(1);
        if (existing is null)
        {
            settings.Id = 1;
            db.GlobalLlmSettings.Add(settings);
        }
        else
        {
            existing.ModelId = settings.ModelId;
        }
        await db.SaveChangesAsync();
        _cache.Remove(CacheKey);
    }
}
