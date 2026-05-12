using System.Windows;
using SPTCoffee.Contracts.Models;

namespace SPTServerManager;

public partial class ConfigEditWindow : Window
{
    public ConfigInfo? Result { get; private set; }

    public ConfigEditWindow(ConfigInfo? existing = null)
    {
        InitializeComponent();

        LastModifiedDatePicker.SelectedDate = DateTime.UtcNow.Date;

        if (existing != null)
        {
            FileNameBox.Text = existing.FileName;
            LastModifiedDatePicker.SelectedDate = existing.LastModified.ToUniversalTime().Date;
            IsEnforcedCheck.IsChecked = existing.IsEnforced;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var fileName = FileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            MessageBox.Show("File name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selectedDate = LastModifiedDatePicker.SelectedDate ?? DateTime.UtcNow.Date;
        var utcDate = DateTime.SpecifyKind(selectedDate, DateTimeKind.Utc);

        Result = new ConfigInfo
        {
            FileName = fileName,
            LastModified = utcDate,
            IsEnforced = IsEnforcedCheck.IsChecked == true
        };

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

