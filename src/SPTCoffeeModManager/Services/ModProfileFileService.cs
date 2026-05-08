using System.IO;
using SPTCoffeeModManager.Models;

namespace SPTCoffeeModManager.Services;

public sealed class ModProfileFileService
{
    private readonly string _pluginsPath;
    private readonly string _configPath;
    private readonly string _profilesRoot;

    private HashSet<string> _excludedMods;
    private HashSet<string> _excludedModFolders;
    private HashSet<string> _excludedConfigs;

    public ModProfileFileService(
        string basePath,
        IEnumerable<string> excludedMods,
        IEnumerable<string> excludedModFolders,
        IEnumerable<string> excludedConfigs)
    {
        _pluginsPath = Path.Combine(basePath, "BepInEx", "plugins");
        _configPath = Path.Combine(basePath, "BepInEx", "config");
        _profilesRoot = Path.Combine(basePath, "BepInEx", "ModProfiles");

        _excludedMods = new HashSet<string>(excludedMods, StringComparer.OrdinalIgnoreCase);
        _excludedModFolders = new HashSet<string>(excludedModFolders, StringComparer.OrdinalIgnoreCase);
        _excludedConfigs = new HashSet<string>(excludedConfigs, StringComparer.OrdinalIgnoreCase);
    }

    public void UpdateExclusions(
        IEnumerable<string> excludedMods,
        IEnumerable<string> excludedModFolders,
        IEnumerable<string> excludedConfigs)
    {
        _excludedMods = new HashSet<string>(excludedMods, StringComparer.OrdinalIgnoreCase);
        _excludedModFolders = new HashSet<string>(excludedModFolders, StringComparer.OrdinalIgnoreCase);
        _excludedConfigs = new HashSet<string>(excludedConfigs, StringComparer.OrdinalIgnoreCase);
    }

    public void ActivateProfile(ModProfileStore store, string targetProfileName)
    {
        var target = store.Profiles.FirstOrDefault(p => string.Equals(p.Name, targetProfileName, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            throw new InvalidOperationException("Profile not found.");
        }

        var active = store.Profiles.FirstOrDefault(p => p.IsActive);
        if (active != null && string.Equals(active.Name, target.Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        EnsureLiveFolders();
        Directory.CreateDirectory(_profilesRoot);

        if (active != null)
        {
            var activeStorage = GetProfileStoragePath(active.Name);
            MoveEligiblePluginData(_pluginsPath, Path.Combine(activeStorage, "plugins"));
            MoveEligibleConfigData(_configPath, Path.Combine(activeStorage, "config"));
            active.IsActive = false;
        }

        var targetStorage = GetProfileStoragePath(target.Name);
        MoveEligiblePluginData(Path.Combine(targetStorage, "plugins"), _pluginsPath);
        MoveEligibleConfigData(Path.Combine(targetStorage, "config"), _configPath);

        target.IsActive = true;
        store.ActiveProfileName = target.Name;
    }

    public int CountMods(ModProfile profile)
    {
        var source = profile.IsActive
            ? _pluginsPath
            : Path.Combine(GetProfileStoragePath(profile.Name), "plugins");

        if (!Directory.Exists(source))
        {
            return 0;
        }

        var count = 0;
        foreach (var directory in Directory.GetDirectories(source))
        {
            var folderName = Path.GetFileName(directory);
            if (_excludedModFolders.Contains(folderName))
            {
                continue;
            }

            count++;
        }

        foreach (var file in Directory.GetFiles(source, "*.dll", SearchOption.TopDirectoryOnly))
        {
            if (ShouldSkipPluginFile(file))
            {
                continue;
            }

            count++;
        }

        return count;
    }

    public void RenameProfileStorage(string oldName, string newName)
    {
        var oldPath = GetProfileStoragePath(oldName);
        if (!Directory.Exists(oldPath))
        {
            return;
        }

        var newPath = GetProfileStoragePath(newName);
        if (Directory.Exists(newPath))
        {
            throw new IOException("Target profile folder already exists.");
        }

        Directory.Move(oldPath, newPath);
    }

    public void DeleteProfileStorage(string profileName)
    {
        var path = GetProfileStoragePath(profileName);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    private string GetProfileStoragePath(string profileName)
    {
        return Path.Combine(_profilesRoot, profileName);
    }

    private void EnsureLiveFolders()
    {
        Directory.CreateDirectory(_pluginsPath);
        Directory.CreateDirectory(_configPath);
    }

    private void MoveEligiblePluginData(string sourcePath, string destinationPath)
    {
        if (!Directory.Exists(sourcePath))
        {
            return;
        }

        Directory.CreateDirectory(destinationPath);

        foreach (var directory in Directory.GetDirectories(sourcePath))
        {
            var folderName = Path.GetFileName(directory);
            if (_excludedModFolders.Contains(folderName))
            {
                continue;
            }

            var destinationDirectory = Path.Combine(destinationPath, folderName);
            if (Directory.Exists(destinationDirectory))
            {
                Directory.Delete(destinationDirectory, true);
            }

            Directory.Move(directory, destinationDirectory);
        }

        foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.TopDirectoryOnly))
        {
            if (ShouldSkipPluginFile(file))
            {
                continue;
            }

            var destinationFile = Path.Combine(destinationPath, Path.GetFileName(file));
            if (File.Exists(destinationFile))
            {
                File.Delete(destinationFile);
            }

            File.Move(file, destinationFile);
        }
    }

    private void MoveEligibleConfigData(string sourcePath, string destinationPath)
    {
        if (!Directory.Exists(sourcePath))
        {
            return;
        }

        Directory.CreateDirectory(destinationPath);

        foreach (var directory in Directory.GetDirectories(sourcePath))
        {
            var folderName = Path.GetFileName(directory);
            var destinationDirectory = Path.Combine(destinationPath, folderName);
            if (Directory.Exists(destinationDirectory))
            {
                Directory.Delete(destinationDirectory, true);
            }

            Directory.Move(directory, destinationDirectory);
        }

        foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(file);
            if (_excludedConfigs.Contains(fileName))
            {
                continue;
            }

            var destinationFile = Path.Combine(destinationPath, fileName);
            if (File.Exists(destinationFile))
            {
                File.Delete(destinationFile);
            }

            File.Move(file, destinationFile);
        }
    }

    private bool ShouldSkipPluginFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (string.Equals(Path.GetExtension(fileName), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            var modName = Path.GetFileNameWithoutExtension(fileName);
            return _excludedMods.Contains(modName);
        }

        return false;
    }
}

