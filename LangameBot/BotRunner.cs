using System.Net.Http;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace LangameBot;

internal sealed class BotRunner
{
    private readonly ITelegramBotClient _bot;
    private readonly LangameApiClient _apiClient;
    private readonly AllowlistProvider _allowlistProvider;
    private readonly BotConfig _config;

    public BotRunner(ITelegramBotClient bot, LangameApiClient apiClient, AllowlistProvider allowlistProvider, BotConfig config)
    {
        _bot = bot;
        _apiClient = apiClient;
        _allowlistProvider = allowlistProvider;
        _config = config;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = new[] { UpdateType.Message },
            DropPendingUpdates = true
        };

        _bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, receiverOptions, cancellationToken);

        var me = await _bot.GetMe(cancellationToken);
        Console.WriteLine($"Bot @{me.Username} started. Use: /free_reboot");

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Message is not { } message || message.Type != MessageType.Text || string.IsNullOrWhiteSpace(message.Text))
            return;

        var chatId = message.Chat.Id;
        if (!_allowlistProvider.IsAllowed(chatId))
            return;

        var text = message.Text.Trim();
        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            await bot.SendMessage(chatId, BuildHelpText(), cancellationToken: ct);
            return;
        }

        if (text.StartsWith("/free_reboot", StringComparison.OrdinalIgnoreCase))
        {
            await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
            await HandleFreeRebootAsync(bot, chatId, ct);
            return;
        }

        await bot.SendMessage(chatId, "Неизвестная команда. Доступно: /free_reboot", cancellationToken: ct);
    }

    private async Task HandleFreeRebootAsync(ITelegramBotClient bot, long chatId, CancellationToken ct)
    {
        try
        {
            var lines = await BuildRebootLinesAsync(ct);
            if (lines.Count == 0)
            {
                await bot.SendMessage(chatId, "Нет данных (пустой ответ или не найдены соответствия UUID→name).", cancellationToken: ct);
                return;
            }

            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                if (sb.Length + line.Length + 1 > 3800)
                {
                    await bot.SendMessage(chatId, sb.ToString(), cancellationToken: ct);
                    sb.Clear();
                }

                sb.AppendLine(line);
            }

            if (sb.Length > 0)
                await bot.SendMessage(chatId, sb.ToString(), cancellationToken: ct);
        }
        catch (HttpRequestException ex)
        {
            await bot.SendMessage(chatId, $"HTTP ошибка: {ex.Message}", cancellationToken: ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            await bot.SendMessage(chatId, "Таймаут запроса к API.", cancellationToken: ct);
        }
        catch (Exception ex)
        {
            await bot.SendMessage(chatId, $"Ошибка: {ex.Message}", cancellationToken: ct);
        }
    }

    private async Task<IReadOnlyList<string>> BuildRebootLinesAsync(CancellationToken ct)
    {
        var manageTask = _apiClient.RebootAsync(_config.ClubId, _config.PcType, ct);
        var namesTask = _apiClient.GetPcNameLookupAsync(_config.ClubId, _config.PcType, ct);

        var manage = await manageTask;
        var uuidToName = await namesTask;

        if (!manage.Status || manage.Data is null || manage.Data.Count == 0)
            return Array.Empty<string>();

        var lines = new List<string>(manage.Data.Count);
        foreach (var (uuid, statuses) in manage.Data)
        {
            var ok = statuses is { Length: > 0 } && statuses[0];
            var name = uuidToName.TryGetValue(uuid, out var resolved) ? resolved : uuid;
            lines.Add($"{name}:{ok.ToString().ToLowerInvariant()}");
        }

        lines.Sort(StringComparer.OrdinalIgnoreCase);
        return lines;
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is TaskCanceledException or OperationCanceledException or TimeoutException)
            return Task.CompletedTask;

        if (exception is Telegram.Bot.Exceptions.RequestException reqEx)
        {
            var msg = reqEx.Message ?? string.Empty;
            if (msg.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;
        }

        Console.WriteLine($"[Telegram Error] {exception}");
        return Task.CompletedTask;
    }

    private string BuildHelpText() =>
        "Команда:\n/free_reboot\n\nПерезагружает все FREE ПК и отвечает строками <pc name>:true/false";
}
