using Microsoft.EntityFrameworkCore;
using Pulse.Data;
using Pulse.Models;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace Pulse.Services.Telegram;

/// <summary>
/// Handles the multi-step wizard that creates a <see cref="ScheduledTask"/> via Telegram.
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

        await bot.SendMessage(chatId,
            "Use /newtask to create a scheduled task.", cancellationToken: ct);
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
                state.Step         = WizardStep.AwaitingFrequencyType;
                await bot.SendMessage(chatId,
                    "🔁 How often should this run?\n`minutes` · `hours` · `days` · `weeks` · `months` · `once`",
                    parseMode: ParseMode.Markdown, cancellationToken: ct);
                break;

            case WizardStep.AwaitingFrequencyType:
                if (!TryParseFrequency(text, out var freq))
                {
                    await bot.SendMessage(chatId,
                        "⚠️ Reply with: `minutes`, `hours`, `days`, `weeks`, `months`, or `once`",
                        parseMode: ParseMode.Markdown, cancellationToken: ct);
                    return;
                }
                state.FrequencyType = freq;
                if (freq == FrequencyType.OneTime)
                {
                    state.Step = WizardStep.AwaitingScheduledAt;
                    await bot.SendMessage(chatId,
                        "📅 When should it run? (e.g. `2026-06-01 09:00` UTC)",
                        parseMode: ParseMode.Markdown, cancellationToken: ct);
                }
                else
                {
                    state.Step = WizardStep.AwaitingFrequencyValue;
                    await bot.SendMessage(chatId,
                        $"🔢 Every how many {freq.ToString().ToLower()}?",
                        cancellationToken: ct);
                }
                break;

            case WizardStep.AwaitingFrequencyValue:
                if (!int.TryParse(text, out var val) || val < 1)
                {
                    await bot.SendMessage(chatId,
                        "⚠️ Enter a whole number greater than 0.", cancellationToken: ct);
                    return;
                }
                state.FrequencyValue = val;
                state.Step           = WizardStep.AwaitingDeliveryType;
                await AskDeliveryAsync(bot, chatId, ct);
                break;

            case WizardStep.AwaitingScheduledAt:
                if (!DateTime.TryParse(text, out var scheduledAt))
                {
                    await bot.SendMessage(chatId,
                        "⚠️ Use format: `2026-06-01 09:00`",
                        parseMode: ParseMode.Markdown, cancellationToken: ct);
                    return;
                }
                state.ScheduledAt    = DateTime.SpecifyKind(scheduledAt, DateTimeKind.Utc);
                state.FrequencyValue = 1;
                state.Step           = WizardStep.AwaitingDeliveryType;
                await AskDeliveryAsync(bot, chatId, ct);
                break;

            case WizardStep.AwaitingDeliveryType:
                if (!TryParseDelivery(text, out var delivery))
                {
                    await bot.SendMessage(chatId,
                        "⚠️ Reply with: `dashboard`, `email`, `telegram`, `dashboard+email`, `dashboard+telegram`, `email+telegram`, or `all`",
                        parseMode: ParseMode.Markdown, cancellationToken: ct);
                    return;
                }
                state.DeliveryType = delivery;
                if (delivery is DeliveryType.Email or DeliveryType.DashboardEmail
                             or DeliveryType.EmailTelegram or DeliveryType.All)
                {
                    state.Step = WizardStep.AwaitingDeliveryEmail;
                    await bot.SendMessage(chatId,
                        "📧 Enter the delivery email address:", cancellationToken: ct);
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
                        "⚠️ Enter a valid email address.", cancellationToken: ct);
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
            ? $"once at {state.ScheduledAt:yyyy-MM-dd HH:mm} UTC"
            : $"every {state.FrequencyValue} {state.FrequencyType.ToString().ToLower()}";

        await bot.SendMessage(chatId,
            $"✅ *Task created!* (ID: {created.Id})\n\n" +
            $"*Title:* {created.Title}\n" +
            $"*Frequency:* {freqDesc}\n" +
            $"*Delivery:* {state.DeliveryType}",
            parseMode: ParseMode.Markdown, cancellationToken: ct);
    }

    private static Task AskDeliveryAsync(
        ITelegramBotClient bot, long chatId, CancellationToken ct)
        => bot.SendMessage(chatId,
            "📬 Where should results be delivered?\n" +
            "`dashboard` · `email` · `telegram`\n" +
            "`dashboard+email` · `dashboard+telegram` · `email+telegram` · `all`",
            parseMode: ParseMode.Markdown, cancellationToken: ct);

    private static bool TryParseFrequency(string text, out FrequencyType result)
    {
        result = text.ToLower().Trim() switch
        {
            "minutes" or "minute" => FrequencyType.Minutes,
            "hours"   or "hour"   => FrequencyType.Hours,
            "days"    or "day"    => FrequencyType.Days,
            "weeks"   or "week"   => FrequencyType.Weeks,
            "months"  or "month"  => FrequencyType.Months,
            "once"    or "onetime"=> FrequencyType.OneTime,
            _                     => (FrequencyType)(-1)
        };
        return (int)result >= 0;
    }

    private static bool TryParseDelivery(string text, out DeliveryType result)
    {
        result = text.ToLower().Trim() switch
        {
            "dashboard"                              => DeliveryType.Dashboard,
            "email"                                  => DeliveryType.Email,
            "telegram"                               => DeliveryType.Telegram,
            "dashboard+email" or "email+dashboard"   => DeliveryType.DashboardEmail,
            "dashboard+telegram"
                or "telegram+dashboard"              => DeliveryType.DashboardTelegram,
            "email+telegram" or "telegram+email"     => DeliveryType.EmailTelegram,
            "all"                                    => DeliveryType.All,
            _                                        => (DeliveryType)(-1)
        };
        return (int)result >= 0;
    }
}
