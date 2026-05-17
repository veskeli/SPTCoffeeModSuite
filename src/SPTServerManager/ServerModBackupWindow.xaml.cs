using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using SPTServerManager.Models;

namespace SPTServerManager;

public partial class ServerModBackupWindow : Window
{
    private readonly ObservableCollection<OldServerModBackupEntry> _backups = new();

    public OldServerModBackupEntry? RequestedBackupRestore { get; private set; }

    public ServerModBackupWindow(IEnumerable<OldServerModBackupEntry>? backups)
    {
        InitializeComponent();

        if (backups != null)
        {
            foreach (var backup in backups)
            {
                _backups.Add(backup);
            }
        }

        BackupsListView.ItemsSource = _backups;
    }

    private void RestoreSelected_Click(object sender, RoutedEventArgs e)
    {
        if (BackupsListView.SelectedItem is not OldServerModBackupEntry selected)
        {
            MessageBox.Show("Please select one archived backup to queue for restore.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        RequestedBackupRestore = selected;
        DialogResult = true;
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = BackupsListView.SelectedItems.Cast<OldServerModBackupEntry>().ToList();
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
                    MoveBackupFileToTempTrash(backup.FilePath, "RemovedServerBackups");
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

                _backups.Remove(backup);
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

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

