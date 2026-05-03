using System.Text.Json.Serialization;

namespace SPTCoffee.Contracts.Models;

public class ConfigInfo
{
    [JsonPropertyName("FileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("LastModified")]
    public DateTime LastModified { get; set; }

    [JsonPropertyName("IsEnforced")]
    public bool IsEnforced { get; set; }
}

