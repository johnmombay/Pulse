using Pulse.Models;

namespace Pulse.Data.Entities;

public class InAppNotification
{
    public int     Id        { get; set; }
    public string  UserId    { get; set; } = string.Empty;
    public string  Title     { get; set; } = string.Empty;
    public string  Body      { get; set; } = string.Empty;
    public bool    IsRead    { get; set; } = false;
    public string? ActionUrl { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // Navigation
    public ApplicationUser User { get; set; } = null!;
}
