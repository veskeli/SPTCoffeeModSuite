using System.Windows;
using System.Windows.Controls;
using System.Collections.ObjectModel;
using SPTServerManager.Models;

namespace SPTServerManager;

public partial class FileChangePreviewWindow : Window
{
    public IReadOnlyCollection<string> ExcludedSourcePaths { get; private set; } = Array.Empty<string>();
    public string? SelectedPluginVersion { get; private set; }
    public string? SelectedServerVersion { get; private set; }
    public int? SelectedPluginRevision { get; private set; }
    public int? SelectedServerRevision { get; private set; }
    public bool IncludePluginUpdate { get; private set; }
    public bool IncludeServerUpdate { get; private set; }
    public bool SkipPluginUpdateOnSameVersion { get; private set; }
    public bool SkipServerUpdateOnSameVersion { get; private set; }
    public bool SelectedPluginIsFolderMod { get; private set; }
    public bool SelectedPluginAllowOnHeadless { get; private set; }
    public bool SelectedPluginIsOptional { get; private set; }
    public bool SelectedPluginOptionalDefaultState { get; private set; }

    private readonly List<FileChangePreviewItem> _items;
    private readonly List<FileChangePreviewItem> _allItems;
    private readonly ObservableCollection<FileChangePreviewItem> _displayItems;
    private readonly string _pluginAction;
    private readonly string? _pluginOldVersion;
    private readonly int _pluginOldRevision;
    private readonly string _serverAction;
    private readonly string? _serverOldVersion;
    private readonly string? _serverNewVersion;
    private readonly int _serverOldRevision;
    private readonly string? _detectedServerModName;

    public FileChangePreviewWindow(
        List<FileChangePreviewItem> items,
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
        int pluginOldRevision,
        string serverAction,
        string? serverOldVersion,
        string? serverNewVersion,
        int serverOldRevision)
    {
        InitializeComponent();

         _items = items;
         _allItems = new List<FileChangePreviewItem>(items);
         _displayItems = new ObservableCollection<FileChangePreviewItem>(items);
         _pluginAction = pluginAction;
         _pluginOldVersion = pluginOldVersion;
         _pluginOldRevision = Math.Max(0, pluginOldRevision);
         _serverAction = serverAction;
         _serverOldVersion = serverOldVersion;
          _serverNewVersion = serverNewVersion;
         _serverOldRevision = Math.Max(0, serverOldRevision);
         _detectedServerModName = detectedServerModName;
         FilesListView.ItemsSource = _displayItems;
         SyncLatestServerFilesButton.IsEnabled = !string.Equals(serverAction, "None", StringComparison.OrdinalIgnoreCase)
                                                 && !string.IsNullOrWhiteSpace(detectedServerModName);

        ActionSummaryTextBlock.Text = actionSummary;

         var pluginText = string.IsNullOrWhiteSpace(detectedPluginName) ? "-" : detectedPluginName;
         var serverText = string.IsNullOrWhiteSpace(detectedServerModName) ? "-" : detectedServerModName;
         DetectedSummaryTextBlock.Text = $"Detected Type: {detectedType} | Plugin: {pluginText} | Server Mod: {serverText}";

         UpdateServerVersionInfoBanner();

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
        PluginRevisionBox.Text = SuggestRevisionForInput(pluginAction, pluginOldVersion, pluginNewVersion, _pluginOldRevision);
        ServerRevisionBox.Text = SuggestRevisionForInput(serverAction, serverOldVersion, serverNewVersion, _serverOldRevision);
         PluginOldVersionTextBlock.Text = $"Version: {FormatVersion(pluginOldVersion)}";
         PluginOldRevisionTextBlock.Text = $"Revision: {FormatRevision(_pluginOldRevision)}";
         ServerOldVersionTextBlock.Text = $"Version: {FormatVersion(serverOldVersion)}";
         ServerOldRevisionTextBlock.Text = $"Revision: {FormatRevision(_serverOldRevision)}";
        PluginIsFolderModCheckBox.IsChecked = pluginIsFolderMod;
        PluginAllowOnHeadlessCheckBox.IsChecked = pluginAllowOnHeadless;
        PluginIsOptionalCheckBox.IsChecked = pluginIsOptional;
        PluginOptionalDefaultStateCheckBox.IsChecked = pluginOptionalDefaultState;

         if (hasConfigConflicts)
         {
             ConfigWarningBorder.Visibility = Visibility.Visible;
         }

         PluginIncludeCheckBox.Checked += IncludeCheckBox_CheckedChanged;
         PluginIncludeCheckBox.Unchecked += IncludeCheckBox_CheckedChanged;
         ServerIncludeCheckBox.Checked += IncludeCheckBox_CheckedChanged;
         ServerIncludeCheckBox.Unchecked += IncludeCheckBox_CheckedChanged;
     }

