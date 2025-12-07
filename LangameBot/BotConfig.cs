namespace LangameBot;

internal sealed class BotConfig
{
    private const int DefaultTimeoutSeconds = 30;
    private const int MaxTimeoutSeconds = 300;

    private BotConfig(
        string telegramToken,
        string apiKey,
        Uri baseUri,
        string allowlistPath,
        TimeSpan apiTimeout,
        int apiRetryCount,
        int clubId,
        string pcType)
    {
        TelegramToken = telegramToken;
        ApiKey = apiKey;
        BaseUri = baseUri;
        AllowlistPath = allowlistPath;
        ApiTimeout = apiTimeout;
        ApiRetryCount = apiRetryCount;
        ClubId = clubId;
        PcType = pcType;
    }

    public string TelegramToken { get; }
    public string ApiKey { get; }
    public Uri BaseUri { get; }
    public string AllowlistPath { get; }
    public TimeSpan ApiTimeout { get; }
    public int ApiRetryCount { get; }
    public int ClubId { get; }
    public string PcType { get; }

    public static BotConfig FromEnvironment()
    {
        var telegramToken = ReadRequired("TELEGRAM_BOT_TOKEN");
        var apiKey = ReadRequired("LANGAME_API_KEY");

        var baseUrl = ReadRequired("LANGAME_BASE_URL").Trim();
        if (baseUrl.EndsWith("/"))
            baseUrl = baseUrl.TrimEnd('/');

        if (!Uri.TryCreate(baseUrl + "/", UriKind.Absolute, out var baseUri))
            throw new InvalidOperationException($"ENV LANGAME_BASE_URL is invalid: {baseUrl}");

        var allowlistPath = Environment.GetEnvironmentVariable("ALLOWED_IDS_YML");
        if (string.IsNullOrWhiteSpace(allowlistPath))
            allowlistPath = "allowed_ids.yml";
        allowlistPath = Path.GetFullPath(allowlistPath);

        var timeoutValue = ParseIntEnv("LANGAME_HTTP_TIMEOUT_SECONDS", DefaultTimeoutSeconds);
        timeoutValue = Math.Clamp(timeoutValue, 5, MaxTimeoutSeconds);
        var retries = Math.Clamp(ParseIntEnv("LANGAME_HTTP_RETRY_COUNT", 3), 1, 6);
        var clubId = Math.Max(1, ParseIntEnv("LANGAME_CLUB_ID", 1));
        var pcType = Environment.GetEnvironmentVariable("LANGAME_PC_TYPE");
        pcType = string.IsNullOrWhiteSpace(pcType) ? "free" : pcType.Trim();

        return new BotConfig(
            telegramToken.Trim(),
            apiKey.Trim(),
            baseUri,
            allowlistPath,
            TimeSpan.FromSeconds(timeoutValue),
            retries,
            clubId,
            pcType);
    }

    private static string ReadRequired(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"ENV {key} is not set.");
        return value;
    }

    private static int ParseIntEnv(string key, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }
}
