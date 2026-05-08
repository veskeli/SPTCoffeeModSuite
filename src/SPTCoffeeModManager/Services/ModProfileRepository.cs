using System.IO;
using System.Text.Json;
using SPTCoffeeModManager.Models;

namespace SPTCoffeeModManager.Services;

public sealed class ModProfileRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _profilesRoot;
    private readonly string _storePath;

    public ModProfileRepository(string basePath)
    {
        _profilesRoot = Path.Combine(basePath, "BepInEx", "ModProfiles");
        _storePath = Path.Combine(_profilesRoot, "profiles.json");
    }

    public string ProfilesRoot => _profilesRoot;

    public ModProfileStore EnsureInitialized()
    {
        Directory.CreateDirectory(_profilesRoot);

        var store = LoadStore();
        if (store.Profiles.Count == 0)
        {
            store.Profiles.Add(new ModProfile
            {
                Name = "Default",
                IsServerProfile = true,
                IsActive = true,
                IsProtected = true
            });
            store.ActiveProfileName = "Default";
            SaveStore(store);
            return store;
        }

        NormalizeStore(store);
        SaveStore(store);
        return store;
    }

    public ModProfileStore LoadStore()
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                return new ModProfileStore();
            }

            var json = File.ReadAllText(_storePath);
            return JsonSerializer.Deserialize<ModProfileStore>(json, JsonOptions) ?? new ModProfileStore();
        }
        catch
        {
            return new ModProfileStore();
        }
    }

    public void SaveStore(ModProfileStore store)
    {
        NormalizeStore(store);
        Directory.CreateDirectory(_profilesRoot);
        var json = JsonSerializer.Serialize(store, JsonOptions);
        File.WriteAllText(_storePath, json);
    }

    private static void NormalizeStore(ModProfileStore store)
    {
        store.SchemaVersion = 1;

        if (store.Profiles.Count == 0)
        {
            store.ActiveProfileName = null;
            return;
        }

        var active = store.Profiles.FirstOrDefault(p => p.IsActive);
        if (active == null)
        {
            active = store.Profiles.First();
            active.IsActive = true;
        }

        foreach (var profile in store.Profiles.Where(p => !ReferenceEquals(p, active)))
        {
            profile.IsActive = false;
        }

        store.ActiveProfileName = active.Name;
    }
}

