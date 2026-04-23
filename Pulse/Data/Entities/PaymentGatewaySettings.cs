namespace Pulse.Data.Entities;

public enum PaymentGatewayProvider
{
    HitPay,
    PayMongo,
    DragonPay,
    Stripe,
    PayPal
}

/// <summary>
/// One row per payment gateway provider. Only one row may have <see cref="IsActive"/> = true
/// at any time. Managed exclusively by SuperAdmin.
/// </summary>
public class PaymentGatewaySettings
{
    public int Id { get; set; }

    public PaymentGatewayProvider Provider { get; set; }

    public bool IsActive { get; set; }

    public bool IsTestMode { get; set; } = true;

    // ── Credentials (provider-specific fields) ─────────────────────────────
    /// <summary>API / Publishable key. Used by: HitPay, PayMongo, Stripe.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Secret key. Used by: HitPay, PayMongo, Stripe, DragonPay.</summary>
    public string? SecretKey { get; set; }

    /// <summary>Webhook secret. Used by: HitPay, Stripe.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Client ID. Used by: PayPal.</summary>
    public string? ClientId { get; set; }

    /// <summary>Client secret. Used by: PayPal.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Merchant code. Used by: DragonPay.</summary>
    public string? MerchantCode { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
