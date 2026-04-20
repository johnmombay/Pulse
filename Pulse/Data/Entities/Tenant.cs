namespace Pulse.Data.Entities;

public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>URL-safe slug, reserved for future subdomain use.</summary>
    public string Slug { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public bool IsActive { get; set; } = true;

    // Navigation: ICollection<ApplicationUser> Users — wired in Task 2
}
