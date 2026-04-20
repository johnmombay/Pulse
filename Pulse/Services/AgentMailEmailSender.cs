using Microsoft.AspNetCore.Identity.UI.Services;

namespace Pulse.Services;

/// <summary>
/// Bridges ASP.NET Core Identity's <see cref="IEmailSender"/> to the AgentMail HTTP API.
/// Used by the sign-up confirmation flow (and any other Identity UI flow that depends
/// on <c>IEmailSender</c>) so that account-verification and password-reset emails are
/// actually delivered instead of being silently dropped by the default no-op sender.
/// </summary>
public sealed class AgentMailEmailSender(
    AgentMailService agentMail,
    GlobalAgentMailSettingsService settingsService,
    ILogger<AgentMailEmailSender> logger) : IEmailSender
{
    public async Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        var settings = await settingsService.GetAsync();

        if (!settings.IsEnabled
            || string.IsNullOrWhiteSpace(settings.ApiKey)
            || string.IsNullOrWhiteSpace(settings.DefaultInbox))
        {
            logger.LogWarning(
                "AgentMail is not configured \u2014 dropping outbound email to {Email} (subject: {Subject}).",
                email, subject);
            return;
        }

        try
        {
            // Strip tags for a best-effort plain-text fallback so clients that
            // refuse HTML (or block remote content) still see something useful.
            var plainText = System.Text.RegularExpressions.Regex
                .Replace(htmlMessage, "<.*?>", string.Empty);

            await agentMail.SendEmailAsync(
                inbox:   settings.DefaultInbox,
                to:      [email],
                subject: subject,
                text:    plainText,
                html:    htmlMessage);

            logger.LogInformation(
                "AgentMail: sent \"{Subject}\" to {Email}", subject, email);
        }
        catch (Exception ex)
        {
            // Swallow so callers (e.g. Identity sign-up) don't fail the user-facing
            // request just because email delivery is broken. Identity already handles
            // the "user can request another confirmation email" case.
            logger.LogError(ex,
                "AgentMail failed to send \"{Subject}\" to {Email}", subject, email);
        }
    }
}
