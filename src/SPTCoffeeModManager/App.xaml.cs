using System.Configuration;
using System.Data;
using System.Windows;

namespace SPTCoffeeModManager;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool isUpdateAvailable = await UpdaterService.IsUpdateAvailableAsync();
        if (isUpdateAvailable)
        {
            var updateWindow = new UpdateWindow
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            updateWindow.Show();
            return;
        }

        var mainWindow = new MainWindow();
        mainWindow.Show();
    }
}

