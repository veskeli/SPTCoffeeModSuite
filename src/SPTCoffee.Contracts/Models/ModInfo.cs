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


    [JsonPropertyName("AllowOnHeadless")]
    public bool AllowOnHeadless { get; set; }

    [JsonPropertyName("IsOptional")]
    public bool IsOptional { get; set; }

    [JsonPropertyName("OptionalDefaultState")]
    public bool OptionalDefaultState { get; set; }

    [JsonPropertyName("Revision")]
    public int Revision { get; set; }

    // Pending change state: empty string = no change, "add" = pending addition, "delete" = pending deletion, "update" = pending update
    [JsonIgnore]
    public string PendingChangeState { get; set; } = string.Empty;

    // For update operations: stores the new version
    [JsonIgnore]
    public string? NewVersion { get; set; }

    // For update operations: stores the new revision (same-version updates)
    [JsonIgnore]
    public int? NewRevision { get; set; }
}
