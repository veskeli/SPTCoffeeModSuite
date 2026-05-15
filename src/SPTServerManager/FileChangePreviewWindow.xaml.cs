using System.Windows;
using System.Windows.Controls;
using SPTServerManager.Models;

namespace SPTServerManager;

public partial class FileChangePreviewWindow : Window
{
    public IReadOnlyCollection<string> ExcludedSourcePaths { get; private set; } = Array.Empty<string>();
    public string? SelectedPluginVersion { get; private set; }
    public string? SelectedServerVersion { get; private set; }
    public bool IncludePluginUpdate { get; private set; }
    public bool IncludeServerUpdate { get; private set; }
    public bool SkipPluginUpdateOnSameVersion { get; private set; }
    public bool SkipServerUpdateOnSameVersion { get; private set; }
    public bool SelectedPluginIsFolderMod { get; private set; }
    public bool SelectedPluginAllowOnHeadless { get; private set; }
    public bool SelectedPluginIsOptional { get; private set; }
    public bool SelectedPluginOptionalDefaultState { get; private set; }

    private readonly IReadOnlyCollection<FileChangePreviewItem> _items;
    private readonly string _pluginAction;
    private readonly string? _pluginOldVersion;
    private readonly string _serverAction;
    private readonly string? _serverOldVersion;

