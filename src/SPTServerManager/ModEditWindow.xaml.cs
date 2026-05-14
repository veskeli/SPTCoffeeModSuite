using System.IO;
using System.IO.Compression;
using System.Windows;
using Microsoft.Win32;
using SPTCoffee.Contracts.Models;

namespace SPTServerManager;

public partial class ModEditWindow : Window
{
    public ModInfo? Result { get; private set; }
    public string? SelectedFilePath { get; private set; }

    public ModEditWindow(ModInfo? existing = null)
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

        SelectedFilePathTextBlock.Text = "-";
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

