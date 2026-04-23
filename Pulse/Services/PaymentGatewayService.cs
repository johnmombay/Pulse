using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Pulse.Data;
using Pulse.Data.Entities;

namespace Pulse.Services;

/// <summary>
/// Singleton service that manages payment gateway configuration for SuperAdmin.
/// Enforces the rule that only one gateway may be active at a time.
/// Uses <see cref="IMemoryCache"/> with a 5-minute sliding expiration.
/// </summary>
public sealed class PaymentGatewayService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory;
    private readonly IMemoryCache _cache;
    private const string CacheKey = "ActivePaymentGateway";

    public PaymentGatewayService(
        IDbContextFactory<ApplicationDbContext> factory,
        IMemoryCache cache)
    {
        _factory = factory;
        _cache   = cache;
    }

    public async Task<List<PaymentGatewaySettings>> GetAllAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.PaymentGatewaySettings.OrderBy(p => p.Provider).ToListAsync();
    }

    public async Task<PaymentGatewaySettings?> GetActiveAsync()
    {
        if (_cache.TryGetValue(CacheKey, out PaymentGatewaySettings? cached))
            return cached;

        await using var db = await _factory.CreateDbContextAsync();
        var active = await db.PaymentGatewaySettings.FirstOrDefaultAsync(p => p.IsActive);
        _cache.Set(CacheKey, active, TimeSpan.FromMinutes(5));
        return active;
    }

    public async Task ActivateAsync(PaymentGatewayProvider provider)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.PaymentGatewaySettings.ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false));
        await db.PaymentGatewaySettings
            .Where(p => p.Provider == provider)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.IsActive, true)
                .SetProperty(p => p.UpdatedAtUtc, DateTime.UtcNow));
        _cache.Remove(CacheKey);
    }

    public async Task DeactivateAllAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.PaymentGatewaySettings.ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false));
        _cache.Remove(CacheKey);
    }

    public async Task SaveAsync(PaymentGatewaySettings settings)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var existing = await db.PaymentGatewaySettings.FindAsync(settings.Id)
            ?? throw new InvalidOperationException($"Gateway {settings.Provider} not found.");

        existing.IsTestMode    = settings.IsTestMode;
        existing.ApiKey        = settings.ApiKey?.Trim();
        existing.SecretKey     = settings.SecretKey?.Trim();
        existing.WebhookSecret = settings.WebhookSecret?.Trim();
        existing.ClientId      = settings.ClientId?.Trim();
        existing.ClientSecret  = settings.ClientSecret?.Trim();
        existing.MerchantCode  = settings.MerchantCode?.Trim();
        existing.UpdatedAtUtc  = DateTime.UtcNow;

        await db.SaveChangesAsync();
        _cache.Remove(CacheKey);
    }
}
