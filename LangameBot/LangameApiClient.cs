using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LangameBot;

internal sealed class LangameApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly int _maxRetries;

    public LangameApiClient(BotConfig config)
    {
        _maxRetries = config.ApiRetryCount;

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 10
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = config.ApiTimeout,
            BaseAddress = config.BaseUri
        };
        _httpClient.DefaultRequestHeaders.Add("Accept", "*/*");
        _httpClient.DefaultRequestHeaders.Remove("X-API-KEY");
        _httpClient.DefaultRequestHeaders.Add("X-API-KEY", config.ApiKey);
    }

    public Task<ManageResponse> RebootAsync(int clubId, string pcType, CancellationToken ct)
    {
        var requestModel = new ManageRequest
        {
            ClubId = clubId,
            Command = "reboot",
            Type = pcType,
            Uuids = null
        };
        var payload = JsonSerializer.Serialize(requestModel, JsonOptions);

        return SendJsonAsync<ManageResponse>(() =>
        {
            return new HttpRequestMessage(HttpMethod.Post, "/public_api/pc/manage")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }, ct);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetPcNameLookupAsync(int clubId, string pcType, CancellationToken ct)
    {
        var listUrl = $"/public_api/global/linking_pc_by_type/list?club_id={clubId}&type={pcType}";
        var json = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, listUrl), ct);
        var pcs = DeserializeLinkedPcList(json);
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pc in pcs)
        {
            if (string.IsNullOrWhiteSpace(pc.UUID))
                continue;
            var displayName = string.IsNullOrWhiteSpace(pc.Name) ? pc.UUID : pc.Name!;
            dict[pc.UUID] = displayName;
        }

        return dict;
    }

    private async Task<T> SendJsonAsync<T>(Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        var json = await SendAsync(requestFactory, ct);
        var result = JsonSerializer.Deserialize<T>(json, JsonOptions);
        if (result is null)
            throw new InvalidOperationException($"Не удалось распарсить ответ {typeof(T).Name}.");
        return result;
    }

    private async Task<string> SendAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= _maxRetries; attempt++)
        {
            using var request = requestFactory();
            try
            {
                using var response = await _httpClient.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && attempt < _maxRetries)
            {
                lastError = ex;
            }
            catch (HttpRequestException ex) when (attempt < _maxRetries)
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
                return JsonSerializer.Deserialize<List<LinkedPc>>(root.GetRawText(), JsonOptions) ?? new List<LinkedPc>();

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    return JsonSerializer.Deserialize<List<LinkedPc>>(data.GetRawText(), JsonOptions) ?? new List<LinkedPc>();
                if (root.TryGetProperty("Data", out var data2) && data2.ValueKind == JsonValueKind.Array)
                    return JsonSerializer.Deserialize<List<LinkedPc>>(data2.GetRawText(), JsonOptions) ?? new List<LinkedPc>();
            }
        }
        catch
        {
            // fallthrough to plain parse
        }

        try
        {
            return JsonSerializer.Deserialize<List<LinkedPc>>(json, JsonOptions) ?? new List<LinkedPc>();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse list of PCs: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed class ManageRequest
    {
        [JsonPropertyName("club_id")] public int ClubId { get; set; }
        [JsonPropertyName("command")] public string Command { get; set; } = "reboot";
        [JsonPropertyName("type")] public string Type { get; set; } = "free";
        [JsonPropertyName("uuids")] public string[]? Uuids { get; set; }
    }
}
