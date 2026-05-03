using System.Text.Json.Serialization;

namespace SPTCoffee.Contracts.Models;

public class AdminConfig
{
    [JsonPropertyName("Note")]
    public string Note { get; set; } = string.Empty;

    [JsonPropertyName("Secret")]
    public string Secret { get; set; } = string.Empty;

    [JsonPropertyName("IsEnabled")]
    public bool IsEnabled { get; set; } = true;

    [JsonPropertyName("AllowHeadlessClose")]
    public bool AllowHeadlessClose { get; set; }
}

