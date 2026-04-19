#nullable disable

using Pulse.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Pulse.Areas.Identity.Pages.Account
{
    [AllowAnonymous]
    public class LockoutModel : PageModel
    {
        private readonly LlmSettingsService _settings;

        public LockoutModel(LlmSettingsService settings)
        {
            _settings = settings;
        }

        /// <summary>UTC datetime when the lockout expires (null if unknown).</summary>
        public DateTime? LockoutEnd   { get; private set; }

        /// <summary>Configured lockout duration in hours from live settings.</summary>
        public int       LockoutHours { get; private set; }

        public void OnGet()
        {
            LockoutHours = _settings.Get().Security.LoginLockoutHours;

            if (TempData["LockoutEnd"] is string endStr
                && DateTime.TryParse(endStr, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var end))
            {
                LockoutEnd = end;
            }

            if (TempData["LockoutHours"] is int hours)
                LockoutHours = hours;
        }
    }
}
