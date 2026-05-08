using System.Windows.Controls;
using System.Windows;
using System.ComponentModel;
using System.Windows.Media;
using SPTCoffee.Contracts.Models;

namespace SPTCoffeeModManager.Tabs;

/// <summary>
/// Interaction logic for HomeTab.xaml
/// </summary>
public partial class HomeTab : UserControl
{
    public HomeTab()
    {
        InitializeComponent();

    }

    // Expose controls to parent window for easy access
    public TextBlock ServerStatusTextBlock => ServerStatusText;
    public TextBlock SptServerStatusTextBlock => SptServerStatusText;
    public TextBlock HeadlessStatusTextBlock => HeadlessStatusText;
    public TextBlock SyncStatusTextBlock => SyncStatusText;
    public TextBlock CurrentSptVersionTextBlock => CurrentSptVersionText;
    public TextBlock StatusMessageTextBlock => StatusTextBlock;
    public Button KillHeadlessButtonRef => KillHeadlessButton;
    public Button RefreshButtonRef => RefreshButton;
    public Button CheckUpdatesButtonRef => CheckUpdatesButton;
    public Button LaunchOrUpdateButtonRef => LaunchOrUpdateButton;
    public Border AdminPanelRef => AdminPanel;

    // New: encapsulated UI update methods so parent window doesn't directly manipulate internal controls
    public void SetServerStatus(string text, Brush foreground)
    {
        ServerStatusText.Text = text;
        ServerStatusText.Foreground = foreground;
    }

    public void SetSptServerStatus(string text, Brush foreground)
    {
        SptServerStatusText.Text = text;
        SptServerStatusText.Foreground = foreground;
    }

    public void SetHeadlessStatus(string text, Brush foreground)
    {
        HeadlessStatusText.Text = text;
        HeadlessStatusText.Foreground = foreground;
    }

    public void SetSyncStatus(string text, Brush foreground)
    {
        SyncStatusText.Text = text;
        SyncStatusText.Foreground = foreground;
    }

    public void SetCurrentSptVersion(string text, Brush foreground)
    {
        CurrentSptVersionText.Text = text;
        CurrentSptVersionText.Foreground = foreground;
    }

    public void SetStatusMessage(string text)
    {
        StatusTextBlock.Text = text;
    }

    public void SetLaunchOrUpdateButtonContent(string content)
    {
        LaunchOrUpdateButton.Content = content;
    }

    public string GetLaunchOrUpdateButtonContent()
    {
        return LaunchOrUpdateButton.Content?.ToString() ?? string.Empty;
    }

    public void SetLaunchOrUpdateButtonEnabled(bool enabled)
    {
        LaunchOrUpdateButton.IsEnabled = enabled;
    }

    public void SetModListItemsSource(System.Collections.IEnumerable items)
    {
        ModListView.ItemsSource = items;
    }

    public System.Collections.IEnumerable? GetModListItemsSource()
    {
        return ModListView.ItemsSource as System.Collections.IEnumerable;
    }

    public void RefreshModList()
    {
        ModListView.Items.Refresh();
    }

    public bool IsServerOnline()
    {
        return string.Equals(ServerStatusText.Text, "Online", StringComparison.OrdinalIgnoreCase);
    }

    // Move admin UI update logic into the tab
    public void UpdateAdminStatus(AdminConfig config)
    {
        try
        {
            var vis = config.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            AdminPanel.Visibility = vis;
            AdminPanelText.Visibility = vis;

            KillHeadlessButton.Visibility = config.AllowHeadlessClose ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            // Fallback: if referencing the panel fails for any reason, set the button only
            KillHeadlessButton.Visibility = config.AllowHeadlessClose ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
