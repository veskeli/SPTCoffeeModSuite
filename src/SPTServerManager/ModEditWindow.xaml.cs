using System.Windows;
using SPTCoffee.Contracts.Models;

namespace SPTServerManager;

public partial class ModEditWindow : Window
{
    public ModInfo? Result { get; private set; }

    public ModEditWindow(ModInfo? existing = null)
    {
        InitializeComponent();

        if (existing != null)
        {
            NameBox.Text = existing.Name;
            VersionBox.Text = existing.Version;
            FileNameBox.Text = existing.FileName;
            IsFolderModCheck.IsChecked = existing.IsFolderMod;
            IsForcedCheck.IsChecked = existing.IsForced;
            AllowOnHeadlessCheck.IsChecked = existing.AllowOnHeadless;
            IsOptionalCheck.IsChecked = existing.IsOptional;
            OptionalDefaultStateCheck.IsChecked = existing.OptionalDefaultState;
        }
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
            IsForced = IsForcedCheck.IsChecked == true,
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

