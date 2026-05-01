namespace Pulse.Services.Telegram;

/// <summary>
/// Background service that starts a <see cref="TelegramClientPool"/> bot client for every
/// user who has saved a bot token. New tokens are picked up via
/// <see cref="EnsureUserBotStartedAsync"/> when the user saves their settings.
/// </summary>
public sealed class TelegramBotService(
    TelegramClientPool pool,
    UserTelegramSettingsService settingsSvc,
    ILogger<TelegramBotService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var allSettings = await settingsSvc.GetAllWithTokenAsync(stoppingToken);
        foreach (var s in allSettings.Where(s => !string.IsNullOrWhiteSpace(s.BotToken)))
            await pool.EnsureStartedAsync(s.BotToken!, stoppingToken);

        logger.LogInformation(
            "TelegramBotService: started {Count} user bot(s).", allSettings.Count);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    /// <summary>
    /// Called from the Profile page after the user saves or changes their bot token.
    /// </summary>
    public async Task EnsureUserBotStartedAsync(
        string? oldToken, string newToken, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(oldToken) && oldToken != newToken)
            await pool.StopAsync(oldToken);

        if (!string.IsNullOrWhiteSpace(newToken))
            await pool.EnsureStartedAsync(newToken, ct);
    }

    /// <summary>Send a message on behalf of the user's own bot.</summary>
    public Task SendMessageAsync(
        string botToken, long chatId, string message, CancellationToken ct = default)
        => pool.SendMessageAsync(botToken, chatId, message, ct);
}
