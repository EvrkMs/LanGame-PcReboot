using Telegram.Bot;

namespace LangameBot;

public static class Program
{
    public static async Task Main()
    {
        var config = BotConfig.FromEnvironment();
        using var shutdownCts = new CancellationTokenSource();
        using var shutdownHook = new ShutdownHook(shutdownCts);

        using var allowlist = new AllowlistProvider(config.AllowlistPath);
        using var apiClient = new LangameApiClient(config);
        var botClient = new TelegramBotClient(config.TelegramToken);

        var runner = new BotRunner(botClient, apiClient, allowlist, config);
        await runner.RunAsync(shutdownCts.Token);
    }

    private sealed class ShutdownHook : IDisposable
    {
        private readonly CancellationTokenSource _cts;

        public ShutdownHook(CancellationTokenSource cts)
        {
            _cts = cts;
            Console.CancelKeyPress += OnCancel;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        private void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            _cts.Cancel();
        }

        private void OnProcessExit(object? sender, EventArgs e) => _cts.Cancel();

        public void Dispose()
        {
            Console.CancelKeyPress -= OnCancel;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        }
    }
}
