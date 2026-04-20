using Microsoft.AspNetCore.Identity;
using Pulse.Data.Entities;

namespace Pulse.Models;

public class ApplicationUser : IdentityUser
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName  { get; set; } = string.Empty;

    // Multi-tenancy — null for SuperAdmin accounts
    public Guid?    TenantId { get; set; }
    public Tenant?  Tenant   { get; set; }
}