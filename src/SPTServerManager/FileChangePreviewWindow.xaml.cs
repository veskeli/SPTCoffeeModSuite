using System.Windows;
using SPTServerManager.Models;

namespace SPTServerManager;

public partial class FileChangePreviewWindow : Window
{
    public bool KeepOldConfigFiles { get; private set; }
    public IReadOnlyCollection<string> ExcludedSourcePaths { get; private set; } = Array.Empty<string>();
    public string? SelectedPluginVersion { get; private set; }
    public string? SelectedServerVersion { get; private set; }

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
        KeepOldConfigFiles = false;

        ActionSummaryTextBlock.Text = actionSummary;

        var pluginText = string.IsNullOrWhiteSpace(detectedPluginName) ? "-" : detectedPluginName;
        var serverText = string.IsNullOrWhiteSpace(detectedServerModName) ? "-" : detectedServerModName;
        DetectedSummaryTextBlock.Text = $"Detected Type: {detectedType} | Plugin: {pluginText} | Server Mod: {serverText}";
        PluginSummaryTextBlock.Text = $"Plugin: {pluginAction} | Old: {FormatVersion(pluginOldVersion)} | New: {FormatVersion(pluginNewVersion)}";
        ServerSummaryTextBlock.Text = $"Server Mod: {serverAction} | Old: {FormatVersion(serverOldVersion)} | New: {FormatVersion(serverNewVersion)}";

        PluginVersionPanel.Visibility = string.Equals(pluginAction, "None", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Collapsed
            : Visibility.Visible;
        ServerVersionPanel.Visibility = string.Equals(serverAction, "None", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Collapsed
            : Visibility.Visible;

        PluginVersionBox.Text = FormatVersionForInput(pluginNewVersion);
        ServerVersionBox.Text = FormatVersionForInput(serverNewVersion);

        if (hasConfigConflicts)
        {
            ConfigWarningBorder.Visibility = Visibility.Visible;
            KeepOldConfigButton.Visibility = Visibility.Visible;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void KeepOldConfig_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCommitSelection())
            return;

        KeepOldConfigFiles = true;
        DialogResult = true;
        Close();
    }

    private void ContinueOverride_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCommitSelection())
            return;

        KeepOldConfigFiles = false;
        DialogResult = true;
        Close();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
        {
            item.IsIncluded = true;
        }

        FilesListView.Items.Refresh();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
        {
            item.IsIncluded = false;
        }

        FilesListView.Items.Refresh();
    }

    private bool TryCommitSelection()
    {
        var selectedCount = _items.Count(i => i.IsIncluded);
        if (selectedCount == 0)
        {
            MessageBox.Show("Select at least one file to continue.", "No Files Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        ExcludedSourcePaths = _items
            .Where(i => !i.IsIncluded && !string.IsNullOrWhiteSpace(i.SourceRelativePath))
            .Select(i => i.SourceRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        SelectedPluginVersion = PluginVersionPanel.Visibility == Visibility.Visible
            ? NormalizeVersionInput(PluginVersionBox.Text)
            : null;
        SelectedServerVersion = ServerVersionPanel.Visibility == Visibility.Visible
            ? NormalizeVersionInput(ServerVersionBox.Text)
            : null;

        if (!ConfirmSameVersionIfNeeded("Plugin", _pluginAction, _pluginOldVersion, SelectedPluginVersion))
            return false;

        if (!ConfirmSameVersionIfNeeded("Server Mod", _serverAction, _serverOldVersion, SelectedServerVersion))
            return false;

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

    private static bool ConfirmSameVersionIfNeeded(string label, string action, string? oldVersion, string? newVersion)
    {
        if (string.Equals(action, "None", StringComparison.OrdinalIgnoreCase))
            return true;

        var oldNorm = NormalizeVersionInput(oldVersion);
        var newNorm = NormalizeVersionInput(newVersion);
        if (string.IsNullOrWhiteSpace(oldNorm) || string.IsNullOrWhiteSpace(newNorm))
            return true;

        if (!string.Equals(oldNorm, newNorm, StringComparison.OrdinalIgnoreCase))
            return true;

        var result = MessageBox.Show(
            $"{label} new version is the same as old version ({oldNorm}).\nDo you want to continue anyway?",
            "Same Version Detected",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        return result == MessageBoxResult.Yes;
    }
}

