using System.Text.Json.Serialization;

namespace SPTCoffee.Contracts.Models;

public class PlayerPresenceInfo
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("AccountId")]
    public string AccountId { get; set; } = string.Empty;

    [JsonPropertyName("LastSeenStamp")]
    public string LastSeenStamp { get; set; } = string.Empty;
}

public class PlayerPresenceEventInfo
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("AccountId")]
    public string AccountId { get; set; } = string.Empty;

    [JsonPropertyName("IsConnected")]
    public bool IsConnected { get; set; }

    [JsonPropertyName("Stamp")]
    public string Stamp { get; set; } = string.Empty;
}

public class PlayerPresenceSnapshot
{
    [JsonPropertyName("LogFilePath")]
    public string LogFilePath { get; set; } = string.Empty;

    [JsonPropertyName("LogLastWriteUtc")]
    public DateTime LogLastWriteUtc { get; set; }

    [JsonPropertyName("LastUpdatedUtc")]
    public DateTime LastUpdatedUtc { get; set; }

    [JsonPropertyName("IsServerRunning")]
    public bool IsServerRunning { get; set; }

    [JsonPropertyName("StatusMessage")]
    public string StatusMessage { get; set; } = string.Empty;

    [JsonPropertyName("ActiveCount")]
    public int ActiveCount { get; set; }

    [JsonPropertyName("ActivePlayers")]
    public List<PlayerPresenceInfo> ActivePlayers { get; set; } = [];

    [JsonPropertyName("RecentEvents")]
    public List<PlayerPresenceEventInfo> RecentEvents { get; set; } = [];

    [JsonPropertyName("Error")]
    public string Error { get; set; } = string.Empty;

    /// <summary>Current raid type: "None", "Solo", or "Headless".</summary>
    [JsonPropertyName("CurrentRaidType")]
    public string CurrentRaidType { get; set; } = "None";

    /// <summary>Players detected in the current headless raid (from headless BepInEx log).</summary>
    [JsonPropertyName("HeadlessRaidPlayers")]
    public List<string> HeadlessRaidPlayers { get; set; } = [];
}
