using System.IO;
using System.IO.Compression;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using SPTCoffee.Contracts.Models;
using SPTServerManager.Models;

namespace SPTServerManager;

public partial class ModEditWindow : Window
{
    private readonly ObservableCollection<OldPluginBackupEntry> _oldBackups = new();
    public ModInfo? Result { get; private set; }
    public string? SelectedFilePath { get; private set; }
    public OldPluginBackupEntry? RequestedBackupRevert { get; private set; }

    public ModEditWindow(ModInfo? existing = null, IEnumerable<OldPluginBackupEntry>? oldBackups = null)
    {
        InitializeComponent();

        if (existing != null)
        {
            NameBox.Text = existing.Name;
            VersionBox.Text = existing.Version;
            FileNameBox.Text = existing.FileName;
            IsFolderModCheck.IsChecked = existing.IsFolderMod;
            AllowOnHeadlessCheck.IsChecked = existing.AllowOnHeadless;
            IsOptionalCheck.IsChecked = existing.IsOptional;
            OptionalDefaultStateCheck.IsChecked = existing.OptionalDefaultState;
        }

        if (oldBackups != null)
        {
            foreach (var backup in oldBackups)
            {
                _oldBackups.Add(backup);
            }
        }

        OldModsListView.ItemsSource = _oldBackups;
        SelectedFilePathTextBlock.Text = "-";
    }

    private void RevertOldBackup_Click(object sender, RoutedEventArgs e)
    {
        if (OldModsListView.SelectedItem is not OldPluginBackupEntry selected)
        {
            MessageBox.Show("Please select one archived backup to queue for revert.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        RequestedBackupRevert = selected;
        DialogResult = true;
    }

    private void RemoveOldBackups_Click(object sender, RoutedEventArgs e)
    {
        var selected = OldModsListView.SelectedItems.Cast<OldPluginBackupEntry>().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Please select one or more archived backups to remove.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"Remove {selected.Count} archived backup(s)?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var removed = 0;
        var failures = new List<string>();
        foreach (var backup in selected)
        {
            try
            {
                if (File.Exists(backup.FilePath))
                {
                    MoveBackupFileToTempTrash(backup.FilePath, "RemovedPluginBackups");
                }

                var versionFolder = Path.GetDirectoryName(backup.FilePath);
                if (!string.IsNullOrWhiteSpace(versionFolder)
                    && Directory.Exists(versionFolder)
                    && !Directory.EnumerateFileSystemEntries(versionFolder).Any())
                {
                    Directory.Delete(versionFolder, false);
                }

                var modFolder = string.IsNullOrWhiteSpace(versionFolder) ? null : Path.GetDirectoryName(versionFolder);
                if (!string.IsNullOrWhiteSpace(modFolder)
                    && Directory.Exists(modFolder)
                    && !Directory.EnumerateFileSystemEntries(modFolder).Any())
                {
                    Directory.Delete(modFolder, false);
                }

                _oldBackups.Remove(backup);
                removed++;
            }
            catch (Exception ex)
            {
                failures.Add($"{backup.FileName}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            var details = string.Join("\n", failures.Take(5));
            if (failures.Count > 5)
                details += $"\n...and {failures.Count - 5} more.";

            MessageBox.Show($"Removed {removed} backup(s). Failed: {failures.Count}.\n\n{details}", "Completed with errors", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show($"Removed {removed} archived backup(s).", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void MoveBackupFileToTempTrash(string backupFilePath, string category)
    {
        var tempRoot = FindTempRoot(backupFilePath);
        if (string.IsNullOrWhiteSpace(tempRoot))
            throw new InvalidOperationException("Could not determine the temp backup root for the selected backup.");

        var trashFolder = Path.Combine(tempRoot, "Trash", category);
        Directory.CreateDirectory(trashFolder);

        var destinationPath = GetUniqueDestinationPath(trashFolder, Path.GetFileName(backupFilePath));
        File.Move(backupFilePath, destinationPath);
    }

    private static string? FindTempRoot(string path)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(path) ?? string.Empty);
        while (directory != null)
        {
            if (string.Equals(directory.Name, "Temp", StringComparison.OrdinalIgnoreCase))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    private static string GetUniqueDestinationPath(string destinationFolder, string fileName)
    {
        var destinationPath = Path.Combine(destinationFolder, fileName);
        if (!File.Exists(destinationPath) && !Directory.Exists(destinationPath))
            return destinationPath;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var suffix = 1;
        do
        {
            destinationPath = Path.Combine(destinationFolder, $"{stem}_{DateTime.UtcNow:yyyyMMddHHmmss}_{suffix}{extension}");
            suffix++;
        } while (File.Exists(destinationPath) || Directory.Exists(destinationPath));

        return destinationPath;
    }

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Select Mod File (ZIP or DLL)",
            Filter = "ZIP Files (*.zip)|*.zip|DLL Files (*.dll)|*.dll|All Files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (picker.ShowDialog() != true)
            return;

        SelectedFilePath = picker.FileName;
        FileNameBox.Text = Path.GetFileName(picker.FileName);
        SelectedFilePathTextBlock.Text = picker.FileName;

        // Auto-detect IsFolderMod from the selected file
        IsFolderModCheck.IsChecked = DetectIsFolderMod(picker.FileName);
    }

    private static bool DetectIsFolderMod(string filePath)
    {
        if (!filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return false; // DLLs are never folder mods

        try
        {
            using var archive = ZipFile.OpenRead(filePath);
            foreach (var entry in archive.Entries)
            {
                var fullName = entry.FullName.Replace('\\', '/').Trim('/');
                if (string.IsNullOrWhiteSpace(fullName)) continue;
                var segments = fullName.Split('/', StringSplitOptions.RemoveEmptyEntries);

                // BepInEx/plugins/<something>/<...> → the <something> is a subfolder → folder mod
                // BepInEx/plugins/<something.dll>   → DLL directly in plugins → not folder mod
                if (segments.Length >= 3
                    && string.Equals(segments[0], "BepInEx", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(segments[1], "plugins", StringComparison.OrdinalIgnoreCase))
                {
                    // If segment[2] itself is a directory (has deeper children) or is not a .dll → folder mod
                    bool directDll = segments.Length == 3
                                     && segments[2].EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                    return !directDll;
                }
            }
        }
        catch
        {
            // On failure, fall back to true (safest default for ZIPs)
            return true;
        }

        return false; // No BepInEx/plugins found at all
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = new ModInfo
        {
            Name = name,
            Version = VersionBox.Text.Trim(),
            FileName = FileNameBox.Text.Trim(),
            IsFolderMod = IsFolderModCheck.IsChecked == true,
            AllowOnHeadless = AllowOnHeadlessCheck.IsChecked == true,
            IsOptional = IsOptionalCheck.IsChecked == true,
            OptionalDefaultState = OptionalDefaultStateCheck.IsChecked == true
        };

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

