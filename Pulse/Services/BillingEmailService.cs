using Microsoft.AspNetCore.Identity;
using Pulse.Data.Entities;
using Pulse.Models;

namespace Pulse.Services;

/// <summary>
/// Sends billing-related emails (invoice delivery, gateway-not-configured alerts)
/// via the existing <see cref="AgentMailService"/> / <see cref="AgentMailEmailSender"/> stack.
/// </summary>
public sealed class BillingEmailService(
    AgentMailService agentMail,
    GlobalAgentMailSettingsService settingsService,
    UserManager<ApplicationUser> userManager,
    ILogger<BillingEmailService> logger)
{
    // ── Invoice email ─────────────────────────────────────────────────────────

    public async Task SendInvoiceEmailAsync(Invoice invoice, string toEmail, string tenantName, CancellationToken ct = default)
    {
        var settings = await settingsService.GetAsync();
        if (!settings.IsEnabled || string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            logger.LogWarning("AgentMail not configured — skipping invoice email for {Email}", toEmail);
            return;
        }

        var subject = $"[Pulse] Invoice {invoice.Number} — {StatusLabel(invoice.Status)}";
        var html    = BuildInvoiceHtml(invoice, tenantName);
        var text    = BuildInvoiceText(invoice, tenantName);

        try
        {
            await agentMail.SendEmailAsync(
                inbox:   settings.DefaultInbox,
                to:      [toEmail],
                subject: subject,
                text:    text,
                html:    html,
                ct:      ct);

            logger.LogInformation("Invoice email {Number} sent to {Email}", invoice.Number, toEmail);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send invoice email {Number} to {Email}", invoice.Number, toEmail);
        }
    }

    // ── Gateway-not-configured alert ──────────────────────────────────────────

    public async Task SendGatewayUnconfiguredAlertAsync(string tenantName, Guid tenantId, CancellationToken ct = default)
    {
        var settings = await settingsService.GetAsync();
        if (!settings.IsEnabled || string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            logger.LogWarning("AgentMail not configured — skipping gateway alert for superadmins");
            return;
        }

        var admins = await userManager.GetUsersInRoleAsync("SuperAdmin");
        foreach (var admin in admins)
        {
            if (string.IsNullOrWhiteSpace(admin.Email)) continue;
            try
            {
                var subject = $"[Pulse] ⚠️ Payment gateway not configured — tenant '{tenantName}' could not be activated";
                var html    = $"""
                    <p>Hi {admin.FirstName},</p>
                    <p>Tenant <strong>{tenantName}</strong> (ID: {tenantId}) attempted to subscribe but <strong>no payment gateway is active</strong>.</p>
                    <p>Please configure an active payment gateway in the admin panel so new tenants can complete checkout.</p>
                    <p><a href="/Admin/PaymentGateways">→ Configure Payment Gateway</a></p>
                    <p style="color:#888;font-size:.85em">Pulse Platform</p>
                    """;
                var text = $"Tenant '{tenantName}' (ID: {tenantId}) could not be activated — no payment gateway configured. Visit /Admin/PaymentGateways.";

                await agentMail.SendEmailAsync(
                    inbox:   settings.DefaultInbox,
                    to:      [admin.Email],
                    subject: subject,
                    text:    text,
                    html:    html,
                    ct:      ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send gateway alert to superadmin {Email}", admin.Email);
            }
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string StatusLabel(InvoiceStatus s) => s switch
    {
        InvoiceStatus.Paid   => "Paid",
        InvoiceStatus.Unpaid => "Payment Due",
        InvoiceStatus.Void   => "Void",
        _                    => s.ToString(),
    };

    private static string BuildInvoiceHtml(Invoice inv, string tenantName) => $"""
        <div style="font-family:sans-serif;max-width:600px;margin:0 auto">
          <h2 style="color:#2563eb">Invoice {inv.Number}</h2>
          <p><strong>Tenant:</strong> {tenantName}</p>
          <p><strong>Status:</strong> <span style="color:{(inv.Status == InvoiceStatus.Paid ? "#16a34a" : "#dc2626")}">{StatusLabel(inv.Status)}</span></p>
          <table style="width:100%;border-collapse:collapse;margin:1rem 0">
            <tr style="background:#f3f4f6">
              <th style="padding:.5rem;text-align:left">Period</th>
              <th style="padding:.5rem;text-align:left">Billing Cycle</th>
              <th style="padding:.5rem;text-align:right">Amount</th>
            </tr>
            <tr>
              <td style="padding:.5rem">{inv.PeriodStart:MMM d, yyyy} – {inv.PeriodEnd:MMM d, yyyy}</td>
              <td style="padding:.5rem">{inv.BillingCycle}</td>
              <td style="padding:.5rem;text-align:right"><strong>${inv.Amount:0.00}</strong></td>
            </tr>
          </table>
          {(inv.Status == InvoiceStatus.Unpaid ? $"<p><strong>Due date:</strong> {inv.DueDate:MMM d, yyyy}</p><p><a href=\"/Billing/Invoices/{inv.Id}/Pay\" style=\"background:#2563eb;color:#fff;padding:.5rem 1.25rem;border-radius:.4rem;text-decoration:none\">Pay Now</a></p>" : "")}
          <p style="color:#888;font-size:.8em;margin-top:2rem">Pulse Platform · Invoice {inv.Number} · Issued {inv.IssuedUtc:MMM d, yyyy}</p>
        </div>
        """;

    private static string BuildInvoiceText(Invoice inv, string tenantName) =>
        $"Invoice {inv.Number}\nTenant: {tenantName}\nStatus: {StatusLabel(inv.Status)}\n" +
        $"Period: {inv.PeriodStart:MMM d, yyyy} – {inv.PeriodEnd:MMM d, yyyy}\n" +
        $"Amount: ${inv.Amount:0.00}\n" +
        (inv.Status == InvoiceStatus.Unpaid ? $"Due: {inv.DueDate:MMM d, yyyy}\nPay at: /Billing/Invoices/{inv.Id}/Pay\n" : "");
}
