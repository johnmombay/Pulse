#nullable disable

using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using Pulse.Models;
using Pulse.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;

namespace Pulse.Areas.Identity.Pages.Account
{
    public class ForgotPasswordModel : PageModel
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IEmailSender                 _emailSender;
        private readonly LlmSettingsService           _settings;
        private readonly ILogger<ForgotPasswordModel> _logger;

        public ForgotPasswordModel(
            UserManager<ApplicationUser> userManager,
            IEmailSender                 emailSender,
            LlmSettingsService           settings,
            ILogger<ForgotPasswordModel> logger)
        {
            _userManager = userManager;
            _emailSender = emailSender;
            _settings    = settings;
            _logger      = logger;
        }

        [BindProperty]
        public InputModel Input { get; set; }

        public class InputModel
        {
            [Required]
            [EmailAddress]
            public string Email { get; set; }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!ModelState.IsValid)
                return Page();

            var user = await _userManager.FindByEmailAsync(Input.Email);
            if (user == null || !(await _userManager.IsEmailConfirmedAsync(user)))
            {
                // Don't reveal whether the user exists or is confirmed
                return RedirectToPage("./ForgotPasswordConfirmation");
            }

            var code = await _userManager.GeneratePasswordResetTokenAsync(user);
            code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
            var callbackUrl = Url.Page(
                "/Account/ResetPassword",
                pageHandler: null,
                values: new { area = "Identity", code },
                protocol: Request.Scheme);

            var encodedUrl = HtmlEncoder.Default.Encode(callbackUrl);
            var firstName  = !string.IsNullOrWhiteSpace(user.FirstName) ? user.FirstName : Input.Email;

            // Pull tenant-scoped branding (AppName) for the email subject / body.
            var tenantId = user.TenantId ?? Guid.Empty;
            var appName  = (await _settings.GetAsync(tenantId)).AppName ?? "Pulse";

            try
            {
                await _emailSender.SendEmailAsync(
                    Input.Email,
                    $"Reset your {appName} password",
                    BuildResetEmailHtml(firstName, encodedUrl, appName));

                _logger.LogInformation(
                    "Password reset email queued for {Email}", Input.Email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to send password reset email to {Email}", Input.Email);
            }

            return RedirectToPage("./ForgotPasswordConfirmation");
        }

        private static string BuildResetEmailHtml(string firstName, string encodedUrl, string appName) => $"""
            <!DOCTYPE html>
            <html lang="en">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>
            <body style="margin:0;padding:0;background:#f4f6fb;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif">
              <table width="100%" cellpadding="0" cellspacing="0" style="padding:40px 16px">
                <tr><td align="center">
                  <table width="100%" cellpadding="0" cellspacing="0" style="max-width:520px;background:#fff;border-radius:12px;border:1px solid #e5e7eb;box-shadow:0 1px 6px rgba(0,0,0,.06)">
                    <tr>
                      <td style="padding:36px 40px 28px">

                        <div style="margin-bottom:32px">
                          <svg width="28" height="28" viewBox="0 0 24 24" fill="none">
                            <rect x="2"  y="14" width="4"  height="8"  rx="1" fill="#2563eb" opacity=".7"/>
                            <rect x="8"  y="9"  width="4"  height="13" rx="1" fill="#2563eb" opacity=".85"/>
                            <rect x="14" y="4"  width="4"  height="18" rx="1" fill="#2563eb"/>
                          </svg>
                          <span style="font-size:1.1rem;font-weight:700;color:#111827;vertical-align:middle;margin-left:8px">{appName}</span>
                        </div>

                        <div style="width:48px;height:48px;background:#eff6ff;border-radius:10px;display:inline-flex;align-items:center;justify-content:center;margin-bottom:20px">
                          <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="#2563eb" stroke-width="2" stroke-linecap="round">
                            <rect x="3" y="11" width="18" height="11" rx="2"/>
                            <path d="M7 11V7a5 5 0 0 1 10 0v4"/>
                          </svg>
                        </div>

                        <h1 style="font-size:1.35rem;font-weight:700;color:#111827;margin:0 0 8px">Reset your password</h1>
                        <p style="font-size:.9rem;color:#6b7280;margin:0 0 28px;line-height:1.6">
                          Hi {firstName}, we received a request to reset the password for your {appName} account.
                          Click the button below to set a new password.
                        </p>

                        <table cellpadding="0" cellspacing="0" style="margin-bottom:28px">
                          <tr>
                            <td style="background:#2563eb;border-radius:8px">
                              <a href="{encodedUrl}"
                                 style="display:inline-block;padding:13px 32px;color:#fff;font-size:.925rem;font-weight:600;text-decoration:none;border-radius:8px">
                                Reset Password
                              </a>
                            </td>
                          </tr>
                        </table>

                        <p style="font-size:.82rem;color:#9ca3af;margin:0 0 4px">Button not working? Copy this link into your browser:</p>
                        <p style="font-size:.78rem;color:#2563eb;word-break:break-all;margin:0 0 28px">
                          <a href="{encodedUrl}" style="color:#2563eb">{encodedUrl}</a>
                        </p>

                        <hr style="border:none;border-top:1px solid #f3f4f6;margin:0 0 20px" />
                        <p style="font-size:.78rem;color:#9ca3af;margin:0">
                          If you didn&rsquo;t request a password reset, you can safely ignore this email.
                          Your password will not change.
                        </p>

                      </td>
                    </tr>
                  </table>
                </td></tr>
              </table>
            </body>
            </html>
            """;
    }
}
