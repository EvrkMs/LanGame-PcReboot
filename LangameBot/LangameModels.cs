using System.Text.Json.Serialization;

namespace LangameBot;

internal sealed class ManageResponse
{
    [JsonPropertyName("status")] public bool Status { get; set; }
    [JsonPropertyName("data")] public Dictionary<string, bool[]>? Data { get; set; }
}

internal sealed class LinkedPc
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
