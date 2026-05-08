namespace SPTCoffeeModManager.Models;

public sealed class ModProfileStore
{
    public int SchemaVersion { get; set; } = 1;
    public string? ActiveProfileName { get; set; }
    public List<ModProfile> Profiles { get; set; } = new();
}

