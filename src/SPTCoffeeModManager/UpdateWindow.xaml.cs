using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;

namespace SPTCoffeeModManager;

public partial class UpdateWindow : Window
{
    public UpdateWindow()
    {
        InitializeComponent();

        // Apply Windows 11 rounded corners if available
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _ = WindowCornerHelper.TrySetWindowCornerPreference(hwnd, WindowCornerHelper.DwmWindowCorner.Round);
        };
    }

    // Make the window draggable
    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            this.DragMove();
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Updating... Please wait.";
        StatusText.Foreground = new SolidColorBrush(Colors.White);

        try
        {
            await UpdaterService.CheckAndUpdateAsync(status =>
            {
                Dispatcher.Invoke(() => StatusText.Text = status);
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = "Update failed. Please try again later.";
            StatusText.Foreground = new SolidColorBrush(Colors.Red);
            MessageBox.Show($"Failed to check for updates: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void SetStatusText(string text)
    {
        StatusText.Text = text;
        StatusText.Foreground = new SolidColorBrush(Colors.White);
    }
}