      private void UpdateListViewFilter()
      {
          if (_displayItems == null) return; // Guard against calls during initialization

          var includePlugin = PluginIncludeCheckBox.IsChecked == true;
          var includeServer = ServerIncludeCheckBox.IsChecked == true;

          var filteredItems = _allItems
              .Where(item =>
              {
                  if (string.Equals(item.FileType, "Plugin", StringComparison.OrdinalIgnoreCase))
                      return includePlugin;
                  if (string.Equals(item.FileType, "Server Mod", StringComparison.OrdinalIgnoreCase))
                      return includeServer;
                  return true; // Show items with other file types
              })
              .ToList();

          // Update the observable collection
          _displayItems.Clear();
          foreach (var item in filteredItems)
          {
              _displayItems.Add(item);
          }
      }

      private void UpdateServerVersionInfoBanner()
      {
          var localVersion = NormalizeVersionForDisplay(_serverOldVersion);
          var zipVersion = NormalizeVersionForDisplay(_serverNewVersion);

          if (!ShouldShowServerVersionInfo(localVersion, zipVersion))
          {
              ServerVersionInfoBorder.Visibility = Visibility.Collapsed;
              return;
          }

          ServerVersionInfoTextBlock.Text =
              $"Server folder and ZIP differ. Local server version: {localVersion} | ZIP version: {zipVersion}. " +
              "Use 'Sync Latest Server Files' to pull the current server folder into the preview if you need those changes.";
          ServerVersionInfoBorder.Visibility = Visibility.Visible;
      }

      private static bool ShouldShowServerVersionInfo(string localVersion, string zipVersion)
      {
          if (string.IsNullOrWhiteSpace(localVersion) && string.IsNullOrWhiteSpace(zipVersion))
              return false;

          return !string.Equals(localVersion, zipVersion, StringComparison.OrdinalIgnoreCase);
      }

      private static string NormalizeVersionForDisplay(string? value)
      {
          return string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
      }

      private void ContinueOverride_Click(object sender, RoutedEventArgs e)
      {
          if (!TryCommitSelection())
              return;

          DialogResult = true;
          Close();
      }

        private void IncludeCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
        {
            UpdateListViewFilter();
        }

      private void Cancel_Click(object sender, RoutedEventArgs e)
      {
          DialogResult = false;
          Close();
      }

       private void SelectAll_Click(object sender, RoutedEventArgs e)
     {
         foreach (var item in _displayItems)
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
         foreach (var item in _displayItems)
         {
             item.SelectionChoice = FileChangePreviewItem.ChoiceKeepNone;
             item.IsIncluded = false;
         }

         FilesListView.Items.Refresh();
     }

