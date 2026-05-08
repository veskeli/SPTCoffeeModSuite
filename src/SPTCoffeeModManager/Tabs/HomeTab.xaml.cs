using System.Windows.Controls;
using System.Windows;
using System.ComponentModel;
using System.Windows.Media;
using SPTCoffee.Contracts.Models;
using SPTCoffeeModManager.Models;

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
    public Button NewProfileButtonRef => NewProfileButton;
    public Button ManageProfileButtonRef => ManageProfileButton;
    public ComboBox ModProfileComboBoxRef => ModProfileComboBox;
    public Border AdminPanelRef => AdminPanel;

    public void SetProfiles(IEnumerable<ModProfile> profiles, string? selectedName)
    {
        var profileList = profiles.ToList();

        // Avoid rebinding on every refresh because it can interfere with dropdown interaction.
        var current = (ModProfileComboBox.ItemsSource as IEnumerable<ModProfile>)?.ToList();
        var shouldRebind = current == null ||
                           !current.Select(p => p.Name)
                               .SequenceEqual(profileList.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        if (shouldRebind)
        {
            ModProfileComboBox.ItemsSource = profileList;
            current = profileList;
        }

        var sourceForSelection = current ?? profileList;
        ModProfileComboBox.SelectedItem = sourceForSelection.FirstOrDefault(p =>
            string.Equals(p.Name, selectedName, StringComparison.OrdinalIgnoreCase));
    }

    public string? GetSelectedProfileName()
    {
        return (ModProfileComboBox.SelectedItem as ModProfile)?.Name;
    }

    public void SetModListVisibility(bool isServerProfile)
    {
        ModListView.Visibility = isServerProfile ? Visibility.Visible : Visibility.Collapsed;
        LocalProfileMessageText.Visibility = isServerProfile ? Visibility.Collapsed : Visibility.Visible;
    }

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

        // Keep visual state in sync with the content state.
        if (string.Equals(content, "Launch", StringComparison.OrdinalIgnoreCase))
        {
            LaunchOrUpdateButton.Background = GetThemeBrush("BrushSuccess", System.Windows.Media.Brushes.SeaGreen);
            LaunchOrUpdateButton.BorderBrush = GetThemeBrush("BrushSuccessAlt", System.Windows.Media.Brushes.MediumSeaGreen);
            return;
        }

        if (string.Equals(content, "Update", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(content, "Update SPT", StringComparison.OrdinalIgnoreCase))
        {
            LaunchOrUpdateButton.Background = GetThemeBrush("BrushWarning", System.Windows.Media.Brushes.DarkOrange);
            LaunchOrUpdateButton.BorderBrush = GetThemeBrush("BrushWarningAlt", System.Windows.Media.Brushes.Orange);
            return;
        }

        LaunchOrUpdateButton.Background = GetThemeBrush("BrushAccentAlt", System.Windows.Media.Brushes.Gray);
        LaunchOrUpdateButton.BorderBrush = GetThemeBrush("BrushBorder", System.Windows.Media.Brushes.DimGray);
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

    private static Brush GetThemeBrush(string key, Brush fallback)
    {
        return Application.Current.Resources[key] as Brush ?? fallback;
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
