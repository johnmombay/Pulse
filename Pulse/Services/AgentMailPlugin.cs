using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace Pulse.Services;

/// <summary>
/// Semantic Kernel plugin that gives the AI agent full email capabilities
/// through the AgentMail API (inbox: uspark@agentmail.to).
///
/// Exposed tools:
///   • email_list_threads   — list email threads
///   • email_get_thread     — read a full conversation thread
///   • email_list_messages  — list individual messages
///   • email_get_message    — read a single message with full body
///   • email_send           — compose and send a new email
///   • email_reply          — reply to an existing thread
/// </summary>
public sealed class AgentMailPlugin(
    AgentMailService agentMailService,
    GlobalAgentMailSettingsService agentMailSettings,
    ILogger<AgentMailPlugin> logger)
{
    private string DefaultInbox => agentMailSettings.GetAsync().GetAwaiter().GetResult().DefaultInbox;

    // ── Tool: email_list_threads ──────────────────────────────────────────────

    [KernelFunction("email_list_threads")]
    [Description(
        "Lists email threads (conversation chains) in the AgentMail inbox. " +
        "Returns subject, senders, recipients, preview, thread ID, and message count for each thread. " +
        "Use this to check received or sent emails, find a thread ID before reading or replying, " +
        "or give the user a summary of their inbox.")]
    public async Task<string> ListThreadsAsync(
        [Description("Maximum number of threads to return (1–50). Default is 10.")]
        int limit = 10,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);
        logger.LogInformation("email_list_threads inbox={Inbox} limit={Limit}", DefaultInbox, limit);
        try
        {
            var json = await agentMailService.ListThreadsAsync(DefaultInbox, limit, null, ct);
            return FormatThreadList(json);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "email_list_threads failed");
            return $"Failed to list email threads: {ex.Message}";
        }
    }

    // ── Tool: email_get_thread ────────────────────────────────────────────────

    [KernelFunction("email_get_thread")]
    [Description(
        "Reads the full email thread including every message and its complete body text. " +
        "Use the thread_id from email_list_threads. " +
        "Also returns the message_id of each message, which is needed to reply.")]
    public async Task<string> GetThreadAsync(
        [Description("The thread ID to retrieve, e.g. '17a8e7ad-37e4-44c3-93a7-b9736c230d91'.")]
        string threadId,
        CancellationToken ct = default)
    {
        logger.LogInformation("email_get_thread inbox={Inbox} threadId={ThreadId}", DefaultInbox, threadId);
        try
        {
            var json = await agentMailService.GetThreadAsync(DefaultInbox, threadId, ct);
            return FormatThread(json);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "email_get_thread failed");
            return $"Failed to get email thread: {ex.Message}";
        }
    }

    // ── Tool: email_list_messages ─────────────────────────────────────────────

    [KernelFunction("email_list_messages")]
    [Description(
        "Lists individual email messages in the AgentMail inbox ordered by most recent first. " +
        "Returns subject, from, to, preview, message ID, and thread ID for each message. " +
        "Use email_get_message to fetch the full body of a specific message.")]
    public async Task<string> ListMessagesAsync(
        [Description("Maximum number of messages to return (1–50). Default is 10.")]
        int limit = 10,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);
        logger.LogInformation("email_list_messages inbox={Inbox} limit={Limit}", DefaultInbox, limit);
        try
        {
            var json = await agentMailService.ListMessagesAsync(DefaultInbox, limit, null, ct);
            return FormatMessageList(json);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "email_list_messages failed");
            return $"Failed to list email messages: {ex.Message}";
        }
    }

    // ── Tool: email_get_message ───────────────────────────────────────────────

    [KernelFunction("email_get_message")]
    [Description(
        "Reads the complete content of a specific email message including the full body text. " +
        "Use the message_id from email_list_messages or email_get_thread. " +
        "Also returns the thread_id so you can call email_reply to respond.")]
    public async Task<string> GetMessageAsync(
        [Description("The message ID to retrieve, e.g. '<0100019d383c0c2d-7e1ec51f@email.amazonses.com>'.")]
        string messageId,
        CancellationToken ct = default)
    {
        logger.LogInformation("email_get_message inbox={Inbox} messageId={MessageId}", DefaultInbox, messageId);
        try
        {
            var json = await agentMailService.GetMessageAsync(DefaultInbox, messageId, ct);
            return FormatMessage(json, fullBody: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "email_get_message failed");
            return $"Failed to get email message: {ex.Message}";
        }
    }

    // ── Tool: email_send ──────────────────────────────────────────────────────

    [KernelFunction("email_send")]
    [Description(
        "Composes and sends a new email from the AgentMail inbox (uspark@agentmail.to). " +
        "Returns the message ID and thread ID of the sent email. " +
        "Use email_reply instead when you want to continue an existing conversation thread.")]
    public async Task<string> SendEmailAsync(
        [Description("Comma-separated list of recipient email addresses, e.g. 'alice@example.com,bob@company.com'.")]
        string to,
        [Description("The email subject line.")]
        string subject,
        [Description("The email body as plain text. Use \\n for line breaks.")]
        string body,
        [Description("Optional comma-separated CC email addresses.")]
        string cc = "",
        [Description("Optional comma-separated BCC email addresses.")]
        string bcc = "",
        CancellationToken ct = default)
    {
        var toArr  = SplitEmails(to);
        var ccArr  = SplitEmails(cc);
        var bccArr = SplitEmails(bcc);

        if (toArr.Length == 0)
            return "Error: at least one recipient (to) email address is required.";

        logger.LogInformation("email_send inbox={Inbox} to={To} subject={Subject}",
            DefaultInbox, string.Join(", ", toArr), subject);
        try
        {
            var result = await agentMailService.SendEmailAsync(
                DefaultInbox, toArr, subject, body,
                html:       null,
                cc:         ccArr.Length  > 0 ? ccArr  : null,
                bcc:        bccArr.Length > 0 ? bccArr : null,
                inReplyTo:  null,
                ct);
            return FormatSendResult(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "email_send failed");
            return $"Failed to send email: {ex.Message}";
        }
    }

    // ── Tool: email_reply ─────────────────────────────────────────────────────

    [KernelFunction("email_reply")]
    [Description(
        "Replies to an existing email by referencing the original message ID. " +
        "Call email_get_thread first to find the message_id to reply to. " +
        "The reply will be threaded in the same conversation. " +
        "Returns the message ID and thread ID of the sent reply.")]
    public async Task<string> ReplyToEmailAsync(
        [Description("The message_id of the email you are replying to, e.g. '<0100019d@email.amazonses.com>'. Use email_get_thread to find this.")]
        string inReplyToMessageId,
        [Description("Comma-separated recipient email addresses for the reply.")]
        string to,
        [Description("The reply subject line (typically 'Re: Original Subject').")]
        string subject,
        [Description("The reply body as plain text.")]
        string body,
        [Description("Optional comma-separated CC addresses.")]
        string cc = "",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(inReplyToMessageId))
            return "Error: inReplyToMessageId is required. Use email_get_thread to find the message ID.";

        var toArr = SplitEmails(to);
        var ccArr = SplitEmails(cc);

        if (toArr.Length == 0)
            return "Error: at least one recipient (to) email address is required.";

        logger.LogInformation("email_reply inbox={Inbox} inReplyTo={MsgId} to={To}",
            DefaultInbox, inReplyToMessageId, string.Join(", ", toArr));
        try
        {
            var result = await agentMailService.SendEmailAsync(
                DefaultInbox, toArr, subject, body,
                html:       null,
                cc:         ccArr.Length > 0 ? ccArr : null,
                bcc:        null,
                inReplyTo:  inReplyToMessageId,
                ct);
            return FormatSendResult(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "email_reply failed");
            return $"Failed to send reply: {ex.Message}";
        }
    }

    // ── Formatters ────────────────────────────────────────────────────────────

    private static string FormatThreadList(string json)
    {
        using var doc     = JsonDocument.Parse(json);
        var root          = doc.RootElement;
        var count         = root.TryGetProperty("count",   out var c) ? c.GetInt32() : 0;
        var threadsEl     = root.TryGetProperty("threads", out var t) ? t : default;

        if (count == 0 || threadsEl.ValueKind != JsonValueKind.Array)
            return "No email threads found in the inbox.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {count} thread(s) in {GetInboxId(root)}:");
        sb.AppendLine();

        int i = 1;
        foreach (var thread in threadsEl.EnumerateArray())
        {
            AppendProp(sb, $"[{i++}] Thread ID", thread, "thread_id");
            AppendProp(sb, "    Subject",         thread, "subject", "(no subject)");
            AppendJoinedProp(sb, "    From",      thread, "senders");
            AppendJoinedProp(sb, "    To",        thread, "recipients");
            AppendJoinedProp(sb, "    Labels",    thread, "labels");
            AppendProp(sb, "    Preview",         thread, "preview");
            AppendProp(sb, "    Date",            thread, "timestamp");
            if (thread.TryGetProperty("message_count", out var mc))
                sb.AppendLine($"    Messages: {mc.GetInt32()}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatThread(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root      = doc.RootElement;
        var sb        = new StringBuilder();

        AppendProp(sb, "Thread ID", root, "thread_id");
        AppendProp(sb, "Subject",   root, "subject", "(no subject)");
        AppendJoinedProp(sb, "Labels", root, "labels");
        sb.AppendLine();

        if (root.TryGetProperty("messages", out var msgs) &&
            msgs.ValueKind == JsonValueKind.Array)
        {
            var list = msgs.EnumerateArray().ToList();
            sb.AppendLine($"Messages ({list.Count}):");

            int i = 1;
            foreach (var msg in list)
            {
                sb.AppendLine($"--- Message {i++} ---");
                AppendProp(sb, "Message ID", msg, "message_id");
                AppendProp(sb, "From",        msg, "from");
                AppendJoinedProp(sb, "To",    msg, "to");
                AppendProp(sb, "Date",        msg, "timestamp");
                AppendJoinedProp(sb, "Labels", msg, "labels");

                var body = GetBody(msg);
                sb.AppendLine("Body:");
                sb.AppendLine(string.IsNullOrEmpty(body) ? "(empty)" : body);
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("(No messages found in thread)");
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatMessageList(string json)
    {
        using var doc  = JsonDocument.Parse(json);
        var root       = doc.RootElement;
        var count      = root.TryGetProperty("count",    out var c) ? c.GetInt32() : 0;
        var messagesEl = root.TryGetProperty("messages", out var m) ? m : default;

        if (count == 0 || messagesEl.ValueKind != JsonValueKind.Array)
            return "No messages found in the inbox.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {count} message(s) in {GetInboxId(root)}:");
        sb.AppendLine();

        int i = 1;
        foreach (var msg in messagesEl.EnumerateArray())
        {
            AppendProp(sb, $"[{i++}] Message ID", msg, "message_id");
            AppendProp(sb, "    Thread ID",         msg, "thread_id");
            AppendProp(sb, "    Subject",           msg, "subject", "(no subject)");
            AppendProp(sb, "    From",              msg, "from");
            AppendJoinedProp(sb, "    To",          msg, "to");
            AppendJoinedProp(sb, "    Labels",      msg, "labels");
            AppendProp(sb, "    Preview",           msg, "preview");
            AppendProp(sb, "    Date",              msg, "timestamp");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatMessage(string json, bool fullBody)
    {
        using var doc = JsonDocument.Parse(json);
        var root      = doc.RootElement;
        var sb        = new StringBuilder();

        AppendProp(sb, "Message ID", root, "message_id");
        AppendProp(sb, "Thread ID",  root, "thread_id");
        AppendProp(sb, "Subject",    root, "subject", "(no subject)");
        AppendProp(sb, "From",       root, "from");
        AppendJoinedProp(sb, "To",   root, "to");
        AppendProp(sb, "Date",       root, "timestamp");
        AppendJoinedProp(sb, "Labels", root, "labels");

        if (fullBody)
        {
            var body = GetBody(root);
            sb.AppendLine();
            sb.AppendLine("Body:");
            sb.AppendLine(string.IsNullOrEmpty(body) ? "(empty)" : body);
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatSendResult(string json)
    {
        using var doc  = JsonDocument.Parse(json);
        var root       = doc.RootElement;
        var msgId      = root.TryGetProperty("message_id", out var mid) ? mid.GetString() : "-";
        var threadId   = root.TryGetProperty("thread_id",  out var tid) ? tid.GetString() : "-";
        return $"Email sent successfully.\nMessage ID: {msgId}\nThread ID: {threadId}";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AppendProp(
        StringBuilder sb, string label, JsonElement el, string prop, string fallback = "")
    {
        if (el.TryGetProperty(prop, out var v) &&
            v.ValueKind != JsonValueKind.Null &&
            !string.IsNullOrEmpty(v.GetString()))
            sb.AppendLine($"{label}: {v.GetString()}");
        else if (!string.IsNullOrEmpty(fallback))
            sb.AppendLine($"{label}: {fallback}");
    }

    private static void AppendJoinedProp(
        StringBuilder sb, string label, JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;
        var values = arr.EnumerateArray()
            .Select(x => x.GetString())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
        if (values.Count > 0)
            sb.AppendLine($"{label}: {string.Join(", ", values)}");
    }

    private static string GetBody(JsonElement msg)
    {
        if (msg.TryGetProperty("text", out var txt) &&
            txt.ValueKind != JsonValueKind.Null &&
            !string.IsNullOrEmpty(txt.GetString()))
            return txt.GetString()!;

        if (msg.TryGetProperty("extracted_text", out var ext) &&
            ext.ValueKind != JsonValueKind.Null &&
            !string.IsNullOrEmpty(ext.GetString()))
            return ext.GetString()!;

        if (msg.TryGetProperty("preview", out var prev) &&
            prev.ValueKind != JsonValueKind.Null)
            return prev.GetString() ?? "";

        return "";
    }

    private static string GetInboxId(JsonElement root)
        => root.TryGetProperty("inbox_id", out var id) ? id.GetString() ?? "inbox" : "inbox";

    private static string[] SplitEmails(string? input)
        => (input ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => e.Contains('@'))
            .ToArray();
}