      private void SyncLatestServerFiles_Click(object sender, RoutedEventArgs e)
      {
          if (string.IsNullOrWhiteSpace(_detectedServerModName))
          {
              MessageBox.Show("No server mod was detected for this preview.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
              return;
          }

          if (Owner is not MainWindow mainWindow)
          {
              MessageBox.Show("Unable to access the server sync helper.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
              return;
          }

          if (!mainWindow.TrySyncLatestServerFilesIntoPreview(
                  _detectedServerModName,
                  _serverOldVersion,
                  _serverNewVersion,
                  _allItems,
                  out var syncedItems,
                  out var statusMessage))
          {
              if (!string.IsNullOrWhiteSpace(statusMessage))
              {
                  MessageBox.Show(statusMessage, "Sync Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
              }

              return;
          }

          ApplySyncedItems(syncedItems);

          if (!string.IsNullOrWhiteSpace(statusMessage))
          {
              MessageBox.Show(statusMessage, "Sync Complete", MessageBoxButton.OK, MessageBoxImage.Information);
          }
      }

      private void ApplySyncedItems(IReadOnlyCollection<FileChangePreviewItem> syncedItems)
      {
          _items.Clear();
          foreach (var item in syncedItems)
          {
              _items.Add(item);
          }

          _allItems.Clear();
          foreach (var item in syncedItems)
          {
              _allItems.Add(item);
          }

          UpdateListViewFilter();
      }

     private bool TryCommitSelection()
     {
         IncludePluginUpdate = PluginVersionPanel.Visibility == Visibility.Visible && PluginIncludeCheckBox.IsChecked == true;
         IncludeServerUpdate = ServerVersionPanel.Visibility == Visibility.Visible && ServerIncludeCheckBox.IsChecked == true;
         SkipPluginUpdateOnSameVersion = false;
         SkipServerUpdateOnSameVersion = false;
         var skipPluginUpdate = false;
         var skipServerUpdate = false;

         var selectedCount = _displayItems.Count(i => i.ShouldKeepAny);
         if (selectedCount == 0)
         {
             MessageBox.Show("Select at least one file to continue.", "No Files Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
             return false;
         }

         foreach (var item in _displayItems)
         {
             item.IsIncluded = item.ShouldKeepAny;
         }

         if (!IncludePluginUpdate && !IncludeServerUpdate)
         {
             MessageBox.Show("Select Include for the plugin and/or server mod before continuing.", "Nothing Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
             return false;
         }

         ExcludedSourcePaths = _displayItems
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
        SelectedPluginRevision = IncludePluginUpdate
            ? NormalizeRevisionInput(PluginRevisionBox.Text)
            : null;
        SelectedServerRevision = IncludeServerUpdate
            ? NormalizeRevisionInput(ServerRevisionBox.Text)
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

         if (IncludePluginUpdate && !string.Equals(_pluginAction, "None", StringComparison.OrdinalIgnoreCase)
             && SelectedPluginRevision is null or <= 0)
         {
             MessageBox.Show(
                 "Plugin revision is required and must be at least 1.",
                 "Plugin Revision Missing",
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

         if (IncludeServerUpdate && !string.Equals(_serverAction, "None", StringComparison.OrdinalIgnoreCase)
             && SelectedServerRevision is null or <= 0)
         {
             MessageBox.Show(
                 "Server mod revision is required and must be at least 1.",
                 "Server Mod Revision Missing",
                 MessageBoxButton.OK,
                 MessageBoxImage.Warning);
             return false;
         }

        SelectedPluginIsFolderMod = PluginIsFolderModCheckBox.IsChecked == true;
        SelectedPluginAllowOnHeadless = PluginAllowOnHeadlessCheckBox.IsChecked == true;
        SelectedPluginIsOptional = PluginIsOptionalCheckBox.IsChecked == true;
        SelectedPluginOptionalDefaultState = PluginOptionalDefaultStateCheckBox.IsChecked == true;

        if (IncludePluginUpdate
            && !ConfirmSameVersionIfNeeded("Plugin", _pluginAction, _pluginOldVersion, SelectedPluginVersion, _pluginOldRevision, SelectedPluginRevision, out skipPluginUpdate))
            return false;
        SkipPluginUpdateOnSameVersion = skipPluginUpdate;

        if (IncludeServerUpdate
            && !ConfirmSameVersionIfNeeded("Server Mod", _serverAction, _serverOldVersion, SelectedServerVersion, _serverOldRevision, SelectedServerRevision, out skipServerUpdate))
            return false;
        SkipServerUpdateOnSameVersion = skipServerUpdate;

        return true;
    }

     private static string FormatVersion(string? version)
     {
         return string.IsNullOrWhiteSpace(version) ? "-" : version;
     }

     private static string FormatRevision(int revision)
     {
         return revision <= 0 ? "none" : revision.ToString();
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

     private static int? NormalizeRevisionInput(string? value)
     {
         var trimmed = value?.Trim();
         if (string.IsNullOrWhiteSpace(trimmed))
             return null;

         if (!int.TryParse(trimmed, out var parsed))
             return null;

         // Treat 0 and negative numbers as "none" (null)
         return parsed > 0 ? parsed : null;
     }

     private static string SuggestRevisionForInput(string action, string? oldVersion, string? newVersion, int currentRevision)
     {
         // Default for adds/non-detected actions starts from revision 1.
         if (!string.Equals(action, "update", StringComparison.OrdinalIgnoreCase))
             return "1";

         var oldNorm = NormalizeVersionInput(oldVersion);
         var newNorm = NormalizeVersionInput(newVersion);

         // Same version update: bump revision; different version update: reset to 1.
         if (!string.IsNullOrWhiteSpace(oldNorm)
             && !string.IsNullOrWhiteSpace(newNorm)
             && string.Equals(oldNorm, newNorm, StringComparison.OrdinalIgnoreCase))
         {
             // If current revision is 0 or none, suggest 1; otherwise increment
             return currentRevision <= 0 ? "1" : (currentRevision + 1).ToString();
         }

         return "1";
     }

    private static bool ConfirmSameVersionIfNeeded(string label, string action, string? oldVersion, string? newVersion, int oldRevision, int? newRevision, out bool skipSameVersionUpdate)
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

        // Version matches — check if revision also matches (true duplicate)
        var effectiveOldRevision = oldRevision <= 0 ? (int?)null : oldRevision;
        var revisionsMatch = effectiveOldRevision == newRevision
                             || (effectiveOldRevision == null && (newRevision == null || newRevision <= 0));
        if (!revisionsMatch)
            return true; // Same version but different revision — allow without prompt

        var result = MessageBox.Show(
            $"{label} new version and revision are identical to the existing entry (v{oldNorm}, rev {FormatRevision(oldRevision)}).\nDo you want to add it anyway?",
            "True Duplicate Detected",
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
