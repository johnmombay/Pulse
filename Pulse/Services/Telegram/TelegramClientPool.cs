using Hangfire;
using Pulse.Jobs;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Pulse.Services.Telegram;

/// <summary>
/// Manages one <see cref="TelegramBotClient"/> per user bot token.
/// Clients are started on-demand and stopped when a token is revoked.
/// </summary>
public sealed class TelegramClientPool(
    IServiceScopeFactory scopeFactory,
    ILogger<TelegramClientPool> logger) : IAsyncDisposable
{
    // key = botToken
    private readonly Dictionary<string, (TelegramBotClient Client, CancellationTokenSource Cts)> _clients = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task EnsureStartedAsync(string botToken, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_clients.ContainsKey(botToken)) return;

            var cts    = new CancellationTokenSource();
            var client = new TelegramBotClient(botToken);

            client.StartReceiving(
                updateHandler: (bot, update, innerCt) =>
                    HandleUpdateAsync(bot, botToken, update, innerCt),
                errorHandler: (_, ex, _, _) =>
                {
                    logger.LogError(ex, "Telegram error [{Token}]", MaskToken(botToken));
                    return Task.CompletedTask;
                },
                receiverOptions: new ReceiverOptions
                {
                    AllowedUpdates = [UpdateType.Message]
                },
                cancellationToken: cts.Token);

            _clients[botToken] = (client, cts);
            logger.LogInformation("Telegram client started for token {Token}", MaskToken(botToken));
        }
        finally { _lock.Release(); }
    }

    public async Task StopAsync(string botToken)
    {
        await _lock.WaitAsync();
        try
        {
            if (!_clients.TryGetValue(botToken, out var entry)) return;
            await entry.Cts.CancelAsync();
            entry.Cts.Dispose();
            _clients.Remove(botToken);
            logger.LogInformation("Telegram client stopped for token {Token}", MaskToken(botToken));
        }
        finally { _lock.Release(); }
    }

    public async Task SendMessageAsync(
        string botToken, long chatId, string message, CancellationToken ct = default)
    {
        if (!_clients.TryGetValue(botToken, out var entry))
        {
            logger.LogWarning(
                "SendMessage: no active client for token {Token}", MaskToken(botToken));
            return;
        }
        await entry.Client.SendMessage(
            chatId, message,
            parseMode: ParseMode.Markdown,
            cancellationToken: ct);
    }

    private async Task HandleUpdateAsync(
        ITelegramBotClient bot, string botToken,
        Update update, CancellationToken ct)
    {
        if (update.Message is not { Text: { } text, Chat.Id: var chatId }) return;

        text = text.Trim();

        using var scope        = scopeFactory.CreateScope();
        var settingsSvc        = scope.ServiceProvider.GetRequiredService<UserTelegramSettingsService>();
        var conversationSvc    = scope.ServiceProvider.GetRequiredService<TelegramConversationService>();

        // ── /pair <code> ──────────────────────────────────────────────────────
        if (text.StartsWith("/pair", StringComparison.OrdinalIgnoreCase))
        {
            var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var code  = parts.Length > 1 ? parts[1].Trim() : "";
            await HandlePairAsync(bot, chatId, code, settingsSvc, ct);
            return;
        }

        // ── All other commands require a paired account ───────────────────────
        var userSettings = await settingsSvc.GetByChatIdAsync(chatId, ct);
        if (userSettings is null || userSettings.BotToken != botToken)
        {
            await bot.SendMessage(chatId,
                "⚠️ Your Telegram account is not paired.\n\n" +
                "Go to *Profile → Telegram* in Pulse and send the pair code shown there:\n`/pair YOURCODE`",
                parseMode: ParseMode.Markdown, cancellationToken: ct);
            return;
        }

        await conversationSvc.HandleMessageAsync(
            bot, chatId,
            userSettings.UserId, userSettings.TenantId,
            text, ct);

        // Self-learning: Telegram domain reflection
        BackgroundJob.Enqueue<AgentReflectionJob>(j =>
            j.ReflectAsync(userSettings.TenantId, userSettings.UserId, AgentDomain.Telegram,
                text, string.Empty, string.Empty,
                true, null, JobCancellationToken.Null));
    }

    private static async Task HandlePairAsync(
        ITelegramBotClient bot, long chatId, string code,
        UserTelegramSettingsService settingsSvc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            await bot.SendMessage(chatId,
                "Usage: `/pair YOURCODE`\n\nGet your code from *Profile → Telegram* in Pulse.",
                parseMode: ParseMode.Markdown, cancellationToken: ct);
            return;
        }

        var settings = await settingsSvc.GetByPairCodeAsync(code, ct);
        if (settings is null)
        {
            await bot.SendMessage(chatId,
                "⚠️ Invalid or expired pair code. Generate a new one in Pulse.",
                cancellationToken: ct);
            return;
        }

        await settingsSvc.CompletePairingAsync(settings.Id, chatId, ct);
        await bot.SendMessage(chatId,
            "🎉 *Paired successfully!*\n\nUse /newtask to create a scheduled task.",
            parseMode: ParseMode.Markdown, cancellationToken: ct);
    }

    private static string MaskToken(string token)
        => token.Length > 10 ? $"{token[..6]}***{token[^4..]}" : "***";

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, entry) in _clients)
        {
            await entry.Cts.CancelAsync();
            entry.Cts.Dispose();
        }
        _clients.Clear();
        _lock.Dispose();
    }
}
