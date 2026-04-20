#nullable disable

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Pulse.Models;
using Pulse.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;

namespace Pulse.Areas.Identity.Pages.Account
{
    public class LoginModel : PageModel
    {
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly UserManager<ApplicationUser>  _userManager;
        private readonly LlmSettingsService            _settings;
        private readonly ILogger<LoginModel>           _logger;

        public LoginModel(
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser>  userManager,
            LlmSettingsService            settings,
            ILogger<LoginModel>           logger)
        {
            _signInManager = signInManager;
            _userManager   = userManager;
            _settings      = settings;
            _logger        = logger;
        }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [BindProperty]
        public InputModel Input { get; set; }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public IList<AuthenticationScheme> ExternalLogins { get; set; }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public string ReturnUrl { get; set; }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        [TempData]
        public string ErrorMessage { get; set; }

        /// <summary>
        ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public class InputModel
        {
            /// <summary>
            ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
            ///     directly from your code. This API may change or be removed in future releases.
            /// </summary>
            [Required]
            [EmailAddress]
            public string Email { get; set; }

            /// <summary>
            ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
            ///     directly from your code. This API may change or be removed in future releases.
            /// </summary>
            [Required]
            [DataType(DataType.Password)]
            public string Password { get; set; }

            /// <summary>
            ///     This API supports the ASP.NET Core Identity default UI infrastructure and is not intended to be used
            ///     directly from your code. This API may change or be removed in future releases.
            /// </summary>
            [Display(Name = "Remember me?")]
            public bool RememberMe { get; set; }
        }

        public async Task OnGetAsync(string returnUrl = null)
        {
            if (!string.IsNullOrEmpty(ErrorMessage))
            {
                ModelState.AddModelError(string.Empty, ErrorMessage);
            }

            returnUrl ??= Url.Content("~/");

            // Clear the existing external cookie to ensure a clean login process
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

            ExternalLogins = (await _signInManager.GetExternalAuthenticationSchemesAsync()).ToList();

            ReturnUrl = returnUrl;
        }

        public async Task<IActionResult> OnPostAsync(string returnUrl = null)
        {
            returnUrl ??= Url.Content("~/");

            ExternalLogins = (await _signInManager.GetExternalAuthenticationSchemesAsync()).ToList();

            if (!ModelState.IsValid)
                return Page();

            // TODO: fix pre-auth security settings for multi-tenancy
            var sec  = (await _settings.GetAsync(Guid.Empty)).Security;
            var user = await _userManager.FindByEmailAsync(Input.Email);

            // Check if already locked out before attempting sign-in
            if (user != null && await _userManager.IsLockedOutAsync(user))
            {
                _logger.LogWarning("Locked-out user {Email} attempted login.", Input.Email);
                var end = await _userManager.GetLockoutEndDateAsync(user);
                TempData["LockoutEnd"]   = end?.UtcDateTime.ToString("O");
                TempData["LockoutHours"] = sec.LoginLockoutHours;
                return RedirectToPage("./Lockout");
            }

            // lockoutOnFailure: false — we manage the failure count ourselves
            var result = await _signInManager.PasswordSignInAsync(
                Input.Email, Input.Password, Input.RememberMe, lockoutOnFailure: false);

            if (result.Succeeded)
            {
                _logger.LogInformation("User {Email} logged in.", Input.Email);
                // Reset failed count on successful login
                if (user != null) await _userManager.ResetAccessFailedCountAsync(user);
                return LocalRedirect(returnUrl);
            }

            if (result.RequiresTwoFactor)
                return RedirectToPage("./LoginWith2fa",
                    new { ReturnUrl = returnUrl, RememberMe = Input.RememberMe });

            // Failed password — increment and check live threshold
            if (user != null)
            {
                await _userManager.AccessFailedAsync(user);
                var failCount = await _userManager.GetAccessFailedCountAsync(user);
                var remaining = sec.MaxFailedLoginAttempts - failCount;

                if (remaining <= 0)
                {
                    var lockEnd = DateTimeOffset.UtcNow.AddHours(sec.LoginLockoutHours);
                    await _userManager.SetLockoutEndDateAsync(user, lockEnd);
                    await _userManager.ResetAccessFailedCountAsync(user);
                    _logger.LogWarning(
                        "User {Email} locked out for {Hours}h after {Max} failed attempt(s).",
                        Input.Email, sec.LoginLockoutHours, sec.MaxFailedLoginAttempts);
                    TempData["LockoutEnd"]   = lockEnd.UtcDateTime.ToString("O");
                    TempData["LockoutHours"] = sec.LoginLockoutHours;
                    return RedirectToPage("./Lockout");
                }

                var attemptWord = remaining == 1 ? "attempt" : "attempts";
                ModelState.AddModelError(string.Empty,
                    $"Invalid login attempt. {remaining} {attemptWord} remaining before your account is locked.");
            }
            else
            {
                ModelState.AddModelError(string.Empty, "Invalid login attempt.");
            }

            return Page();
        }
    }
}
