using Microsoft.EntityFrameworkCore;
using Pulse.Data;
using Pulse.Infrastructure;
using Pulse.Models;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace Pulse.Services.Telegram;

/// <summary>
/// Handles the multi-step wizard that creates a <see cref="ScheduledTask"/> via Telegram.
/// Non-wizard messages are routed to <see cref="AgentOrchestrationService"/> (AI chat domain).
/// One instance is shared (Singleton) — state is keyed by Telegram Chat ID.
/// </summary>
public sealed class TelegramConversationService(IServiceScopeFactory scopeFactory)
{
    private readonly Dictionary<long, TelegramConversationState> _sessions = [];

    public async Task HandleMessageAsync(
        ITelegramBotClient bot, long chatId,
        string userId, Guid tenantId, string text, CancellationToken ct)
    {
        if (text.Equals("/newtask", StringComparison.OrdinalIgnoreCase))
        {
            _sessions[chatId] = new TelegramConversationState
            {
                Step     = WizardStep.AwaitingTitle,
                UserId   = userId,
                TenantId = tenantId,
            };
            await bot.SendMessage(chatId,
                "📝 *New Scheduled Task*\n\nWhat's the task title? (or /cancel)",
                parseMode: ParseMode.Markdown, cancellationToken: ct);
            return;
        }

        if (text.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
        {
            _sessions.Remove(chatId);
            await bot.SendMessage(chatId, "❌ Cancelled.", cancellationToken: ct);
            return;
        }

        if (_sessions.TryGetValue(chatId, out var state))
        {
            using var scope   = scopeFactory.CreateScope();
            var scheduler     = scope.ServiceProvider.GetRequiredService<SchedulerService>();
            await ProcessWizardAsync(bot, chatId, text, state, scheduler, ct);
            return;
        }

        // Default: route to AI chat agent
        await HandleChatAsync(bot, chatId, userId, tenantId, text, ct);
    }

    private async Task HandleChatAsync(
        ITelegramBotClient bot, long chatId,
        string userId, Guid tenantId, string text, CancellationToken ct)
    {
        using var scope        = scopeFactory.CreateScope();
        var tenantContext      = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var orchestration      = scope.ServiceProvider.GetRequiredService<AgentOrchestrationService>();

        tenantContext.SetTenantId(tenantId);

        var sessionId = $"telegram:{chatId}";

        try
        {
            var response = await orchestration.ExecuteAsync(sessionId, text, userId, ct);
            if (!string.IsNullOrWhiteSpace(response))
                await bot.SendMessage(chatId, response,
                    parseMode: ParseMode.Markdown, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            await bot.SendMessage(chatId,
                $"⚠️ Agent error: {ex.Message}", cancellationToken: ct);
        }
    }

    private async Task ProcessWizardAsync(
        ITelegramBotClient bot, long chatId, string text,
        TelegramConversationState state, SchedulerService scheduler, CancellationToken ct)
    {
        switch (state.Step)
        {
            case WizardStep.AwaitingTitle:
                state.Title = text;
                state.Step  = WizardStep.AwaitingInstructions;
                await bot.SendMessage(chatId,
                    "📋 What should the agent do? _(Enter instructions)_",
                    parseMode: ParseMode.Markdown, cancellationToken: ct);
                break;

            case WizardStep.AwaitingInstructions:
                state.Instructions = text;
                state.Step         = WizardStep.AwaitingFrequency;
                await bot.SendMessage(chatId,
                    "🔁 How often should this run?\n\n" +
                    "Just describe it naturally — for example:\n" +
                    "• _every day_\n• _every 3 hours_\n• _weekly_\n• _once_",
                    parseMode: ParseMode.Markdown, cancellationToken: ct);
                break;

            case WizardStep.AwaitingFrequency:
            case WizardStep.AwaitingFrequencyType: // in-flight compat
            case WizardStep.AwaitingFrequencyValue:
                if (!TryParseNaturalFrequency(text, out var freq, out var freqVal))
                {
                    await bot.SendMessage(chatId,
                        "⚠️ I didn't catch that. Try something like:\n" +
                        "_every day_, _every 6 hours_, _weekly_, _once_",
                        parseMode: ParseMode.Markdown, cancellationToken: ct);
                    return;
                }
                state.FrequencyType  = freq;
                state.FrequencyValue = freqVal;
                state.Step           = freq == FrequencyType.OneTime
                    ? WizardStep.AwaitingScheduledAt
                    : WizardStep.AwaitingDeliveryType;

                if (freq == FrequencyType.OneTime)
                    await bot.SendMessage(chatId,
                        "📅 When should it run?\n\n" +
                        "Just say it naturally — for example:\n" +
                        "• _tomorrow at 9am_\n• _next Monday_\n• _in 2 hours_\n• _June 1 at noon_",
                        parseMode: ParseMode.Markdown, cancellationToken: ct);
                else
                    await AskDeliveryAsync(bot, chatId, ct);
                break;

            case WizardStep.AwaitingScheduledAt:
                if (!TryParseNaturalDateTime(text, out var scheduledAt))
                {
                    await bot.SendMessage(chatId,
                        "⚠️ Couldn't parse that time. Try:\n" +
                        "_tomorrow at 9am_, _next Monday at noon_, _in 2 hours_, _June 1 9:00_",
                        parseMode: ParseMode.Markdown, cancellationToken: ct);
                    return;
                }
                state.ScheduledAt = scheduledAt;
                state.Step        = WizardStep.AwaitingDeliveryType;
                await AskDeliveryAsync(bot, chatId, ct);
                break;

            case WizardStep.AwaitingDeliveryType:
                if (!TryParseNaturalDelivery(text, out var delivery))
                {
                    await bot.SendMessage(chatId,
                        "⚠️ I didn't catch that. Try:\n" +
                        "_dashboard_, _email_, _telegram_, _dashboard and email_, _everything_",
                        parseMode: ParseMode.Markdown, cancellationToken: ct);
                    return;
                }
                state.DeliveryType = delivery;
                if (delivery is DeliveryType.Email or DeliveryType.DashboardEmail
                             or DeliveryType.EmailTelegram or DeliveryType.All)
                {
                    state.Step = WizardStep.AwaitingDeliveryEmail;
                    await bot.SendMessage(chatId,
                        "📧 What email address should results be sent to?",
                        cancellationToken: ct);
                }
                else
                {
                    await CreateTaskAsync(bot, chatId, state, scheduler, ct);
                }
                break;

            case WizardStep.AwaitingDeliveryEmail:
                if (!text.Contains('@'))
                {
                    await bot.SendMessage(chatId,
                        "⚠️ That doesn't look like a valid email address. Try again.",
                        cancellationToken: ct);
                    return;
                }
                state.DeliveryEmail = text;
                await CreateTaskAsync(bot, chatId, state, scheduler, ct);
                break;
        }
    }

    private async Task CreateTaskAsync(
        ITelegramBotClient bot, long chatId,
        TelegramConversationState state, SchedulerService scheduler, CancellationToken ct)
    {
        var task = new ScheduledTask
        {
            UserId         = state.UserId,
            TenantId       = state.TenantId,
            Title          = state.Title,
            Instructions   = state.Instructions,
            FrequencyType  = state.FrequencyType,
            FrequencyValue = state.FrequencyValue,
            ScheduledAt    = state.ScheduledAt,
            DeliveryType   = state.DeliveryType,
            DeliveryEmail  = state.DeliveryEmail,
            IsEnabled      = true,
        };

        var created  = await scheduler.CreateAsync(task, ct);
        _sessions.Remove(chatId);

        var freqDesc = state.FrequencyType == FrequencyType.OneTime
            ? $"once at {state.ScheduledAt:MMM d, yyyy HH:mm} UTC"
            : FormatFrequencyDesc(state.FrequencyType, state.FrequencyValue);

        await bot.SendMessage(chatId,
            $"✅ *Task created!* (ID: {created.Id})\n\n" +
            $"*Title:* {created.Title}\n" +
            $"*Frequency:* {freqDesc}\n" +
            $"*Delivery:* {DescribeDelivery(state.DeliveryType)}",
            parseMode: ParseMode.Markdown, cancellationToken: ct);
    }

    private static Task AskDeliveryAsync(ITelegramBotClient bot, long chatId, CancellationToken ct)
        => bot.SendMessage(chatId,
            "📬 Where should results be delivered?\n\n" +
            "Just say it naturally — for example:\n" +
            "• _dashboard_\n• _email_\n• _telegram_\n" +
            "• _dashboard and email_\n• _dashboard and telegram_\n" +
            "• _email and telegram_\n• _everything_",
            parseMode: ParseMode.Markdown, cancellationToken: ct);

    // ── Natural language parsers ──────────────────────────────────────────────

    /// <summary>Parses "every 3 days", "daily", "weekly", "once", etc.</summary>
    private static bool TryParseNaturalFrequency(string text, out FrequencyType type, out int value)
    {
        type  = FrequencyType.Days;
        value = 1;
        var s = text.Trim().ToLowerInvariant();

        // one-time / once
        if (s is "once" or "one time" or "onetime" or "one-time")
        {
            type = FrequencyType.OneTime;
            return true;
        }

        // shorthand: daily/weekly/monthly/hourly
        if (s is "daily" or "every day" or "each day") { type = FrequencyType.Days;  return true; }
        if (s is "weekly" or "every week" or "each week") { type = FrequencyType.Weeks; return true; }
        if (s is "monthly" or "every month" or "each month") { type = FrequencyType.Months; return true; }
        if (s is "hourly" or "every hour" or "each hour") { type = FrequencyType.Hours; return true; }

        // "every N unit[s]" or "each N unit[s]"
        var m = System.Text.RegularExpressions.Regex.Match(s,
            @"^(?:every|each)\s+(\d+)\s+(minute|minutes|hour|hours|day|days|week|weeks|month|months)$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out value) && value >= 1)
        {
            type = m.Groups[2].Value.TrimEnd('s') switch
            {
                "minute" => FrequencyType.Minutes,
                "hour"   => FrequencyType.Hours,
                "day"    => FrequencyType.Days,
                "week"   => FrequencyType.Weeks,
                "month"  => FrequencyType.Months,
                _        => FrequencyType.Days
            };
            return true;
        }

        // "every N minutes" without leading "every" already matched, try bare number + unit
        var m2 = System.Text.RegularExpressions.Regex.Match(s,
            @"^(\d+)\s+(minute|minutes|hour|hours|day|days|week|weeks|month|months)$");
        if (m2.Success && int.TryParse(m2.Groups[1].Value, out value) && value >= 1)
        {
            type = m2.Groups[2].Value.TrimEnd('s') switch
            {
                "minute" => FrequencyType.Minutes,
                "hour"   => FrequencyType.Hours,
                "day"    => FrequencyType.Days,
                "week"   => FrequencyType.Weeks,
                "month"  => FrequencyType.Months,
                _        => FrequencyType.Days
            };
            return true;
        }

        return false;
    }

    /// <summary>Parses natural language datetimes: "tomorrow at 9am", "next Monday", "in 2 hours", etc.</summary>
    private static bool TryParseNaturalDateTime(string text, out DateTime result)
    {
        result = default;
        var s   = text.Trim().ToLowerInvariant();
        var now = DateTime.UtcNow;

        // "in N minutes/hours/days"
        var mIn = System.Text.RegularExpressions.Regex.Match(s,
            @"^in\s+(\d+)\s+(minute|minutes|hour|hours|day|days)$");
        if (mIn.Success && int.TryParse(mIn.Groups[1].Value, out var inAmt))
        {
            result = mIn.Groups[2].Value.TrimEnd('s') switch
            {
                "minute" => now.AddMinutes(inAmt),
                "hour"   => now.AddHours(inAmt),
                "day"    => now.AddDays(inAmt),
                _        => now.AddHours(inAmt)
            };
            return true;
        }

        // "tomorrow [at TIME]"
        if (s.StartsWith("tomorrow"))
        {
            var base_ = now.Date.AddDays(1);
            result = TryExtractTime(s.Replace("tomorrow", "").Trim(), base_, out var dt) ? dt : base_.AddHours(9);
            return true;
        }

        // "today [at TIME]"
        if (s.StartsWith("today"))
        {
            var base_ = now.Date;
            result = TryExtractTime(s.Replace("today", "").Trim(), base_, out var dt) ? dt : base_.AddHours(9);
            return true;
        }

        // "next Monday/Tuesday/…"
        var mNext = System.Text.RegularExpressions.Regex.Match(s, @"^next\s+(monday|tuesday|wednesday|thursday|friday|saturday|sunday)(.*)$");
        if (mNext.Success)
        {
            var target = ParseDayOfWeek(mNext.Groups[1].Value);
            var daysAhead = ((int)target - (int)now.DayOfWeek + 7) % 7;
            if (daysAhead == 0) daysAhead = 7;
            var base_ = now.Date.AddDays(daysAhead);
            var rest  = mNext.Groups[2].Value.Trim();
            result = TryExtractTime(rest, base_, out var dt) ? dt : base_.AddHours(9);
            return true;
        }

        // fall back to standard DateTime.TryParse (handles "June 1 9:00", "2026-06-01 09:00", etc.)
        if (DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
        {
            result = parsed.ToUniversalTime();
            return true;
        }

        return false;
    }

    private static bool TryExtractTime(string s, DateTime baseDate, out DateTime result)
    {
        result = default;
        s = s.TrimStart('a', 't', ' '); // strip leading "at "
        s = s.Trim();

        if (string.IsNullOrEmpty(s)) return false;

        // "noon" / "midnight"
        if (s == "noon")    { result = baseDate.AddHours(12); return true; }
        if (s == "midnight"){ result = baseDate; return true; }

        // "9am", "9:30am", "14:00"
        var mTime = System.Text.RegularExpressions.Regex.Match(s,
            @"^(\d{1,2})(?::(\d{2}))?\s*(am|pm)?$");
        if (mTime.Success)
        {
            int h = int.Parse(mTime.Groups[1].Value);
            int mi = mTime.Groups[2].Success ? int.Parse(mTime.Groups[2].Value) : 0;
            var ampm = mTime.Groups[3].Value;
            if (ampm == "pm" && h < 12) h += 12;
            if (ampm == "am" && h == 12) h = 0;
            result = baseDate.AddHours(h).AddMinutes(mi);
            return true;
        }

        return false;
    }

    private static DayOfWeek ParseDayOfWeek(string s) => s switch
    {
        "monday"    => DayOfWeek.Monday,
        "tuesday"   => DayOfWeek.Tuesday,
        "wednesday" => DayOfWeek.Wednesday,
        "thursday"  => DayOfWeek.Thursday,
        "friday"    => DayOfWeek.Friday,
        "saturday"  => DayOfWeek.Saturday,
        "sunday"    => DayOfWeek.Sunday,
        _           => DayOfWeek.Monday
    };

    /// <summary>Parses natural delivery: "email", "dashboard and telegram", "everything", etc.</summary>
    private static bool TryParseNaturalDelivery(string text, out DeliveryType result)
    {
        result = DeliveryType.Dashboard;
        var s = text.Trim().ToLowerInvariant();

        bool hasDash     = s.Contains("dashboard") || s.Contains("dash");
        bool hasEmail    = s.Contains("email");
        bool hasTelegram = s.Contains("telegram") || s.Contains("tg");

        if (s is "all" or "everything" or "all channels") { result = DeliveryType.All; return true; }

        if (hasDash && hasEmail && hasTelegram) { result = DeliveryType.All;               return true; }
        if (hasDash && hasEmail)               { result = DeliveryType.DashboardEmail;     return true; }
        if (hasDash && hasTelegram)            { result = DeliveryType.DashboardTelegram;  return true; }
        if (hasEmail && hasTelegram)           { result = DeliveryType.EmailTelegram;      return true; }
        if (hasDash)     { result = DeliveryType.Dashboard; return true; }
        if (hasEmail)    { result = DeliveryType.Email;     return true; }
        if (hasTelegram) { result = DeliveryType.Telegram;  return true; }

        return false;
    }

    private static string FormatFrequencyDesc(FrequencyType type, int value) => type switch
    {
        FrequencyType.Minutes => value == 1 ? "every minute"  : $"every {value} minutes",
        FrequencyType.Hours   => value == 1 ? "every hour"    : $"every {value} hours",
        FrequencyType.Days    => value == 1 ? "daily"         : $"every {value} days",
        FrequencyType.Weeks   => value == 1 ? "weekly"        : $"every {value} weeks",
        FrequencyType.Months  => value == 1 ? "monthly"       : $"every {value} months",
        _                     => type.ToString()
    };

    private static string DescribeDelivery(DeliveryType type) => type switch
    {
        DeliveryType.Dashboard         => "Dashboard",
        DeliveryType.Email             => "Email",
        DeliveryType.Telegram          => "Telegram",
        DeliveryType.Both              => "Dashboard + Email",
        DeliveryType.DashboardEmail    => "Dashboard + Email",
        DeliveryType.DashboardTelegram => "Dashboard + Telegram",
        DeliveryType.EmailTelegram     => "Email + Telegram",
        DeliveryType.All               => "Dashboard + Email + Telegram",
        _                              => type.ToString()
    };
}


