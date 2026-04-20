using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Pulse.Models;

namespace Pulse.Infrastructure;

/// <summary>
/// Extends the default Identity claims principal factory to add a <c>tid</c> claim
/// containing the user's TenantId (when set). This claim is read by <see cref="TenantContext"/>
/// to determine the current tenant on every request.
/// </summary>
public class ApplicationUserClaimsPrincipalFactory
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>
{
    public ApplicationUserClaimsPrincipalFactory(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IOptions<IdentityOptions> optionsAccessor)
        : base(userManager, roleManager, optionsAccessor)
    {
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        if (user.TenantId.HasValue)
        {
            identity.AddClaim(new Claim("tid", user.TenantId.Value.ToString()));
        }

        return identity;
    }
}
