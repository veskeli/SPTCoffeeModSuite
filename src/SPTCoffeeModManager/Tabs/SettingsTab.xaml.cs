using System;
using System.Windows;
using System.Windows.Controls;

namespace SPTCoffeeModManager.Tabs;

/// <summary>
/// Interaction logic for SettingsTab.xaml
/// </summary>
public partial class SettingsTab : UserControl
{
    public SettingsTab()
    {
        InitializeComponent();
        Loaded += SettingsTab_Loaded;
    }

    private void SettingsTab_Loaded(object? sender, RoutedEventArgs e)
    {
        // Populate fields from parent MainWindow config when the tab is loaded
        if (Application.Current.MainWindow is MainWindow main)
        {
            AddressTextBox.Text = main.GetServerIp();
            PortTextBox.Text = main.GetServerPort().ToString();
            SptServerAddressTextBox.Text = main.GetSptServerAddress();
            SecretKeyTextBox.Password = main.GetSecret() ?? string.Empty;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortTextBox.Text.Trim(), out var port) || port <= 0)
        {
            MessageBox.Show("Please enter a valid port number.", "Invalid Port", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (Application.Current.MainWindow is MainWindow main)
        {
            main.SetServerIp(AddressTextBox.Text.Trim());
            main.SetServerPort(port);
            main.SetSptServerAddress(SptServerAddressTextBox.Text.Trim());
            main.SetSecret(SecretKeyTextBox.Password);

            // Validate secret on server and update admin status via public wrapper
            var validatedSecret = main.ValidateSecretPublic(main.GetSecret());
            main.SetSecret(validatedSecret);

            // Persist config and refresh status via public wrappers
            main.SaveConfigPublic();
            _ = main.RefreshModsPublic();
            _ = main.CheckServerStatusPublic();

            MessageBox.Show("Server configuration saved.", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
