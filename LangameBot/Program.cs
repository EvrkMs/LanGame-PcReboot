using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LangameBot;

public class Program
{
    private static readonly string? TelegramToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
    private static readonly string? ApiKey = Environment.GetEnvironmentVariable("LANGAME_API_KEY");
    private static readonly string? BaseUrlEnv = Environment.GetEnvironmentVariable("LANGAME_BASE_URL");
    private static readonly string BaseUrl = (BaseUrlEnv ?? throw new InvalidOperationException("ENV LANGAME_BASE_URL is not set.")).TrimEnd('/');
    private static readonly string AllowlistPath = Environment.GetEnvironmentVariable("ALLOWED_IDS_YML") ?? "allowed_ids.yml";
    private static readonly string? ApiTimeoutEnv = Environment.GetEnvironmentVariable("LANGAME_HTTP_TIMEOUT_SECONDS");
    private static readonly string? ApiRetryEnv = Environment.GetEnvironmentVariable("LANGAME_HTTP_RETRY_COUNT");
    private static readonly int ApiTimeoutSeconds = int.TryParse(ApiTimeoutEnv, out var parsedTimeout) && parsedTimeout > 0
        ? parsedTimeout
        : 30;
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(ApiTimeoutSeconds);
    private static readonly int ApiRequestMaxRetries = Math.Clamp(int.TryParse(ApiRetryEnv, out var parsedRetry) ? parsedRetry : 3, 1, 6);
    private static readonly Lazy<HttpClient> ApiHttpClientLazy = new(BuildHttpClient);
    private static HashSet<long> AllowedChatIds = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task Main()
    {
        if (string.IsNullOrWhiteSpace(TelegramToken))
            throw new InvalidOperationException("ENV TELEGRAM_BOT_TOKEN is not set.");
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("ENV LANGAME_API_KEY is not set.");

        // Load Telegram ID allowlist (optional file). If empty or missing, allow all.
        AllowedChatIds = LoadAllowlist(AllowlistPath);

        var bot = new TelegramBotClient(TelegramToken!);
        using var cts = new CancellationTokenSource();

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = new[] { UpdateType.Message },
            DropPendingUpdates = true
        };

        bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, receiverOptions, cts.Token);

        var me = await bot.GetMe(cts.Token);
        Console.WriteLine($"Bot @{me.Username} started. Use: /free_reboot");

        await Task.Delay(Timeout.Infinite, cts.Token);
    }

    private static async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Message is not { } msg || msg.Type != MessageType.Text || msg.Text is null)
            return;

        var chatId = msg.Chat.Id;
        // If allowlist is set and chatId is not allowed — ignore silently
        if (AllowedChatIds.Count > 0 && !AllowedChatIds.Contains(chatId))
            return;
        var text = msg.Text.Trim();

        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            await bot.SendMessage(chatId, "Команда:\n/free_reboot\n\nПерезагружает все FREE ПК в клубе #1 и отвечает строками <pc name>:true/false", cancellationToken: ct);
            return;
        }

        if (text.StartsWith("/free_reboot", StringComparison.OrdinalIgnoreCase))
        {
            await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);

            try
            {
                var resultLines = await RebootAndReportAsync(ct);
                if (resultLines.Count == 0)
                {
                    await bot.SendMessage(chatId, "Нет данных (пустой ответ или не найдены соответствия UUID→name).", cancellationToken: ct);
                    return;
                }

                var sb = new StringBuilder();
                foreach (var line in resultLines)
                {
                    if (sb.Length + line.Length + 1 > 3800) // запас до лимита Telegram ~4096
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
            catch (TaskCanceledException)
            {
                await bot.SendMessage(chatId, "Таймаут запроса к API.", cancellationToken: ct);
            }
            catch (Exception ex)
            {
                await bot.SendMessage(chatId, $"Ошибка: {ex.Message}", cancellationToken: ct);
            }

            return;
        }

        await bot.SendMessage(chatId, "Неизвестная команда. Доступно: /free_reboot", cancellationToken: ct);
    }

    private static Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        // Suppress expected long-poll timeouts and cancellations from Telegram long polling
        if (ex is TaskCanceledException || ex is OperationCanceledException || ex is TimeoutException)
            return Task.CompletedTask;

        if (ex is Telegram.Bot.Exceptions.RequestException reqEx)
        {
            var msg = reqEx.Message ?? string.Empty;
            if (msg.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;
        }

        Console.WriteLine($"[Telegram Error] {ex}");
        return Task.CompletedTask;
    }

    private static async Task<List<string>> RebootAndReportAsync(CancellationToken ct)
    {
        // 1) POST /public_api/pc/manage (club_id=1, type=free)
        var manageReq = new ManageRequest
        {
            ClubId = 1,
            Command = "reboot",
            Type = "free",
            Uuids = null
        };

        var managePayload = JsonSerializer.Serialize(manageReq, JsonOpts);
        var manageJson = await SendApiRequestAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/public_api/pc/manage")
            {
                Content = new StringContent(managePayload, Encoding.UTF8, "application/json")
            };
            return request;
        }, ct);
        var manage = JsonSerializer.Deserialize<ManageResponse>(manageJson, JsonOpts)
                     ?? throw new InvalidOperationException("Не удалось распарсить ответ manage.");

        if (!manage.Status || manage.Data is null || manage.Data.Count == 0)
            return new List<string>();

        // 2) GET /global/linking_pc_by_type/list
        // Если бек ожидает фильтры, лучше сразу явно указать:
        var listUrl = "/public_api/global/linking_pc_by_type/list?club_id=1&type=free";
        var listJson = await SendApiRequestAsync(() => new HttpRequestMessage(HttpMethod.Get, listUrl), ct);

        var pcs = DeserializeLinkedPcList(listJson);

        // UUID -> Name
        var uuidToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pc in pcs)
        {
            if (!string.IsNullOrWhiteSpace(pc.UUID))
                uuidToName[pc.UUID] = string.IsNullOrWhiteSpace(pc.Name) ? pc.UUID : pc.Name!;
        }

        // Формат "<pc name>:true/false"
        var lines = new List<string>(manage.Data.Count);
        foreach (var kv in manage.Data)
        {
            var uuid = kv.Key;
            var statusArr = kv.Value;
            var ok = (statusArr is { Length: > 0 } && statusArr[0]);

            var name = uuidToName.TryGetValue(uuid, out var foundName) ? foundName : uuid;
            lines.Add($"{name}:{ok.ToString().ToLowerInvariant()}");
        }

        lines.Sort(StringComparer.OrdinalIgnoreCase);
        return lines;
    }

    private static HttpClient ApiHttpClient => ApiHttpClientLazy.Value;

    private static HttpClient BuildHttpClient()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 10
        })
        {
            Timeout = ApiTimeout,
            BaseAddress = new Uri(BaseUrl + "/")
        };
        http.DefaultRequestHeaders.Add("Accept", "*/*");
        // Ensure API key is sent for all requests to BaseUrl
        http.DefaultRequestHeaders.Remove("X-API-KEY");
        http.DefaultRequestHeaders.Add("X-API-KEY", ApiKey!);
        return http;
    }

    private static async Task<string> SendApiRequestAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= ApiRequestMaxRetries; attempt++)
        {
            using var request = requestFactory();
            try
            {
                using var response = await ApiHttpClient.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested && attempt < ApiRequestMaxRetries)
            {
                lastError = ex;
            }
            catch (HttpRequestException ex) when (attempt < ApiRequestMaxRetries)
            {
                lastError = ex;
            }

            var delayMs = Math.Min(500 * (1 << (attempt - 1)), 4000);
            await Task.Delay(delayMs, ct);
        }

        if (lastError is not null)
            throw lastError;

        throw new InvalidOperationException("Не удалось выполнить запрос к API.");
    }

    private static List<LinkedPc> DeserializeLinkedPcList(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<LinkedPc>>(root.GetRawText(), JsonOpts) ?? new();
            }
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    return JsonSerializer.Deserialize<List<LinkedPc>>(data.GetRawText(), JsonOpts) ?? new();
                }
                if (root.TryGetProperty("Data", out var data2) && data2.ValueKind == JsonValueKind.Array)
                {
                    return JsonSerializer.Deserialize<List<LinkedPc>>(data2.GetRawText(), JsonOpts) ?? new();
                }
            }
        }
        catch
        {
            // fallthrough to generic parse
        }

        // Last attempt as plain list
        try
        {
            return JsonSerializer.Deserialize<List<LinkedPc>>(json, JsonOpts) ?? new();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse list of PCs: {ex.Message}");
        }
    }

    private static HashSet<long> LoadAllowlist(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new HashSet<long>();

            var yaml = File.ReadAllText(path);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            // Try plain list first
            try
            {
                var list = deserializer.Deserialize<List<long>>(yaml);
                if (list is { Count: > 0 })
                    return new HashSet<long>(list);
            }
            catch { /* fallthrough */ }

            // Try object with common keys
            try
            {
                var cfg = deserializer.Deserialize<AllowlistConfig>(yaml);
                var list = cfg?.Allowlist ?? cfg?.AllowedIds ?? cfg?.Ids;
                if (list is { Count: > 0 })
                    return new HashSet<long>(list);
            }
            catch { /* ignore */ }
        }
        catch { /* ignore and allow all */ }
        return new HashSet<long>();
    }

    private sealed class AllowlistConfig
    {
        public List<long>? Allowlist { get; set; }
        public List<long>? AllowedIds { get; set; }
        public List<long>? Ids { get; set; }
    }

    // ===== DTO =====
    private sealed class ManageRequest
    {
        [JsonPropertyName("club_id")] public int ClubId { get; set; }
        [JsonPropertyName("command")] public string Command { get; set; } = "reboot";
        [JsonPropertyName("type")] public string Type { get; set; } = "free";
        [JsonPropertyName("uuids")] public string[]? Uuids { get; set; }
    }

    private sealed class ManageResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("data")] public Dictionary<string, bool[]>? Data { get; set; }
    }

    private sealed class LinkedPc
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("pc_number")] public string? PcNumber { get; set; }
        [JsonPropertyName("packets_type_PC")] public int PacketsTypePC { get; set; }
        [JsonPropertyName("fiscal_name")] public string? FiscalName { get; set; }
        [JsonPropertyName("UUID")] public string? UUID { get; set; }
        [JsonPropertyName("club_id")] public int ClubId { get; set; }
        [JsonPropertyName("date")] public string? Date { get; set; }
        [JsonPropertyName("isPS")] public int IsPS { get; set; }
        [JsonPropertyName("rele_type")] public string? ReleType { get; set; }
        [JsonPropertyName("color")] public string? Color { get; set; }
    }
}
