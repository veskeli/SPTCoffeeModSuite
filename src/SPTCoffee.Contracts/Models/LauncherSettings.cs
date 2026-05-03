using System.Text.Json.Serialization;

namespace SPTCoffee.Contracts.Models;

public class LauncherSettings
{
    [JsonPropertyName("ExcludedMods")]
    public List<string> ExcludedMods { get; set; } = new();

    [JsonPropertyName("ExcludedModFolders")]
    public List<string> ExcludedModFolders { get; set; } = new();

    [JsonPropertyName("ExcludedConfigs")]
    public List<string> ExcludedConfigs { get; set; } = new();
}

