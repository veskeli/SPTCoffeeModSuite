using System.Text.Json.Serialization;

namespace SPTCoffee.Contracts.Models;

public class ModInfo
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("FileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("IsFolderMod")]
    public bool IsFolderMod { get; set; }
}

