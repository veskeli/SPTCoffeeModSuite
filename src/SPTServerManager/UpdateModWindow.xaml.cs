using System.Diagnostics;
using System.IO;
using System.Windows;
using SPTCoffee.Contracts.Models;
using Microsoft.Win32;

namespace SPTServerManager;

public partial class UpdateModWindow : Window
{
    public ModInfo? Result { get; private set; }
    private string? _selectedFilePath;

    public UpdateModWindow(ModInfo mod)
    {
        InitializeComponent();
        CurrentVersionTextBlock.Text = mod.Version;
        Title = $"Update Mod - {mod.Name}";
        Result = new ModInfo
        {
            Name = mod.Name,
            Version = mod.Version,
            FileName = mod.FileName,
            IsFolderMod = mod.IsFolderMod,
            AllowOnHeadless = mod.AllowOnHeadless,
            IsOptional = mod.IsOptional,
            OptionalDefaultState = mod.OptionalDefaultState
        };
    }

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new OpenFileDialog
        {
            Title = "Select Mod File (DLL or ZIP)",
            Filter = "DLL Files (*.dll)|*.dll|ZIP Files (*.zip)|*.zip|All Files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };

        if (openFileDialog.ShowDialog() == true)
        {
            _selectedFilePath = openFileDialog.FileName;
            FileNameTextBlock.Text = Path.GetFileName(_selectedFilePath);
            SelectedFilePathTextBlock.Text = _selectedFilePath;

            // Extract version from the file
            try
            {
                string newVersion = ExtractVersionFromFile(_selectedFilePath);
                NewVersionTextBlock.Text = newVersion;
                NewVersionTextBlock.Foreground = System.Windows.Media.Brushes.LimeGreen;
                UpdateButton.IsEnabled = true;

                if (Result != null)
                {
                    Result.NewVersion = newVersion;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not extract version: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                NewVersionTextBlock.Text = "-";
                NewVersionTextBlock.Foreground = System.Windows.Media.Brushes.Red;
                UpdateButton.IsEnabled = false;
            }
        }
    }

    private string ExtractVersionFromFile(string filePath)
    {
        if (filePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var fileVersionInfo = FileVersionInfo.GetVersionInfo(filePath);
            return fileVersionInfo.FileVersion ?? "0.0.0";
        }
        else if (filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // For ZIP files, we can't extract version directly, so use file's modified date or just "unknown"
            return "custom";
        }
        return "unknown";
    }

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        if (Result != null && !string.IsNullOrWhiteSpace(_selectedFilePath))
        {
            DialogResult = true;
            Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

