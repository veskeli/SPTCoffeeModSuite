using System.Text.Json.Serialization;

namespace SPTCoffee.Contracts.Models;

public class ServerModInfo
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("FileName")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>"add", "delete", or "update"</summary>
    [JsonIgnore]
    public string PendingChangeState { get; set; } = string.Empty;

    /// <summary>Target version for pending update operations.</summary>
    [JsonIgnore]
    public string? NewVersion { get; set; }

    /// <summary>Absolute path to the local mod folder – populated during scan, used when zipping.</summary>
    [JsonIgnore]
    public string? LocalFolderPath { get; set; }
}