    public FileChangePreviewWindow(
        IReadOnlyCollection<FileChangePreviewItem> items,
        bool hasConfigConflicts,
        string actionSummary,
        string detectedType,
        string? detectedPluginName,
        string? detectedServerModName,
        string pluginAction,
        string? pluginOldVersion,
        string? pluginNewVersion,
        bool pluginIsFolderMod,
        bool pluginAllowOnHeadless,
        bool pluginIsOptional,
        bool pluginOptionalDefaultState,
        string serverAction,
        string? serverOldVersion,
        string? serverNewVersion)
    {
        InitializeComponent();

        _items = items;
        _pluginAction = pluginAction;
        _pluginOldVersion = pluginOldVersion;
        _serverAction = serverAction;
        _serverOldVersion = serverOldVersion;
        FilesListView.ItemsSource = items;

        ActionSummaryTextBlock.Text = actionSummary;

        var pluginText = string.IsNullOrWhiteSpace(detectedPluginName) ? "-" : detectedPluginName;
        var serverText = string.IsNullOrWhiteSpace(detectedServerModName) ? "-" : detectedServerModName;
        DetectedSummaryTextBlock.Text = $"Detected Type: {detectedType} | Plugin: {pluginText} | Server Mod: {serverText}";
        PluginSummaryTextBlock.Text = $"Plugin: {pluginAction} | Old: {FormatVersion(pluginOldVersion)} | New: {FormatVersion(pluginNewVersion)}";
        ServerSummaryTextBlock.Text = $"Server Mod: {serverAction} | Old: {FormatVersion(serverOldVersion)} | New: {FormatVersion(serverNewVersion)}";

        PluginVersionPanel.Visibility = string.Equals(pluginAction, "None", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Collapsed
            : Visibility.Visible;
        PluginFlagsPanel.Visibility = PluginVersionPanel.Visibility;
        ServerVersionPanel.Visibility = string.Equals(serverAction, "None", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Collapsed
            : Visibility.Visible;

        PluginIncludeCheckBox.IsChecked = !string.Equals(pluginAction, "None", StringComparison.OrdinalIgnoreCase);
        ServerIncludeCheckBox.IsChecked = !string.Equals(serverAction, "None", StringComparison.OrdinalIgnoreCase);

        PluginVersionBox.Text = FormatVersionForInput(pluginNewVersion);
        ServerVersionBox.Text = FormatVersionForInput(serverNewVersion);
        PluginIsFolderModCheckBox.IsChecked = pluginIsFolderMod;
        PluginAllowOnHeadlessCheckBox.IsChecked = pluginAllowOnHeadless;
        PluginIsOptionalCheckBox.IsChecked = pluginIsOptional;
        PluginOptionalDefaultStateCheckBox.IsChecked = pluginOptionalDefaultState;

        if (hasConfigConflicts)
        {
            ConfigWarningBorder.Visibility = Visibility.Visible;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ContinueOverride_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCommitSelection())
            return;

        DialogResult = true;
        Close();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
        {
            item.SelectionChoice = string.Equals(item.SourceKind, "Old", StringComparison.OrdinalIgnoreCase)
                ? FileChangePreviewItem.ChoiceKeepExisting
                : FileChangePreviewItem.ChoiceKeepIncoming;
            item.IsIncluded = true;
        }

        FilesListView.Items.Refresh();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
        {
            item.SelectionChoice = FileChangePreviewItem.ChoiceKeepNone;
            item.IsIncluded = false;
        }

        FilesListView.Items.Refresh();
    }

    private bool TryCommitSelection()
    {
        IncludePluginUpdate = PluginVersionPanel.Visibility == Visibility.Visible && PluginIncludeCheckBox.IsChecked == true;
        IncludeServerUpdate = ServerVersionPanel.Visibility == Visibility.Visible && ServerIncludeCheckBox.IsChecked == true;
        SkipPluginUpdateOnSameVersion = false;
        SkipServerUpdateOnSameVersion = false;
        var skipPluginUpdate = false;
        var skipServerUpdate = false;

        var selectedCount = _items.Count(i => i.ShouldKeepAny);
        if (selectedCount == 0)
        {
            MessageBox.Show("Select at least one file to continue.", "No Files Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        foreach (var item in _items)
        {
            item.IsIncluded = item.ShouldKeepAny;
        }

        if (!IncludePluginUpdate && !IncludeServerUpdate)
        {
            MessageBox.Show("Select Include for the plugin and/or server mod before continuing.", "Nothing Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        ExcludedSourcePaths = _items
            .Where(i => !i.ShouldKeepIncoming && !string.IsNullOrWhiteSpace(i.SourceRelativePath))
            .Select(i => i.SourceRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        SelectedPluginVersion = IncludePluginUpdate
            ? NormalizeVersionInput(PluginVersionBox.Text)
            : null;
        SelectedServerVersion = IncludeServerUpdate
            ? NormalizeVersionInput(ServerVersionBox.Text)
            : null;

        if (IncludePluginUpdate && !string.Equals(_pluginAction, "None", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(SelectedPluginVersion))
        {
            MessageBox.Show(
                "Plugin version is required. The ZIP must contain the plugin DLL version, or enter a custom value.",
                "Plugin Version Missing",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        if (IncludeServerUpdate && !string.Equals(_serverAction, "None", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(SelectedServerVersion))
        {
            MessageBox.Show(
                "Server mod version is required. The ZIP must contain the server DLL version, or enter a custom value.",
                "Server Version Missing",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        SelectedPluginIsFolderMod = PluginIsFolderModCheckBox.IsChecked == true;
        SelectedPluginAllowOnHeadless = PluginAllowOnHeadlessCheckBox.IsChecked == true;
        SelectedPluginIsOptional = PluginIsOptionalCheckBox.IsChecked == true;
        SelectedPluginOptionalDefaultState = PluginOptionalDefaultStateCheckBox.IsChecked == true;

        if (IncludePluginUpdate
            && !ConfirmSameVersionIfNeeded("Plugin", _pluginAction, _pluginOldVersion, SelectedPluginVersion, out skipPluginUpdate))
            return false;
        SkipPluginUpdateOnSameVersion = skipPluginUpdate;

        if (IncludeServerUpdate
            && !ConfirmSameVersionIfNeeded("Server Mod", _serverAction, _serverOldVersion, SelectedServerVersion, out skipServerUpdate))
            return false;
        SkipServerUpdateOnSameVersion = skipServerUpdate;

        return true;
    }

    private static string FormatVersion(string? version)
    {
        return string.IsNullOrWhiteSpace(version) ? "-" : version;
    }

    private static string FormatVersionForInput(string? version)
    {
        return string.IsNullOrWhiteSpace(version) || string.Equals(version, "-", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : version;
    }

    private static string? NormalizeVersionInput(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static bool ConfirmSameVersionIfNeeded(string label, string action, string? oldVersion, string? newVersion, out bool skipSameVersionUpdate)
    {
        skipSameVersionUpdate = false;

        if (string.Equals(action, "None", StringComparison.OrdinalIgnoreCase))
            return true;

        var oldNorm = NormalizeVersionInput(oldVersion);
        var newNorm = NormalizeVersionInput(newVersion);
        if (string.IsNullOrWhiteSpace(oldNorm) || string.IsNullOrWhiteSpace(newNorm))
            return true;

        if (!string.Equals(oldNorm, newNorm, StringComparison.OrdinalIgnoreCase))
            return true;

        var result = MessageBox.Show(
            $"{label} new version is the same as old version ({oldNorm}).\nDo you want to update still?",
            "Same Version Detected",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        switch (result)
        {
            case MessageBoxResult.Yes:
                return true;
            case MessageBoxResult.No:
                skipSameVersionUpdate = true;
                return true;
            default:
                return false;
        }
    }

    private void ComboBoxItem_MouseEnter(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBoxItem item)
        {
            item.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3d, 0x3d, 0x40));
        }
    }

    private void ComboBoxItem_MouseLeave(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBoxItem item)
        {
            item.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2d, 0x2d, 0x30));
        }
    }
}
