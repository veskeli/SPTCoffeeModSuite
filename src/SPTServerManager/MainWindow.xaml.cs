using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.SignalR.Client;
using System.Windows.Media;
using System.Windows.Threading;
using SQLitePCL;
using SPTCoffee.Contracts.Models;
using SPTServerManager.Models;
using SharpCompress.Archives;
using SharpCompress.Common;
using MessageBox = System.Windows.MessageBox;

namespace SPTServerManager;

public partial class MainWindow : Window
{
    private ServerConfig Config { get; set; } = new();

    private readonly string? _configPath;
    private readonly string? _adminConfigPath;
    private readonly string? _launcherSettingsPath;
    private readonly string? _exeFolder;
    private string? _databasePath;

    private readonly DispatcherTimer _launcherButtonCooldownTimer = new();
    private readonly DispatcherTimer _sptServerButtonCooldownTimer = new();
    private readonly DispatcherTimer _headlessManagerButtonCooldownTimer = new();
    private readonly DispatcherTimer _playerPresenceTimer = new();

    private bool _isHeadlessWaitingToBeStarted = false;
    private bool _isHeadlessAutoStartInProgress = false;
    private bool _prevHeadlessRunning = false;
    private bool _prevSptServerRunning = false;
    private bool _headlessRestartNotified = false;
    private CancellationTokenSource? _headlessAutoStartCts;
    private bool _isPlayerPresenceRefreshInProgress = false;
    private PlayerPresenceSnapshot _lastPlayerPresenceSnapshot = new();
    private static readonly HttpClient PlayerPresenceHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };
    private static readonly HttpClient PluginDownloadHttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(3)
    };

    private HubConnection? _hubConnection;

    private readonly Dictionary<string, ModInfo> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServerModInfo> _pendingServerChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _serverConfigConflictDecisions = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _savedPreviewExcludedPaths;
    private bool _restartServersIfUpdateStartedOnline;
    private bool _isInitializingRestartCheckbox = true;

    private string BaseUrl => $"http://localhost:{Config.Port}";

    private readonly List<string> _excludedMods = new List<string>
    {
        "spt-common", "spt-core", "spt-custom", "spt-debugging",
        "spt-reflection", "spt-singleplayer", "Fika.Headless"
    };
    private readonly List<string> _excludedModFolders = new List<string>
    {
        "spt"
    };
    private readonly List<string> _excludedConfigs = new List<string>
    {
        "BepInEx.cfg", "com.bepis.bepinex.configurationmanager.cfg",
        "com.fika.headless.cfg"
    };

    private const string DefaultSptCoffeeDbFileName = "MainDatabase\\SPTCoffee.db";
    private const string PluginZipFolderName = "PluginZip";
    private const string AdditionalModsFolderName = "AdditionalMods";
    private const string MainDatabaseFolderName = "MainDatabase";
    private const string SptUpdateFolderName = "SptUpdate";
    private const string ConfigFilesFolderName = "ConfigFiles";
    private const string ServerModZipFolderName = "ServerModZips";
    private const string UserBackupsFolderName = "UserBackups";
    private const string PendingTempFolderName = "PendingChangesTemp";
    private const string ServerStateAddBoth = "add_both";
    private const string ServerStateUpdateBoth = "update_both";
    private const string ServerStateDeleteBoth = "delete_both";
    private const string ServerStateAddDb = "add_db";
    private const string ServerStateUpdateDb = "update_db";
    private const string ServerStateDeleteDb = "delete_db";
    private const string ServerStateAddLocal = "add_local";
    private const string ServerStateUpdateLocal = "update_local";
    private const string ServerStateDeleteLocal = "delete_local";
    private const string RestartServersAfterPendingUpdateSettingKey = "restart_servers_after_pending_update";
    private const string PreviewExcludedPathsSettingKey = "preview_excluded_paths";
    private const int HeadlessAutoStartMaxAttempts = 3;
    private const int HeadlessStartupValidationSeconds = 10;
    private const int SptReadyProbeIntervalSeconds = 1;
    private const int SptReadyTimeoutSeconds = 120;

    private static readonly Uri[] SptServerProbeUris =
    {
        new("https://127.0.0.1:6969/"),
        new("https://localhost:6969/"),
        new("http://127.0.0.1:6969/"),
        new("http://localhost:6969/")
    };

    public MainWindow()
    {
        try
        {
            var processModule = Process.GetCurrentProcess().MainModule;
            if (processModule != null)
            {
                var exePath = processModule.FileName;
                var exeDir = Path.GetDirectoryName(exePath)!;
                _exeFolder = exeDir;
                _configPath = Path.Combine(_exeFolder, "config.json");
                _adminConfigPath = Path.Combine(_exeFolder, "admins.json");
                _launcherSettingsPath = Path.Combine(_exeFolder, "LauncherConfig.json");
            }
        }
        catch
        {
            System.Windows.MessageBox.Show("Failed to determine executable path.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        InitializeComponent();
        Batteries_V2.Init();
        LoadConfig();
        EnsureStorageFolders();
        PopulateSettingsTabFromConfig();

        // Auto-start if the -AutoStartServers argument was supplied
        var args = Environment.GetCommandLineArgs();
        if (args.Any(a => string.Equals(a, "-AutoStartServers", StringComparison.OrdinalIgnoreCase)))
        {
            Loaded += (_, __) =>
            {
                // Use dispatcher to ensure UI is fully ready before starting servers
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    StartAllServers_Click(this, new RoutedEventArgs());
                }), DispatcherPriority.Background);
            };
        }

        // Start status update timer
        _statusTimer.Interval = TimeSpan.FromSeconds(1);
        _statusTimer.Tick += StatusTimer_Tick;
        _statusTimer.Start();
        // Initial status update
        StatusTimer_Tick(this, EventArgs.Empty);

        // Start player presence polling timer
        _playerPresenceTimer.Interval = TimeSpan.FromSeconds(3);
        _playerPresenceTimer.Tick += async (_, __) => await RefreshPlayerPresenceAsync();
        _playerPresenceTimer.Start();
        _ = RefreshPlayerPresenceAsync();

        // Load pending changes from database first so other views can reflect queued states.
        LoadPendingChangesFromDatabase();
        LoadServerPendingChangesFromDatabase();
        RefreshPendingChanges_Internal();

        // Load admin list
        RefreshAdminListView();

        // Load mod list
        RefreshModListView();

        // Load server mods list
        RefreshServerModsListView();

        // Load config list
        RefreshConfigListView();

        // Load installed plugins list
        RefreshInstalledPlugins_Click(this, new RoutedEventArgs());

        RestartServersCheckBox.IsChecked = _restartServersIfUpdateStartedOnline;
        _isInitializingRestartCheckbox = false;

        // Update/Create launcher config file
        try
        {
            if (_launcherSettingsPath != null)
            {
                // Create or update LauncherConfig.json
                var launcherConfig = new LauncherSettings()
                {
                    ExcludedMods = _excludedMods,
                    ExcludedModFolders = _excludedModFolders,
                    ExcludedConfigs = _excludedConfigs
                };
                // Write to file
                var json = JsonSerializer.Serialize(launcherConfig, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_launcherSettingsPath, json);
            }
        }
        catch (System.Exception ex)
        {
            System.Windows.MessageBox.Show("Failed to create/update launcher configuration: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task InitializeSignalR()
    {
        _hubConnection = new HubConnectionBuilder()
            .WithUrl($"{BaseUrl}/api/hub")
            .WithAutomaticReconnect()
            .Build();

        // On headless restarted
        _hubConnection.On<string>("HeadlessRestarted", (message) =>
        {
            Dispatcher.Invoke(() =>
            {
                Console.WriteLine("[SERVER NOTICE] " + message);
                _headlessRestartNotified = true;
            });
        });

        try
        {
            await _hubConnection.StartAsync();
            Dispatcher.Invoke(() => Console.WriteLine("Connected to server notifications"));
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => MessageBox.Show("SignalR connection failed:\n" + ex.Message));
        }
    }

    private void LoadConfig()
    {
        try
        {
            BootstrapConfig? bootstrap = null;
            ServerConfig? legacyConfig = null;

            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                bootstrap = JsonSerializer.Deserialize<BootstrapConfig>(json);
                legacyConfig = JsonSerializer.Deserialize<ServerConfig>(json);
            }

            Config.Port = bootstrap?.Port > 0 ? bootstrap.Port : (legacyConfig?.Port ?? Config.Port);
            _restartServersIfUpdateStartedOnline = bootstrap?.RestartServersIfUpdateStartedOnline ?? false;
            _databasePath = ResolveDatabasePath(bootstrap?.DatabaseFileName);

            if (_databasePath != null)
            {
                using var connection = new SqliteConnection($"Data Source={_databasePath}");
                connection.Open();
                EnsureSptCoffeeSchema(connection);
                MigratePluginsSchema(connection);

                if (legacyConfig != null)
                {
                    MigrateLegacySettingsToDatabase(connection, legacyConfig);
                }
                MigrateLegacyAdminsToDatabase(connection);

                var dbSettings = LoadManagerSettingsFromDatabase(connection);
                Config.SptServerFolder = dbSettings.SptServerFolder;
                Config.AdditionalModsPath = dbSettings.AdditionalModsPath;
                Config.HeadlessFolder = LoadHeadlessFolderFromDatabase(connection);
                Config.LocalHeadlessPlayerId = LoadLocalHeadlessPlayerIdFromDatabase(connection);
            }
        }
        catch (System.Exception ex)
        {
            System.Windows.MessageBox.Show("Failed to load configuration: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveConfig(ServerConfig? config)
    {
        try
        {
            var cfg = config ?? Config;

            var bootstrap = new BootstrapConfig
            {
                Port = cfg.Port,
                DatabaseFileName = GetDatabasePathForBootstrapSave(),
                RestartServersIfUpdateStartedOnline = _restartServersIfUpdateStartedOnline
            };
            var json = JsonSerializer.Serialize(bootstrap, new JsonSerializerOptions { WriteIndented = true });
            if (_configPath != null)
            {
                File.WriteAllText(_configPath, json);
            }

            if (_databasePath != null)
            {
                using var connection = new SqliteConnection($"Data Source={_databasePath}");
                connection.Open();
                EnsureSptCoffeeSchema(connection);
                SaveManagerSettingsToDatabase(connection, cfg);
            }
        }
        catch (System.Exception ex)
        {
            System.Windows.MessageBox.Show("Failed to save configuration: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartServer_Click(object sender, RoutedEventArgs e)
    {
        bool running = IsProcessRunningRegex(@"^SPTServerConsole$");

        if (running)
        {
            // Close it
            KillProcessRegex(@"^SPTServerConsole$");

            // Cooldown
            SetLauncherServerButtonAsStopping();
            return;
        }

        // Start
        if (_exeFolder != null)
        {
            var consolePath = Path.Combine(_exeFolder, "SPTServerConsole.exe");
            if (!File.Exists(consolePath))
            {
                MessageBox.Show("SPTServerConsole.exe not found!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = consolePath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(consolePath)
            };

            Process.Start(startInfo);

            // Cooldown
            SetLauncherServerButtonAsStarting();
        }
    }

    private async void UpdateMods_Click(object sender, RoutedEventArgs e)
    {
        var exeFolder = _exeFolder;
        if (exeFolder == null)
        {
            MessageBox.Show("Executable folder not determined.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        UpdateModsButtonState(false);

        try
        {
            string bepInExPath1 = Config.SptServerFolder;        // primary plugins
            string bepInExPath2 = Config.AdditionalModsPath; // new second folder

            var modsPath1 = Path.Combine(bepInExPath1, "BepInEx", "plugins");
            var modsPath2 = Path.Combine(bepInExPath2, "BepInEx", "plugins");

            List<string> validPaths = new();

            if (Directory.Exists(modsPath1))
                validPaths.Add(modsPath1);
            if (Directory.Exists(modsPath2))
                validPaths.Add(modsPath2);

            // Delete old zip folder if exists
            var oldZipFolder = GetPluginZipFolder(exeFolder);
            if (Directory.Exists(oldZipFolder))
            {
                Directory.Delete(oldZipFolder, true);
            }
            if (validPaths.Count == 0)
            {
                MessageBox.Show("No valid mod folders found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateModsButtonState(true);
                return;
            }

            var result = await Task.Run(() =>
            {
                int created = 0, updated = 0;
                var mergedMods = new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase);

                foreach (var path in validPaths)
                {
                    var r = ProcessModFolder(path, exeFolder);
                    created += r.created;
                    updated += r.updated;

                    foreach (var mod in r.mods)
                    {
                        mergedMods[mod.Name] = mod;
                    }
                }

                SavePluginsToDatabase(exeFolder, mergedMods.Values);

                return (created, updated);
            });

            MessageBox.Show($"Mods processed: {result.updated} updated, {result.created} created.",
                "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to update mods: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            UpdateModsButtonState(true);
        }
    }

    private (int created, int updated, List<ModInfo> mods) ProcessModFolder(string modsPath, string exeFolder)
    {
        var excludedMods = new HashSet<string>(_excludedMods, StringComparer.OrdinalIgnoreCase);
        var excludedModFolders = new HashSet<string>(_excludedModFolders, StringComparer.OrdinalIgnoreCase);

        string zipFolder = GetPluginZipFolder(exeFolder);
        Directory.CreateDirectory(zipFolder);

        List<ModInfo> pluginList = new();
        HashSet<string> processedMods = new(StringComparer.OrdinalIgnoreCase);

        int createdCount = 0;
        int updatedCount = 0;

        // Helper ZIP creator
        void CreateZip(string zipName, string sourcePath)
        {
            var zipPath = Path.Combine(zipFolder, zipName + ".zip");

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
                updatedCount++;
            }
            else
            {
                createdCount++;
            }

            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

            if (Directory.Exists(sourcePath))
            {
                foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
                {
                    var entryName = Path.GetRelativePath(sourcePath, file);
                    var fileName = Path.GetFileName(file);
                    var fileNameNoExt = Path.GetFileNameWithoutExtension(file);

                    // Skip excluded files
                    if (excludedMods.Contains(fileName))
                        continue;

                    // Skip excluded DLLs (by mod name)
                    if (string.Equals(Path.GetExtension(file), ".dll", StringComparison.OrdinalIgnoreCase)
                        && excludedMods.Contains(fileNameNoExt))
                        continue;

                    zip.CreateEntryFromFile(file, entryName);
                }
            }
            else if (File.Exists(sourcePath))
            {
                var fileName = Path.GetFileName(sourcePath);
                var fileNameNoExt = Path.GetFileNameWithoutExtension(sourcePath);

                if (excludedMods.Contains(fileName))
                    return;

                if (string.Equals(Path.GetExtension(sourcePath), ".dll", StringComparison.OrdinalIgnoreCase)
                    && excludedMods.Contains(fileNameNoExt))
                    return;

                zip.CreateEntryFromFile(sourcePath, Path.GetFileName(sourcePath));
            }
        }

        // 1. Folder mods
        foreach (var modFolder in Directory.GetDirectories(modsPath))
        {
            var folderName = Path.GetFileName(modFolder);
            if (excludedModFolders.Contains(folderName)) continue;

            var dllFiles = Directory.GetFiles(modFolder, "*.dll", SearchOption.AllDirectories);
            if (dllFiles.Length == 0) continue;

            var version = FileVersionInfo.GetVersionInfo(dllFiles[0]).FileVersion ?? "0";

            pluginList.Add(new ModInfo
            {
                Name = folderName,
                Version = version,
                FileName = folderName + ".zip",
                IsFolderMod = true
            });

            processedMods.Add(folderName);
            CreateZip(folderName, modFolder);
        }

        // 2. DLL mods
        foreach (var dll in Directory.GetFiles(modsPath, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var modName = Path.GetFileNameWithoutExtension(dll);
            if (excludedMods.Contains(modName) || processedMods.Contains(modName)) continue;

            var version = FileVersionInfo.GetVersionInfo(dll).FileVersion ?? "0";

            pluginList.Add(new ModInfo
            {
                Name = modName,
                Version = version,
                FileName = modName + ".zip",
                IsFolderMod = false
            });

            processedMods.Add(modName);
            CreateZip(modName, dll);
        }

        return (createdCount, updatedCount, pluginList);
    }

    private void SavePluginsToDatabase(string exeFolder, IEnumerable<ModInfo> mods)
    {
        var dbPath = _databasePath ?? ResolveDatabasePath(DefaultSptCoffeeDbFileName) ?? Path.Combine(exeFolder, DefaultSptCoffeeDbFileName);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        EnsureSptCoffeeSchema(connection);
        MigratePluginsSchema(connection);

        using var tx = connection.BeginTransaction();

        foreach (var mod in mods)
        {
            using var upsertCommand = connection.CreateCommand();
            upsertCommand.Transaction = tx;
            // UPSERT: on name conflict, update auto-detected fields but preserve manually-set flags
            upsertCommand.CommandText = @"
INSERT INTO plugins(name, version, file_name, is_folder_mod, allow_on_headless, is_optional, optional_default_state, updated_utc)
VALUES($name, $version, $fileName, $isFolderMod, 0, 0, 0, $updatedUtc)
ON CONFLICT(name) DO UPDATE SET
    version = excluded.version,
    file_name = excluded.file_name,
    is_folder_mod = excluded.is_folder_mod,
    updated_utc = excluded.updated_utc;";
            upsertCommand.Parameters.AddWithValue("$name", mod.Name);
            upsertCommand.Parameters.AddWithValue("$version", mod.Version);
            upsertCommand.Parameters.AddWithValue("$fileName", mod.FileName);
            upsertCommand.Parameters.AddWithValue("$isFolderMod", mod.IsFolderMod ? 1 : 0);
            upsertCommand.Parameters.AddWithValue("$updatedUtc", DateTime.UtcNow.ToString("O"));
            upsertCommand.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void EnsureSptCoffeeSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS plugins (
    name TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
    version TEXT NOT NULL,
    file_name TEXT NOT NULL,
    is_folder_mod INTEGER NOT NULL,
    allow_on_headless INTEGER NOT NULL DEFAULT 0,
    is_optional INTEGER NOT NULL DEFAULT 0,
    optional_default_state INTEGER NOT NULL DEFAULT 0,
    updated_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS configs (
    file_name TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
    last_modified_utc TEXT NOT NULL,
    is_enforced INTEGER NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS settings (
    key TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
    value TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS admins (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    note TEXT NOT NULL,
    secret TEXT NOT NULL COLLATE NOCASE UNIQUE,
    is_enabled INTEGER NOT NULL,
    allow_headless_close INTEGER NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS pending_changes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    mod_name TEXT NOT NULL COLLATE NOCASE,
    change_type TEXT NOT NULL,
    old_version TEXT,
    new_version TEXT,
    file_name TEXT NOT NULL DEFAULT '',
    is_folder_mod INTEGER NOT NULL DEFAULT 0,
    allow_on_headless INTEGER NOT NULL DEFAULT 0,
    is_optional INTEGER NOT NULL DEFAULT 0,
    optional_default_state INTEGER NOT NULL DEFAULT 0,
    created_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS server_plugins (
    name TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
    version TEXT NOT NULL,
    file_name TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS server_pending_changes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    mod_name TEXT NOT NULL COLLATE NOCASE,
    change_type TEXT NOT NULL,
    old_version TEXT NOT NULL DEFAULT '',
    new_version TEXT NOT NULL DEFAULT '',
    file_name TEXT NOT NULL DEFAULT '',
    created_utc TEXT NOT NULL
);";
        command.ExecuteNonQuery();
    }

    private static void MigratePendingChangesSchema(SqliteConnection connection)
    {
        var newColumns = new[]
        {
            ("file_name", "TEXT NOT NULL DEFAULT ''"),
            ("is_folder_mod", "INTEGER NOT NULL DEFAULT 0"),
            ("allow_on_headless", "INTEGER NOT NULL DEFAULT 0"),
            ("is_optional", "INTEGER NOT NULL DEFAULT 0"),
            ("optional_default_state", "INTEGER NOT NULL DEFAULT 0")
        };

        foreach (var (col, def) in newColumns)
        {
            try
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE pending_changes ADD COLUMN {col} {def};";
                alter.ExecuteNonQuery();
            }
            catch
            {
                // Column already exists — safe to ignore.
            }
        }
    }

    private static void MigratePluginsSchema(SqliteConnection connection)
    {
        // Safely add new columns if upgrading from an older schema
        var newColumns = new[]
        {
            ("allow_on_headless", "INTEGER NOT NULL DEFAULT 0"),
            ("is_optional", "INTEGER NOT NULL DEFAULT 0"),
            ("optional_default_state", "INTEGER NOT NULL DEFAULT 0"),
        };

        foreach (var (col, def) in newColumns)
        {
            try
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE plugins ADD COLUMN {col} {def};";
                alter.ExecuteNonQuery();
            }
            catch
            {
                // Column already exists — safe to ignore
            }
        }
    }

    private void UpdateModsButtonState(bool enabled)
    {
        if (enabled)
        {
            UpdateModsButton.IsEnabled = true;
            UpdateModsButton.Content = "Update Mods";
            UpdateModsButton.Background = Brushes.DarkOliveGreen;
        }
        else
        {
            UpdateModsButton.IsEnabled = false;
            UpdateModsButton.Content = "Updating Mods...";
            UpdateModsButton.Background = Brushes.Gray;
        }
    }

    private void UpdateConfigsButtonState(bool enabled)
    {
        if (enabled)
        {
            UpdateConfigsButton.IsEnabled = true;
            UpdateConfigsButton.Content = "Update Configs";
            UpdateConfigsButton.Background = Brushes.DarkOliveGreen;
        }
        else
        {
            UpdateConfigsButton.IsEnabled = false;
            UpdateConfigsButton.Content = "Updating Configs...";
            UpdateConfigsButton.Background = Brushes.Gray;
        }
    }

    private void OpenZipModFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_exeFolder != null)
        {
            var zipFolder = GetPluginZipFolder(_exeFolder);
            if (Directory.Exists(zipFolder))
            {
                Process.Start("explorer.exe", zipFolder);
            }
            else
            {
                System.Windows.MessageBox.Show("Zip mod folder does not exist! in " + zipFolder, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void UpdateConfigs_Click(object sender, RoutedEventArgs e)
    {
        if (_exeFolder == null)
        {
            MessageBox.Show("Executable folder not determined.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        UpdateConfigsButtonState(false);

        try
        {
            // Primary + secondary config folders
            string primaryPath = Path.Combine(Config.SptServerFolder, "BepInEx", "config");
            string secondaryPath = Path.Combine(Config.AdditionalModsPath, "BepInEx", "config");

            List<string> validPaths = new();

            if (Directory.Exists(primaryPath))
                validPaths.Add(primaryPath);

            if (Directory.Exists(secondaryPath))
                validPaths.Add(secondaryPath);

            if (validPaths.Count == 0)
            {
                MessageBox.Show("No valid config folders found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateConfigsButtonState(true);
                return;
            }

            int totalCreated = await Task.Run(() =>
            {
                int createdTotal = 0;
                var mergedConfigs = new Dictionary<string, ConfigInfo>(StringComparer.OrdinalIgnoreCase);

                foreach (var path in validPaths)
                {
                    var result = ProcessConfigFolder(path, _exeFolder);
                    createdTotal += result.copied;

                    foreach (var config in result.configs)
                    {
                        mergedConfigs[config.FileName] = config;
                    }
                }

                SaveConfigsToDatabase(_exeFolder, mergedConfigs.Values);

                return createdTotal;
            });

            MessageBox.Show($"Config files processed: {totalCreated} files copied.",
                "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to update configs: " + ex.Message,
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            UpdateConfigsButtonState(true);
        }
    }

    private (int copied, List<ConfigInfo> configs) ProcessConfigFolder(string configsPath, string exeFolder)
    {
        string configFolder = GetConfigFilesFolder(exeFolder);
        Directory.CreateDirectory(configFolder);

        var excludedConfigs = new HashSet<string>(_excludedConfigs, StringComparer.OrdinalIgnoreCase);
        var configList = new List<ConfigInfo>();

        int copiedCount = 0;

        // Process only *.cfg
        foreach (var configFile in Directory.GetFiles(configsPath, "*.cfg", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(configFile);
            if (excludedConfigs.Contains(fileName)) continue;

            var lastModified = File.GetLastWriteTimeUtc(configFile);

            configList.Add(new ConfigInfo
            {
                FileName = fileName,
                LastModified = lastModified
            });

            var destPath = Path.Combine(configFolder, fileName);
            File.Copy(configFile, destPath, true);
            copiedCount++;
        }

        return (copiedCount, configList);
    }

    private void SaveConfigsToDatabase(string exeFolder, IEnumerable<ConfigInfo> configs)
    {
        var dbPath = _databasePath ?? ResolveDatabasePath(DefaultSptCoffeeDbFileName) ?? Path.Combine(exeFolder, DefaultSptCoffeeDbFileName);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        EnsureSptCoffeeSchema(connection);

        var enforcedByFileName = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        using (var readCommand = connection.CreateCommand())
        {
            readCommand.CommandText = "SELECT file_name, is_enforced FROM configs;";
            using var reader = readCommand.ExecuteReader();
            while (reader.Read())
            {
                enforcedByFileName[reader.GetString(0)] = reader.GetInt32(1) == 1;
            }
        }

        using var tx = connection.BeginTransaction();

        using (var clearCommand = connection.CreateCommand())
        {
            clearCommand.Transaction = tx;
            clearCommand.CommandText = "DELETE FROM configs;";
            clearCommand.ExecuteNonQuery();
        }

        foreach (var config in configs)
        {
            var isEnforced = enforcedByFileName.TryGetValue(config.FileName, out var previousValue) && previousValue;

            using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = tx;
            insertCommand.CommandText = @"
INSERT INTO configs(file_name, last_modified_utc, is_enforced, updated_utc)
VALUES($fileName, $lastModifiedUtc, $isEnforced, $updatedUtc);";
            insertCommand.Parameters.AddWithValue("$fileName", config.FileName);
            insertCommand.Parameters.AddWithValue("$lastModifiedUtc", config.LastModified.ToUniversalTime().ToString("O"));
            insertCommand.Parameters.AddWithValue("$isEnforced", isEnforced ? 1 : 0);
            insertCommand.Parameters.AddWithValue("$updatedUtc", DateTime.UtcNow.ToString("O"));
            insertCommand.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private string? ResolveDatabasePath(string? dbFileNameOrPath)
    {
        if (_exeFolder == null)
        {
            return null;
        }

        var configured = string.IsNullOrWhiteSpace(dbFileNameOrPath) ? DefaultSptCoffeeDbFileName : dbFileNameOrPath;
        var dbPath = Path.IsPathRooted(configured) ? configured : Path.Combine(_exeFolder, configured);
        var dbDirectory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(dbDirectory))
        {
            Directory.CreateDirectory(dbDirectory);
        }

        return dbPath;
    }

    private string GetDatabasePathForBootstrapSave()
    {
        if (string.IsNullOrWhiteSpace(_databasePath) || string.IsNullOrWhiteSpace(_exeFolder))
        {
            return DefaultSptCoffeeDbFileName;
        }

        var relative = Path.GetRelativePath(_exeFolder, _databasePath);
        return relative.StartsWith("..") ? _databasePath : relative;
    }

    private void EnsureStorageFolders()
    {
        if (_exeFolder == null)
        {
            return;
        }

        Directory.CreateDirectory(GetPluginZipFolder(_exeFolder));
        Directory.CreateDirectory(GetAdditionalModsFolder(_exeFolder));
        Directory.CreateDirectory(GetMainDatabaseFolder(_exeFolder));
        Directory.CreateDirectory(GetSptUpdateFolder(_exeFolder));
        Directory.CreateDirectory(GetConfigFilesFolder(_exeFolder));
        Directory.CreateDirectory(GetServerModZipFolder(_exeFolder));
        Directory.CreateDirectory(GetUserBackupsFolder(_exeFolder));
        Directory.CreateDirectory(GetPendingClientFilesFolder(_exeFolder));
        Directory.CreateDirectory(GetPendingServerFilesFolder(_exeFolder));
    }

    private static string GetPluginZipFolder(string rootFolder) => Path.Combine(rootFolder, PluginZipFolderName);
    private static string GetAdditionalModsFolder(string rootFolder) => Path.Combine(rootFolder, AdditionalModsFolderName);
    private static string GetMainDatabaseFolder(string rootFolder) => Path.Combine(rootFolder, MainDatabaseFolderName);
    private static string GetSptUpdateFolder(string rootFolder) => Path.Combine(rootFolder, SptUpdateFolderName);
    private static string GetConfigFilesFolder(string rootFolder) => Path.Combine(rootFolder, ConfigFilesFolderName);
    private static string GetServerModZipFolder(string rootFolder) => Path.Combine(rootFolder, ServerModZipFolderName);
    private static string GetUserBackupsFolder(string rootFolder) => Path.Combine(rootFolder, UserBackupsFolderName);
    private static string GetPendingTempFolder(string rootFolder) => Path.Combine(rootFolder, PendingTempFolderName);
    private static string GetPendingClientFilesFolder(string rootFolder) => Path.Combine(GetPendingTempFolder(rootFolder), "ClientFiles");
    private static string GetPendingServerFilesFolder(string rootFolder) => Path.Combine(GetPendingTempFolder(rootFolder), "ServerFiles");

    private string StagePendingFile(string sourcePath, string pendingFolder, string? preferredFileName = null)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException($"Selected file not found: {sourcePath}", sourcePath);

        Directory.CreateDirectory(pendingFolder);

        var fileName = string.IsNullOrWhiteSpace(preferredFileName)
            ? Path.GetFileName(sourcePath)
            : preferredFileName!;

        var stagedPath = Path.Combine(pendingFolder, fileName);
        File.Copy(sourcePath, stagedPath, true);
        return fileName;
    }

    private string StageClientPendingFile(string sourcePath, string? preferredFileName = null)
    {
        if (_exeFolder == null)
            throw new InvalidOperationException("Executable folder not determined.");

        return StagePendingFile(sourcePath, GetPendingClientFilesFolder(_exeFolder), preferredFileName);
    }

    private string StageServerPendingZip(string sourceZipPath, string? preferredFileName = null)
    {
        if (_exeFolder == null)
            throw new InvalidOperationException("Executable folder not determined.");

        return StagePendingFile(sourceZipPath, GetPendingServerFilesFolder(_exeFolder), preferredFileName);
    }

    private string ResolvePendingServerZipPath(string fileName)
    {
        if (_exeFolder == null)
            throw new InvalidOperationException("Executable folder not determined.");

        var pendingPath = Path.Combine(GetPendingServerFilesFolder(_exeFolder), fileName);
        if (File.Exists(pendingPath))
            return pendingPath;

        var storagePath = Path.Combine(GetServerModZipFolder(_exeFolder), fileName);
        if (File.Exists(storagePath))
            return storagePath;

        throw new FileNotFoundException($"Server mod zip not found in pending temp or storage: {fileName}", fileName);
    }

    private void EnsureClientPendingFileInStorage(ModInfo mod)
    {
        if (_exeFolder == null)
            throw new InvalidOperationException("Executable folder not determined.");

        if (string.IsNullOrWhiteSpace(mod.FileName))
            return;

        var storageFolder = GetPluginZipFolder(_exeFolder);
        Directory.CreateDirectory(storageFolder);

        var destinationPath = Path.Combine(storageFolder, mod.FileName);
        if (File.Exists(destinationPath))
            return;

        var stagedPath = Path.Combine(GetPendingClientFilesFolder(_exeFolder), mod.FileName);
        if (!File.Exists(stagedPath))
            throw new FileNotFoundException($"Pending client mod file not found: {stagedPath}", stagedPath);

        File.Copy(stagedPath, destinationPath, true);
    }

    private void RemoveStagedPendingFile(string? fileName, bool server)
    {
        if (_exeFolder == null || string.IsNullOrWhiteSpace(fileName))
            return;

        var folder = server ? GetPendingServerFilesFolder(_exeFolder) : GetPendingClientFilesFolder(_exeFolder);
        var fullPath = Path.Combine(folder, fileName);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
    }

    private void ClearPendingTempStorage()
    {
        if (_exeFolder == null)
            return;

        var pendingRoot = GetPendingTempFolder(_exeFolder);
        if (Directory.Exists(pendingRoot))
            Directory.Delete(pendingRoot, true);

        Directory.CreateDirectory(GetPendingClientFilesFolder(_exeFolder));
        Directory.CreateDirectory(GetPendingServerFilesFolder(_exeFolder));
    }

    private string? BackupUserProfilesBeforeApplyChanges()
    {
        if (_exeFolder == null)
            throw new InvalidOperationException("Executable folder not determined.");

        var profilesRoot = Path.Combine(Config.SptServerFolder, "SPT", "user", "profiles");
        if (!Directory.Exists(profilesRoot))
            return null;

        var backupsRoot = GetUserBackupsFolder(_exeFolder);
        Directory.CreateDirectory(backupsRoot);

        var folderName = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var backupFolder = Path.Combine(backupsRoot, folderName);
        var suffix = 1;
        while (Directory.Exists(backupFolder))
        {
            backupFolder = Path.Combine(backupsRoot, $"{folderName}_{suffix}");
            suffix++;
        }

        var targetProfilesFolder = Path.Combine(backupFolder, "profiles");
        Directory.CreateDirectory(targetProfilesFolder);

        foreach (var sourceFile in Directory.GetFiles(profilesRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(profilesRoot, sourceFile);
            if (relativePath.StartsWith("backups\\", StringComparison.OrdinalIgnoreCase)
                || relativePath.StartsWith("backups/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destinationFile = Path.Combine(targetProfilesFolder, relativePath);
            var destinationDir = Path.GetDirectoryName(destinationFile);
            if (!string.IsNullOrWhiteSpace(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            File.Copy(sourceFile, destinationFile, true);
        }

        return backupFolder;
    }

    private static void SaveManagerSettingsToDatabase(SqliteConnection connection, ServerConfig config)
    {
        UpsertSetting(connection, "spt_server_folder", config.SptServerFolder);
        UpsertSetting(connection, "additional_mods_path", config.AdditionalModsPath);
        UpsertSetting(connection, "headless_folder", config.HeadlessFolder);
        UpsertSetting(connection, "local_headless_player_id", config.LocalHeadlessPlayerId);
    }

    private static void MigrateLegacySettingsToDatabase(SqliteConnection connection, ServerConfig legacy)
    {
        if (!string.IsNullOrWhiteSpace(GetSetting(connection, "spt_server_folder")))
        {
            return;
        }

        SaveManagerSettingsToDatabase(connection, legacy);
    }

    private void MigrateLegacyAdminsToDatabase(SqliteConnection connection)
    {
        if (LoadAdminsFromDatabase(connection).Count > 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_adminConfigPath) || !File.Exists(_adminConfigPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_adminConfigPath);
            var admins = JsonSerializer.Deserialize<List<AdminConfig>>(json) ?? new List<AdminConfig>();
            SaveAdminsToDatabase(connection, admins);
        }
        catch
        {
            // Keep startup resilient if legacy admin migration fails.
        }
    }

    private static List<AdminConfig> LoadAdminsFromDatabase(SqliteConnection connection)
    {
        var admins = new List<AdminConfig>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT note, secret, is_enabled, allow_headless_close FROM admins ORDER BY note COLLATE NOCASE;";
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            admins.Add(new AdminConfig
            {
                Note = reader.GetString(0),
                Secret = reader.GetString(1),
                IsEnabled = reader.GetInt32(2) == 1,
                AllowHeadlessClose = reader.GetInt32(3) == 1
            });
        }

        return admins;
    }

    private static void SaveAdminsToDatabase(SqliteConnection connection, IEnumerable<AdminConfig> admins)
    {
        using var tx = connection.BeginTransaction();

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM admins;";
            clear.ExecuteNonQuery();
        }

        foreach (var admin in admins)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = @"
INSERT INTO admins(note, secret, is_enabled, allow_headless_close, updated_utc)
VALUES($note, $secret, $isEnabled, $allowHeadlessClose, $updatedUtc);";
            insert.Parameters.AddWithValue("$note", admin.Note ?? string.Empty);
            insert.Parameters.AddWithValue("$secret", admin.Secret ?? string.Empty);
            insert.Parameters.AddWithValue("$isEnabled", admin.IsEnabled ? 1 : 0);
            insert.Parameters.AddWithValue("$allowHeadlessClose", admin.AllowHeadlessClose ? 1 : 0);
            insert.Parameters.AddWithValue("$updatedUtc", DateTime.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static (string SptServerFolder, string AdditionalModsPath) LoadManagerSettingsFromDatabase(SqliteConnection connection)
    {
        var sptServerFolder = GetSetting(connection, "spt_server_folder");
        var additionalModsPath = GetSetting(connection, "additional_mods_path");

        return (
            string.IsNullOrWhiteSpace(sptServerFolder) ? @"C:\SPT" : sptServerFolder,
            additionalModsPath ?? string.Empty
        );
    }

    private static string LoadHeadlessFolderFromDatabase(SqliteConnection connection)
    {
        return GetSetting(connection, "headless_folder") ?? string.Empty;
    }

    private static string LoadLocalHeadlessPlayerIdFromDatabase(SqliteConnection connection)
    {
        return GetSetting(connection, "local_headless_player_id") ?? string.Empty;
    }

    private static string? GetSetting(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void UpsertSetting(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO settings(key, value, updated_utc)
VALUES($key, $value, $updatedUtc)
ON CONFLICT(key) DO UPDATE SET
    value = excluded.value,
    updated_utc = excluded.updated_utc;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value ?? string.Empty);
        command.Parameters.AddWithValue("$updatedUtc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }


    private void PopulateSettingsTabFromConfig()
    {
        SettingsLauncherPortTextBox.Text = Config.Port.ToString();
        SettingsServerFolderTextBox.Text = Config.SptServerFolder;
        SettingsAdditionalModsTextBox.Text = Config.AdditionalModsPath;
        SettingsHeadlessFolderTextBox.Text = Config.HeadlessFolder;
        SettingsLocalHeadlessPlayerIdTextBox.Text = Config.LocalHeadlessPlayerId;
    }

    private void SaveSettingsTab_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SettingsLauncherPortTextBox.Text, out var port) || port <= 0 || port > 65535)
        {
            MessageBox.Show("Please enter a valid port number between 1 and 65535.", "Invalid Port", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Config.Port = port;
        Config.SptServerFolder = SettingsServerFolderTextBox.Text?.Trim() ?? string.Empty;
        Config.AdditionalModsPath = SettingsAdditionalModsTextBox.Text?.Trim() ?? string.Empty;
        Config.HeadlessFolder = SettingsHeadlessFolderTextBox.Text?.Trim() ?? string.Empty;
        Config.LocalHeadlessPlayerId = SettingsLocalHeadlessPlayerIdTextBox.Text?.Trim() ?? string.Empty;

        SaveConfig(Config);
        PopulateSettingsTabFromConfig();
        MessageBox.Show("Settings saved.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BrowseSettingsServerFolder_Click(object sender, RoutedEventArgs e)
    {
        using var folderDialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select SPT Server Folder"
        };

        if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            SettingsServerFolderTextBox.Text = folderDialog.SelectedPath;
        }
    }

    private void BrowseSettingsAdditionalModsFolder_Click(object sender, RoutedEventArgs e)
    {
        using var folderDialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select Additional Mods Folder"
        };

        if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            SettingsAdditionalModsTextBox.Text = folderDialog.SelectedPath;
        }
    }

    private void OpenSettingsServerFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderFromText(SettingsServerFolderTextBox.Text, "Server folder path does not exist!");
    }

    private void OpenSettingsAdditionalModsFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderFromText(SettingsAdditionalModsTextBox.Text, "Additional mods path does not exist!");
    }

    private void BrowseSettingsHeadlessFolder_Click(object sender, RoutedEventArgs e)
    {
        using var folderDialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select Headless Client Root Folder"
        };

        if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            SettingsHeadlessFolderTextBox.Text = folderDialog.SelectedPath;
        }
    }

    private void OpenSettingsHeadlessFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderFromText(SettingsHeadlessFolderTextBox.Text, "Headless folder path does not exist!");
    }

    private static void OpenFolderFromText(string? folderPath, string notFoundMessage)
    {
        if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
        {
            Process.Start("explorer.exe", folderPath);
            return;
        }

        MessageBox.Show(notFoundMessage, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private readonly DispatcherTimer _statusTimer = new();

    private void StatusTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            // Run server checks off the UI thread
            bool launcherRunning = IsProcessRunningRegex(@"^SPTServerConsole$");
            bool sptServerRunning = IsProcessRunningRegex(@"^SPT\.Server$");
            bool headlessManagerRunning = IsProcessRunningRegex(@"^FikaHeadlessManager$");
            bool headlessClientRunning = IsProcessRunningRegex(@"^EscapeFromTarkov$");

            // Update UI labels
            ServerStatusText.Text = launcherRunning ? "Running" : "Stopped";
            ServerStatusText.Foreground = launcherRunning ? Brushes.LimeGreen : Brushes.DarkRed;

            SptServerStatusText.Text = sptServerRunning ? "Running" : "Stopped";
            SptServerStatusText.Foreground = sptServerRunning ? Brushes.LimeGreen : Brushes.DarkRed;

            SptHeadlessManagerStatusText.Text = headlessManagerRunning ? "Running" : "Stopped";
            SptHeadlessManagerStatusText.Foreground = headlessManagerRunning ? Brushes.LimeGreen : Brushes.DarkRed;

            SptHeadlessStatusText.Text = headlessClientRunning ? "Running" : "Stopped";
            SptHeadlessStatusText.Foreground = headlessClientRunning ? Brushes.LimeGreen : Brushes.DarkRed;

            // Update buttons color and text based on status
            StartLauncherServerButton.Content = launcherRunning ? "Close Launcher Server" : "Start Launcher Server";
            StartLauncherServerButton.Background = launcherRunning ? Brushes.DarkRed : Brushes.Green;
            StartSptServerButton.Content = sptServerRunning ? "Close SPT Server" : "Start SPT Server";
            StartSptServerButton.Background = sptServerRunning ? Brushes.DarkRed : Brushes.Green;
            if (!_isHeadlessWaitingToBeStarted)
            {
                StartSptHeadlessManagerButton.Content = headlessManagerRunning ? "Close Headless Manager" : "Start Headless Manager";
                StartSptHeadlessManagerButton.Background = headlessManagerRunning ? Brushes.DarkRed : Brushes.Green;
            }

            // Update close headless (as the manager opens it, we only enable closing when its actually running)
            CloseSptHeadlessButton.IsEnabled = headlessClientRunning;
            CloseSptHeadlessButton.Background = headlessClientRunning ? Brushes.DarkRed : Brushes.Gray;

            // Update pending changes button state
            UpdatePendingChangesButtonState();

            // If disable timers are running, stop them and re-enable buttons
            if (_launcherButtonCooldownTimer.IsEnabled)
            {
                _launcherButtonCooldownTimer.Stop();
                StartLauncherServerButton.IsEnabled = true;
            }
            if (_sptServerButtonCooldownTimer.IsEnabled)
            {
                _sptServerButtonCooldownTimer.Stop();
                StartSptServerButton.IsEnabled = true;
            }
            if (_headlessManagerButtonCooldownTimer.IsEnabled && !_isHeadlessWaitingToBeStarted)
            {
                _headlessManagerButtonCooldownTimer.Stop();
                StartSptHeadlessManagerButton.IsEnabled = true;
            }

            // Network notification
            if (sptServerRunning != _prevSptServerRunning)
            {
                _prevSptServerRunning = sptServerRunning;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var client = new HttpClient();
                        var endpoint = sptServerRunning ? "online" : "offline";
                        var url = $"http://localhost:{Config.Port}/api/events/spt/{endpoint}";
                        await client.PostAsync(url, null);
                    }
                    catch
                    {
                        // ignore network errors
                    }
                });
            }

            if (!sptServerRunning)
            {
                UpdatePlayerPresenceView(new PlayerPresenceSnapshot
                {
                    LastUpdatedUtc = DateTime.UtcNow,
                    IsServerRunning = false,
                    StatusMessage = "SPT.Server is stopped. All players are disconnected.",
                    LogFilePath = string.Empty,
                    ActivePlayers = [],
                    RecentEvents = [],
                    ActiveCount = 0
                });
            }
            // Headless network notification
            if ((headlessClientRunning != _prevHeadlessRunning && !_headlessRestartNotified)
                || _isHeadlessWaitingToBeStarted
                || (_headlessRestartNotified && headlessClientRunning))
            {
                _prevHeadlessRunning = headlessClientRunning;

                if (_isHeadlessWaitingToBeStarted)
                {
                    _headlessRestartNotified = false; // Server overrides waiting state
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var client = new HttpClient();
                        var endpoint = _isHeadlessWaitingToBeStarted ? "restart" : (headlessClientRunning ? "online" : "offline");
                        var url = $"http://localhost:{Config.Port}/api/events/headless/{endpoint}";
                        await client.PostAsync(url, null);
                    }
                    catch
                    {
                        // ignore network errors
                    }
                });
            }
        }
        catch { /* Ignore exceptions in status update */ }
    }

    private async Task RefreshPlayerPresenceAsync()
    {
        if (_isPlayerPresenceRefreshInProgress)
        {
            return;
        }

        _isPlayerPresenceRefreshInProgress = true;

        try
        {
            var response = await PlayerPresenceHttpClient.GetAsync($"{BaseUrl}/api/spt/players");
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                UpdatePlayerPresenceView(new PlayerPresenceSnapshot
                {
                    LastUpdatedUtc = DateTime.UtcNow,
                    Error = $"Player presence request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                });
                return;
            }

            var snapshot = JsonSerializer.Deserialize<PlayerPresenceSnapshot>(json) ?? new PlayerPresenceSnapshot();
            UpdatePlayerPresenceView(snapshot);
        }
        catch (Exception ex)
        {
            UpdatePlayerPresenceView(new PlayerPresenceSnapshot
            {
                LastUpdatedUtc = DateTime.UtcNow,
                Error = $"Player presence unavailable: {ex.Message}"
            });
        }
        finally
        {
            _isPlayerPresenceRefreshInProgress = false;
        }
    }

    private void UpdatePlayerPresenceView(PlayerPresenceSnapshot snapshot)
    {
        _lastPlayerPresenceSnapshot = snapshot;

        if (PlayerPresenceCountText == null || PlayerPresenceListView == null || PlayerPresenceStatusText == null)
        {
            return;
        }

        PlayerPresenceCountText.Text = snapshot.ActiveCount.ToString();
        PlayerPresenceLastUpdatedText.Text = snapshot.LastUpdatedUtc == default
            ? "Not updated"
            : snapshot.LastUpdatedUtc.ToLocalTime().ToString("g");
        PlayerPresenceLogFileText.Text = string.IsNullOrWhiteSpace(snapshot.LogFilePath)
            ? "No log file"
            : snapshot.LogFilePath;

        if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            PlayerPresenceStatusText.Text = snapshot.Error;
            PlayerPresenceStatusText.Foreground = Brushes.Tomato;
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.StatusMessage))
        {
            PlayerPresenceStatusText.Text = snapshot.StatusMessage;
            PlayerPresenceStatusText.Foreground = snapshot.IsServerRunning ? Brushes.LimeGreen : Brushes.Gold;
        }
        else
        {
            PlayerPresenceStatusText.Text = $"Tracking {snapshot.ActiveCount} player(s).";
            PlayerPresenceStatusText.Foreground = Brushes.LimeGreen;
        }

        PlayerPresenceListView.ItemsSource = snapshot.ActivePlayers;
        PlayerPresenceEventsListView.ItemsSource = snapshot.RecentEvents;

        // Update raid status
        if (RaidStatusText != null)
        {
            RaidStatusText.Text = snapshot.CurrentRaidType switch
            {
                "Solo" => "Solo Raid",
                "Headless" => "Headless Raid",
                "Mixed" => "Mixed Raids",
                _ => "No Raid"
            };
            RaidStatusText.Foreground = snapshot.CurrentRaidType switch
            {
                "Solo" => Brushes.LimeGreen,
                "Headless" => Brushes.DodgerBlue,
                "Mixed" => Brushes.MediumPurple,
                _ => Brushes.Gray
            };
        }

        if (HeadlessStatusText != null)
        {
            HeadlessStatusText.Text = string.IsNullOrWhiteSpace(snapshot.HeadlessStatus)
                ? "Disconnected"
                : snapshot.HeadlessStatus;
            HeadlessStatusText.Foreground = snapshot.HeadlessStatus switch
            {
                "Hosting Raid" => Brushes.DodgerBlue,
                "Starting Raid" => Brushes.Gold,
                "Waiting for Raid" => Brushes.LimeGreen,
                "Restarting for New Raid" => Brushes.Orange,
                "Connected" => Brushes.LimeGreen,
                "In Headless Raid" => Brushes.DeepSkyBlue,
                "Disconnected or Joining" => Brushes.Gold,
                "Disconnected after Headless Raid" => Brushes.MediumPurple,
                "Disconnected" => Brushes.Tomato,
                _ => Brushes.Gray
            };
        }

        if (HeadlessLocationText != null)
        {
            HeadlessLocationText.Text = string.IsNullOrWhiteSpace(snapshot.HeadlessLocation)
                ? "Unknown"
                : snapshot.HeadlessLocation;
        }

        if (HeadlessRaidPlayersListView != null)
        {
            HeadlessRaidPlayersListView.ItemsSource = snapshot.HeadlessRaidPlayers;
        }
    }

    private async void RefreshPlayerPresence_Click(object sender, RoutedEventArgs e)
    {
        await RefreshPlayerPresenceAsync();
    }

    private void LauncherButtonCooldownTimer_Tick(object? sender, EventArgs e)
    {
        StartLauncherServerButton.IsEnabled = true;
        _launcherButtonCooldownTimer.Stop();
    }

    private void SptServerButtonCooldownTimer_Tick(object? sender, EventArgs e)
    {
        StartSptServerButton.IsEnabled = true;
        _sptServerButtonCooldownTimer.Stop();
    }

    private void HeadlessManagerButtonCooldownTimer_Tick(object? sender, EventArgs e)
    {
        StartSptHeadlessManagerButton.IsEnabled = true;
        _headlessManagerButtonCooldownTimer.Stop();
    }

    private bool IsProcessRunningRegex(string pattern)
    {
        var regex = new Regex(pattern, RegexOptions.IgnoreCase);

        return Process
            .GetProcesses()
            .Any(p =>
            {
                try
                {
                    return regex.IsMatch(p.ProcessName);
                }
                catch
                {
                    return false; // ignore protected system processes
                }
            });
    }

    private void StartSptServer_Click(object sender, RoutedEventArgs e)
    {
        bool running = IsProcessRunningRegex(@"^SPT\.Server$");

        if (running)
        {
            KillProcessRegex(@"^SPT\.Server$");

            // Cooldown
            SetSptServerButtonAsStopping();

            // Send network notification and update status
            _ = Task.Run(async () =>
            {
                try
                {
                    using var client = new HttpClient();
                    var url = $"{BaseUrl}/api/events/spt/offline";
                    await client.PostAsync(url, null);
                }
                catch
                {
                    // ignore network errors
                }
            });
            _prevSptServerRunning = false;
            return;
        }

        var sptFolder = Path.Combine(Config.SptServerFolder, "SPT");
        if (!Directory.Exists(sptFolder))
        {
            MessageBox.Show("SPT server folder does not exist: " + sptFolder, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var sptExePath = Path.Combine(sptFolder, "SPT.Server.exe");
        if (!File.Exists(sptExePath))
        {
            MessageBox.Show("SPTServer.exe not found in: " + sptExePath, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = sptExePath,
            UseShellExecute = true,
            WorkingDirectory = sptFolder
        };

        Process.Start(startInfo);

        // Cooldown
        SetSptServerButtonAsStarting();
    }

    private void StartSptHeadlessManager_Click(object sender, RoutedEventArgs e)
    {
        bool running = IsProcessRunningRegex(@"^FikaHeadlessManager$");

        if (running)
        {
            KillProcessRegex(@"^FikaHeadlessManager$");

            // Cooldown
            SetHeadlessManagerButtonAsStopping();
            return;
        }

        var headlessManagerPath = Path.Combine(Config.SptServerFolder, "FikaHeadlessManager.exe");
        if (!File.Exists(headlessManagerPath))
        {
            MessageBox.Show("FikaHeadlessManager.exe not found: " + headlessManagerPath, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = headlessManagerPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(headlessManagerPath)
        };

        Process.Start(startInfo);

        // Cooldown
        SetHeadlessManagerButtonAsStarting();
    }

    private void KillProcessRegex(string pattern)
    {
        var regex = new Regex(pattern, RegexOptions.IgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (regex.IsMatch(p.ProcessName))
                    p.Kill();
            }
            catch { /* ignore */ }
        }
    }

    private void CloseSptHeadless_Click(object sender, RoutedEventArgs e)
    {
        KillProcessRegex(@"^EscapeFromTarkov$");

        // Send network notification and update status
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new HttpClient();
                var url = $"{BaseUrl}/api/events/headless/offline";
                await client.PostAsync(url, null);
            }
            catch
            {
                // ignore network errors
            }
        });
        _prevHeadlessRunning = false;
    }

    private void StartAllServers_Click(object sender, RoutedEventArgs e)
    {
        // Start launcher if not already running
        if (!IsProcessRunningRegex(@"^SPTServerConsole$"))
        {
            StartServer_Click(sender, e);
            SetLauncherServerButtonAsStarting();
        }

        // Start SPT server if not already running
        if (!IsProcessRunningRegex(@"^SPT\.Server$"))
        {
            StartSptServer_Click(sender, e);
            SetSptServerButtonAsStarting();
        }

        // Start headless manager with delay only if not already running
        if (!IsProcessRunningRegex(@"^FikaHeadlessManager$"))
        {
            StartHeadlessWithDelay_Click(sender, e);
        }
    }

    private void SetLauncherServerButtonAsStarting()
    {
        StartLauncherServerButton.IsEnabled = false;
        StartLauncherServerButton.Content = "Starting...";
        StartLauncherServerButton.Background = Brushes.Orange;

        _launcherButtonCooldownTimer.Interval = TimeSpan.FromSeconds(5);
        _launcherButtonCooldownTimer.Tick += LauncherButtonCooldownTimer_Tick;
        _launcherButtonCooldownTimer.Start();
    }

    private void SetLauncherServerButtonAsStopping()
    {
        StartLauncherServerButton.IsEnabled = false;
        StartLauncherServerButton.Content = "Stopping...";
        StartLauncherServerButton.Background = Brushes.Orange;

        _launcherButtonCooldownTimer.Interval = TimeSpan.FromSeconds(5);
        _launcherButtonCooldownTimer.Tick += LauncherButtonCooldownTimer_Tick;
        _launcherButtonCooldownTimer.Start();
    }

    private void SetSptServerButtonAsStarting()
    {
        StartSptServerButton.IsEnabled = false;
        StartSptServerButton.Content = "Starting...";
        StartSptServerButton.Background = Brushes.Orange;

        _sptServerButtonCooldownTimer.Interval = TimeSpan.FromSeconds(5);
        _sptServerButtonCooldownTimer.Tick += SptServerButtonCooldownTimer_Tick;
        _sptServerButtonCooldownTimer.Start();
    }

    private void SetSptServerButtonAsStopping()
    {
        StartSptServerButton.IsEnabled = false;
        StartSptServerButton.Content = "Stopping...";
        StartSptServerButton.Background = Brushes.Orange;

        _sptServerButtonCooldownTimer.Interval = TimeSpan.FromSeconds(5);
        _sptServerButtonCooldownTimer.Tick += SptServerButtonCooldownTimer_Tick;
        _sptServerButtonCooldownTimer.Start();
    }

    private void SetHeadlessManagerButtonAsStarting()
    {
        StartSptHeadlessManagerButton.IsEnabled = false;
        StartSptHeadlessManagerButton.Content = "Starting...";
        StartSptHeadlessManagerButton.Background = Brushes.Orange;

        _headlessManagerButtonCooldownTimer.Interval = TimeSpan.FromSeconds(30);
        _headlessManagerButtonCooldownTimer.Tick += HeadlessManagerButtonCooldownTimer_Tick;
        _headlessManagerButtonCooldownTimer.Start();
    }

    private void SetHeadlessManagerButtonAsStopping()
    {
        StartSptHeadlessManagerButton.IsEnabled = false;
        StartSptHeadlessManagerButton.Content = "Stopping...";
        StartSptHeadlessManagerButton.Background = Brushes.Orange;

        _headlessManagerButtonCooldownTimer.Interval = TimeSpan.FromSeconds(5);
        _headlessManagerButtonCooldownTimer.Tick += HeadlessManagerButtonCooldownTimer_Tick;
        _headlessManagerButtonCooldownTimer.Start();
    }

    private async void StartHeadlessWithDelay_Click(object sender, RoutedEventArgs e)
    {
        if (_isHeadlessAutoStartInProgress)
        {
            return;
        }

        CancelHeadlessAutoStart();
        _headlessAutoStartCts = new CancellationTokenSource();
        var token = _headlessAutoStartCts.Token;

        _isHeadlessAutoStartInProgress = true;
        _isHeadlessWaitingToBeStarted = true;
        StartSptHeadlessManagerButton.IsEnabled = false;
        StartSptHeadlessManagerButton.Content = "Waiting for SPT...";
        StartSptHeadlessManagerButton.Background = Brushes.Orange;

        try
        {
            var sptReady = await WaitForSptServerReadyAsync(token);
            if (!sptReady)
            {
                MessageBox.Show("SPT server did not become reachable in time. Headless manager was not started.", "Timeout", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var headlessStarted = await StartHeadlessManagerWithRetryAsync(token);
            if (!headlessStarted)
            {
                MessageBox.Show("Headless manager failed to stay running after multiple attempts.", "Headless Start Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore cancels from manual stop operations.
        }
        finally
        {
            _isHeadlessAutoStartInProgress = false;
            _isHeadlessWaitingToBeStarted = false;

            if (_headlessAutoStartCts != null && _headlessAutoStartCts.Token == token)
            {
                _headlessAutoStartCts.Dispose();
                _headlessAutoStartCts = null;
            }
        }
    }

    private async Task<bool> WaitForSptServerReadyAsync(CancellationToken token)
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(2)
        };

        var deadline = DateTime.UtcNow.AddSeconds(SptReadyTimeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            if (IsProcessRunningRegex(@"^SPT\.Server$") && await ProbeSptServerAsync(client, token))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(SptReadyProbeIntervalSeconds), token);
        }

        return false;
    }

    private static async Task<bool> ProbeSptServerAsync(HttpClient client, CancellationToken token)
    {
        foreach (var probeUri in SptServerProbeUris)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, probeUri);
                using var _ = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                return true;
            }
            catch
            {
                // Probe next URL.
            }
        }

        return false;
    }

    private async Task<bool> StartHeadlessManagerWithRetryAsync(CancellationToken token)
    {
        for (var attempt = 1; attempt <= HeadlessAutoStartMaxAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();

            if (IsProcessRunningRegex(@"^FikaHeadlessManager$"))
            {
                return true;
            }

            StartSptHeadlessManager_Click(this, new RoutedEventArgs());
            await Task.Delay(TimeSpan.FromSeconds(HeadlessStartupValidationSeconds), token);

            if (IsProcessRunningRegex(@"^FikaHeadlessManager$"))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }

        return false;
    }

    private void CancelHeadlessAutoStart()
    {
        if (_headlessAutoStartCts == null)
        {
            return;
        }

        _headlessAutoStartCts.Cancel();
        _headlessAutoStartCts.Dispose();
        _headlessAutoStartCts = null;
        _isHeadlessAutoStartInProgress = false;
        _isHeadlessWaitingToBeStarted = false;
    }

    private void StopAllServers_Click(object sender, RoutedEventArgs e)
    {
        CancelHeadlessAutoStart();

        if (IsProcessRunningRegex(@"^SPTServerConsole$"))
        {
            // Reroute to existing close launcher method
            StartServer_Click(this, new RoutedEventArgs());

            SetLauncherServerButtonAsStopping();
        }
        if (IsProcessRunningRegex(@"^SPT\.Server$"))
        {
            // Reroute to existing close spt server method
            StartSptServer_Click(this, new RoutedEventArgs());

            SetSptServerButtonAsStopping();
        }
        if (IsProcessRunningRegex(@"^FikaHeadlessManager$"))
        {
            // Reroute to existing headless manager close method
            StartSptHeadlessManager_Click(this, new RoutedEventArgs());

            SetHeadlessManagerButtonAsStopping();
        }
        if (IsProcessRunningRegex(@"^EscapeFromTarkov$"))
        {
            // Reroute to existing close headless method
            CloseSptHeadless_Click(this, new RoutedEventArgs());
        }
    }

    private void RefreshAdminListView()
    {
        if (string.IsNullOrWhiteSpace(_databasePath))
        {
            return;
        }

        try
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();
            EnsureSptCoffeeSchema(connection);
            MigrateLegacyAdminsToDatabase(connection);

            var adminList = LoadAdminsFromDatabase(connection);
            AdminListView.ItemsSource = adminList;
        }
        catch (System.Exception ex)
        {
            System.Windows.MessageBox.Show("Failed to load admin configuration: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshAdminList_Click(object sender, RoutedEventArgs e)
    {
        RefreshAdminListView();
    }

    // ──────────────────── Mod management ────────────────────

    private List<ModInfo> LoadModsFromDatabase(SqliteConnection connection)
    {
        var mods = new List<ModInfo>();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT name, version, file_name, is_folder_mod,
       allow_on_headless, is_optional, optional_default_state
FROM plugins ORDER BY name COLLATE NOCASE;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            mods.Add(new ModInfo
            {
                Name = reader.GetString(0),
                Version = reader.GetString(1),
                FileName = reader.GetString(2),
                IsFolderMod = reader.GetInt32(3) == 1,
                AllowOnHeadless = reader.GetInt32(4) == 1,
                IsOptional = reader.GetInt32(5) == 1,
                OptionalDefaultState = reader.GetInt32(6) == 1
            });
        }
        return mods;
    }

    private void UpsertModInDatabase(SqliteConnection connection, ModInfo mod)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO plugins(name, version, file_name, is_folder_mod, allow_on_headless, is_optional, optional_default_state, updated_utc)
VALUES($name, $version, $fileName, $isFolderMod, $allowOnHeadless, $isOptional, $optionalDefaultState, $updatedUtc)
ON CONFLICT(name) DO UPDATE SET
    version = excluded.version,
    file_name = excluded.file_name,
    is_folder_mod = excluded.is_folder_mod,
    allow_on_headless = excluded.allow_on_headless,
    is_optional = excluded.is_optional,
    optional_default_state = excluded.optional_default_state,
    updated_utc = excluded.updated_utc;";
        command.Parameters.AddWithValue("$name", mod.Name);
        command.Parameters.AddWithValue("$version", mod.Version);
        command.Parameters.AddWithValue("$fileName", mod.FileName);
        command.Parameters.AddWithValue("$isFolderMod", mod.IsFolderMod ? 1 : 0);
        command.Parameters.AddWithValue("$allowOnHeadless", mod.AllowOnHeadless ? 1 : 0);
        command.Parameters.AddWithValue("$isOptional", mod.IsOptional ? 1 : 0);
        command.Parameters.AddWithValue("$optionalDefaultState", mod.OptionalDefaultState ? 1 : 0);
        command.Parameters.AddWithValue("$updatedUtc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private void DeleteModFromDatabase(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM plugins WHERE name = $name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();
    }

    private void RefreshModListView()
    {
        if (string.IsNullOrWhiteSpace(_databasePath)) return;
        try
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();
            EnsureSptCoffeeSchema(connection);
            MigratePluginsSchema(connection);
            var mods = LoadModsFromDatabase(connection);

            // Apply pending change states to the loaded mods
            foreach (var mod in mods)
            {
                if (_pendingChanges.TryGetValue(mod.Name, out var pendingMod))
                {
                    mod.PendingChangeState = pendingMod.PendingChangeState;
                    mod.NewVersion = pendingMod.NewVersion;
                }
            }

            foreach (var pendingAdd in _pendingChanges.Values
                         .Where(m => string.Equals(m.PendingChangeState, "add", StringComparison.OrdinalIgnoreCase)
                                     && mods.All(x => !string.Equals(x.Name, m.Name, StringComparison.OrdinalIgnoreCase))))
            {
                mods.Add(new ModInfo
                {
                    Name = pendingAdd.Name,
                    Version = pendingAdd.Version,
                    FileName = pendingAdd.FileName,
                    IsFolderMod = pendingAdd.IsFolderMod,
                    AllowOnHeadless = pendingAdd.AllowOnHeadless,
                    IsOptional = pendingAdd.IsOptional,
                    OptionalDefaultState = pendingAdd.OptionalDefaultState,
                    PendingChangeState = pendingAdd.PendingChangeState,
                    NewVersion = pendingAdd.NewVersion
                });
            }

            ModListView.ItemsSource = mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to load mod list: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshModList_Click(object sender, RoutedEventArgs e) => RefreshModListView();

    private void AddMod_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Select Mod File (ZIP, 7Z or DLL)",
            Filter = "ZIP Files (*.zip)|*.zip|7Z Files (*.7z)|*.7z|DLL Files (*.dll)|*.dll|All Files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (picker.ShowDialog() != true)
            return;

        string? tempFilteredZipPath = null;
        string? tempConvertedArchivePath = null;
        try
        {
            var selectedFilePath = picker.FileName;
            selectedFilePath = ConvertArchiveToZipIfNeeded(selectedFilePath, out tempConvertedArchivePath);
            var detected = DetectModPackageFromFile(selectedFilePath);
            var keepOldConfigs = false;
            var serverModsWithConfigConflicts = new List<string>();
            var excludedSourcePaths = new List<string>();
            string? selectedPluginVersion = null;
            string? selectedServerVersion = null;
            var selectedPluginIsFolderMod = detected.IsClientFolderMod;
            var selectedPluginAllowOnHeadless = false;
            var selectedPluginIsOptional = false;
            var selectedPluginOptionalDefaultState = false;

            if (!string.IsNullOrWhiteSpace(selectedFilePath)
                && !ShowIncomingFilePreview(selectedFilePath, detected.ClientModName, detected.ServerModName,
                    out keepOldConfigs, out serverModsWithConfigConflicts, out excludedSourcePaths,
                    out selectedPluginVersion, out selectedServerVersion,
                    out selectedPluginIsFolderMod, out selectedPluginAllowOnHeadless,
                    out selectedPluginIsOptional, out selectedPluginOptionalDefaultState))
            {
                return;
            }

            detected.ClientVersion = NormalizeVersionForStorage(selectedPluginVersion, detected.ClientVersion);
            detected.ServerVersion = NormalizeVersionForStorage(selectedServerVersion, detected.ServerVersion);
            detected.IsClientFolderMod = selectedPluginIsFolderMod;
            detected.AllowOnHeadless = selectedPluginAllowOnHeadless;
            detected.IsOptional = selectedPluginIsOptional;
            detected.OptionalDefaultState = selectedPluginOptionalDefaultState;

            if (detected.HasClientMod && IsVersionPlaceholder(detected.ClientVersion))
            {
                MessageBox.Show(
                    "Plugin version was not found in the ZIP plugin DLL and no custom version was provided.",
                    "Plugin Version Missing",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (detected.HasServerMod && IsVersionPlaceholder(detected.ServerVersion))
            {
                MessageBox.Show(
                    "Server mod version was not found in the ZIP server DLL and no custom version was provided.",
                    "Server Version Missing",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!string.IsNullOrWhiteSpace(selectedFilePath)
                && selectedFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && excludedSourcePaths.Count > 0)
            {
                tempFilteredZipPath = CreateFilteredZipFromSelection(selectedFilePath, excludedSourcePaths);
                selectedFilePath = tempFilteredZipPath;
            }

            _ = keepOldConfigs;
            _ = serverModsWithConfigConflicts;

            var queuedClient = false;
            if (detected.HasClientMod)
            {
                queuedClient = QueueDetectedClientMod(selectedFilePath, detected);
            }

            string? syncedServerMod = null;
            if (detected.HasServerMod)
            {
                syncedServerMod = QueueServerPendingFromBundleZip(selectedFilePath, ServerStateAddBoth, detected.ServerVersion);
            }

            if (!queuedClient && string.IsNullOrWhiteSpace(syncedServerMod))
            {
                MessageBox.Show("No plugin or server mod was detected in the selected file.", "Nothing Detected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RefreshModListView();
            RefreshServerModsListView();
            RefreshPendingChanges_Internal();
            SavePendingChangesToDatabase();

            var queuedParts = new List<string>();
            if (queuedClient && !string.IsNullOrWhiteSpace(detected.ClientModName))
                queuedParts.Add($"Plugin: {detected.ClientModName}");
            if (!string.IsNullOrWhiteSpace(syncedServerMod))
                queuedParts.Add($"Server Mod: {syncedServerMod}");

            MessageBox.Show(
                "Queued pending change(s):\n - " + string.Join("\n - ", queuedParts),
                "Success",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to add mod: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempFilteredZipPath) && File.Exists(tempFilteredZipPath))
            {
                File.Delete(tempFilteredZipPath);
            }

            CleanupTempWorkingFile(tempConvertedArchivePath);
        }
    }

    private sealed class DetectedModPackage
    {
        public string? ClientModName { get; set; }
        public bool IsClientFolderMod { get; set; }
        public bool AllowOnHeadless { get; set; }
        public bool IsOptional { get; set; }
        public bool OptionalDefaultState { get; set; }
        public string ClientVersion { get; set; } = string.Empty;
        public string? ServerModName { get; set; }
        public string ServerVersion { get; set; } = string.Empty;
        public bool HasClientMod => !string.IsNullOrWhiteSpace(ClientModName);
        public bool HasServerMod => !string.IsNullOrWhiteSpace(ServerModName);
    }

    private static DetectedModPackage DetectModPackageFromFile(string filePath)
    {
        var result = new DetectedModPackage();
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return result;

        if (filePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            result.ClientModName = Path.GetFileNameWithoutExtension(filePath);
            result.IsClientFolderMod = false;
            result.ClientVersion = TryReadDllVersion(filePath) ?? string.Empty;
            return result;
        }

        if (!filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return result;

        var bundle = InspectBundleZip(filePath);
        result.ClientModName = bundle.ClientModName;
        result.IsClientFolderMod = bundle.IsClientFolderMod ?? true;
        result.ClientVersion = TryExtractPluginVersionFromZip(filePath, bundle.ClientModName, out _, out _) ?? string.Empty;
        result.ServerModName = bundle.ServerModName;
        result.ServerVersion = bundle.ServerModVersion ?? string.Empty;
        return result;
    }


    private bool QueueDetectedClientMod(string packagePath, DetectedModPackage detected)
    {
        if (!detected.HasClientMod || string.IsNullOrWhiteSpace(detected.ClientModName))
            return false;

        var existingMod = TryGetCurrentClientMod(detected.ClientModName);
        var stagedFileName = StageClientPendingFile(packagePath, detected.ClientModName + ".zip");

        if (existingMod != null)
        {
            var pendingUpdate = CloneModInfo(existingMod);
            pendingUpdate.Name = detected.ClientModName;
            pendingUpdate.FileName = stagedFileName;
            pendingUpdate.IsFolderMod = detected.IsClientFolderMod;
            pendingUpdate.AllowOnHeadless = detected.AllowOnHeadless;
            pendingUpdate.IsOptional = detected.IsOptional;
            pendingUpdate.OptionalDefaultState = detected.OptionalDefaultState;
            pendingUpdate.PendingChangeState = "update";
            pendingUpdate.NewVersion = NormalizeVersionForStorage(detected.ClientVersion, pendingUpdate.Version);
            _pendingChanges[pendingUpdate.Name] = pendingUpdate;
            return true;
        }

        _pendingChanges[detected.ClientModName] = new ModInfo
        {
            Name = detected.ClientModName,
            Version = NormalizeVersionForStorage(detected.ClientVersion, "0.0.0"),
            FileName = stagedFileName,
            IsFolderMod = detected.IsClientFolderMod,
            AllowOnHeadless = detected.AllowOnHeadless,
            IsOptional = detected.IsOptional,
            OptionalDefaultState = detected.OptionalDefaultState,
            PendingChangeState = "add",
            NewVersion = null
        };
        return true;
    }

    private void EditMod_Click(object sender, RoutedEventArgs e)
    {
        var selectedMods = ModListView.SelectedItems.Cast<ModInfo>().ToList();
        if (selectedMods.Count != 1)
        {
            MessageBox.Show("Please select exactly one mod to edit.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selected = selectedMods[0];

        var editWindow = new ModEditWindow(selected);
        if (editWindow.ShowDialog() != true) return;

        try
        {
            var updatedMod = editWindow.Result!;

            // If the mod has a pending change state, preserve it
            if (_pendingChanges.TryGetValue(selected.Name, out var pendingMod))
            {
                updatedMod.PendingChangeState = pendingMod.PendingChangeState;
                updatedMod.NewVersion = pendingMod.NewVersion;
                _pendingChanges[selected.Name] = updatedMod;
                SavePendingChangesToDatabase();
                RefreshPendingChanges_Internal();
            }
            else
            {
                // No pending changes, save directly to database
                if (string.IsNullOrWhiteSpace(_databasePath))
                {
                    MessageBox.Show("Database path not configured.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                using var connection = new SqliteConnection($"Data Source={_databasePath}");
                connection.Open();
                EnsureSptCoffeeSchema(connection);
                MigratePluginsSchema(connection);
                UpsertModInDatabase(connection, updatedMod);
            }

            RefreshModListView();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to save mod: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveMod_Click(object sender, RoutedEventArgs e)
    {
        var selectedMods = ModListView.SelectedItems.Cast<ModInfo>().ToList();
        if (selectedMods.Count == 0)
        {
            MessageBox.Show("Please select one or more mods to remove.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"Mark {selectedMods.Count} selected mod(s) for deletion? This will be applied when you update changes.", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            foreach (var selected in selectedMods)
            {
                selected.PendingChangeState = "delete";
                _pendingChanges[selected.Name] = selected;
            }

            RefreshModListView();
            RefreshPendingChanges_Internal();
            SavePendingChangesToDatabase();
            MessageBox.Show($"Queued {selectedMods.Count} mod(s) for deletion.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to mark mod for removal: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateSelectedMod_Click(object sender, RoutedEventArgs e)
    {
        var selectedMods = ModListView.SelectedItems.Cast<ModInfo>().ToList();
        if (selectedMods.Count != 1)
        {
            MessageBox.Show("Please select exactly one mod to update.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selected = selectedMods[0];

        var updateWindow = new UpdateModWindow(selected);
        if (updateWindow.ShowDialog() != true) return;

        string? tempFilteredZipPath = null;
        string? tempConvertedArchivePath = null;
        try
        {
            var updatedMod = updateWindow.Result!;
            var selectedFilePath = updateWindow.SelectedFilePath;
            if (!string.IsNullOrWhiteSpace(selectedFilePath))
            {
                selectedFilePath = ConvertArchiveToZipIfNeeded(selectedFilePath, out tempConvertedArchivePath);
            }
            var normalizedNewVersion = NormalizeVersionForStorage(updatedMod.NewVersion, selected.Version);
            string? selectedServerVersion = null;

            if (!string.IsNullOrWhiteSpace(selectedFilePath))
            {
                var selectedPluginIsFolderMod = updatedMod.IsFolderMod;
                var selectedPluginAllowOnHeadless = updatedMod.AllowOnHeadless;
                var selectedPluginIsOptional = updatedMod.IsOptional;
                var selectedPluginOptionalDefaultState = updatedMod.OptionalDefaultState;

                if (!ShowIncomingFilePreview(selectedFilePath, updatedMod.Name, null,
                        out var keepOldConfigs, out var serverModsWithConfigConflicts, out var excludedSourcePaths,
                        out var selectedPluginVersion, out selectedServerVersion,
                        out selectedPluginIsFolderMod, out selectedPluginAllowOnHeadless,
                        out selectedPluginIsOptional, out selectedPluginOptionalDefaultState))
                {
                    return;
                }

                normalizedNewVersion = NormalizeVersionForStorage(selectedPluginVersion, normalizedNewVersion);
                updatedMod.IsFolderMod = selectedPluginIsFolderMod;
                updatedMod.AllowOnHeadless = selectedPluginAllowOnHeadless;
                updatedMod.IsOptional = selectedPluginIsOptional;
                updatedMod.OptionalDefaultState = selectedPluginOptionalDefaultState;

                _ = keepOldConfigs;
                _ = serverModsWithConfigConflicts;

                if (!string.IsNullOrWhiteSpace(selectedFilePath)
                    && selectedFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    && excludedSourcePaths.Count > 0)
                {
                    tempFilteredZipPath = CreateFilteredZipFromSelection(selectedFilePath, excludedSourcePaths);
                    selectedFilePath = tempFilteredZipPath;
                }

                if (!string.IsNullOrWhiteSpace(selectedFilePath))
                {
                    updatedMod.FileName = StageClientPendingFile(selectedFilePath, updatedMod.Name + ".zip");
                }
            }

            if (string.Equals(normalizedNewVersion, selected.Version, StringComparison.OrdinalIgnoreCase))
            {
                var keepSameVersion = MessageBox.Show(
                    $"Plugin new version is the same as old version ({selected.Version}). Continue anyway?",
                    "Same Version Detected",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (keepSameVersion != MessageBoxResult.Yes)
                    return;
            }

            // Mark as update pending
            updatedMod.PendingChangeState = "update";
            updatedMod.NewVersion = normalizedNewVersion;
            _pendingChanges[selected.Name] = updatedMod;

            var syncedServerMod = QueueServerPendingFromBundleZip(selectedFilePath, ServerStateUpdateBoth, selectedServerVersion);
            RefreshModListView();
            RefreshServerModsListView();
            RefreshPendingChanges_Internal();
            SavePendingChangesToDatabase();
            var syncText = string.IsNullOrWhiteSpace(syncedServerMod) ? string.Empty : $"\nSynced server mod: {syncedServerMod}";
            MessageBox.Show($"Mod \"{selected.Name}\" marked for update (v{selected.Version} → v{updatedMod.NewVersion}).{syncText}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to update mod: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempFilteredZipPath) && File.Exists(tempFilteredZipPath))
            {
                File.Delete(tempFilteredZipPath);
            }

            CleanupTempWorkingFile(tempConvertedArchivePath);
        }
    }

    private void AddAdmin_Click(object sender, RoutedEventArgs e)
    {
        // Open add admin window
        var addWindow = new AdminConfigWindow();

        if (addWindow.ShowDialog() == true)
        {
            // Create new admin config
            var newAdmin = new AdminConfig
            {
                Note = addWindow.NoteText,
                Secret = addWindow.Secret,
                IsEnabled = addWindow.IsAdminEnabled,
                AllowHeadlessClose = addWindow.AllowHeadlessClose
            };

            try
            {
                if (string.IsNullOrWhiteSpace(_databasePath))
                {
                    MessageBox.Show("Database path not configured.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                using var connection = new SqliteConnection($"Data Source={_databasePath}");
                connection.Open();
                EnsureSptCoffeeSchema(connection);

                var adminList = LoadAdminsFromDatabase(connection);
                adminList.Add(newAdmin);
                SaveAdminsToDatabase(connection, adminList);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show("Failed to save admin configuration: " + ex.Message, "Error", MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            // Refresh list view
            RefreshAdminListView();
        }
    }

    private void EditAdmin_Click(object sender, RoutedEventArgs e)
    {
        // Get selected admin
        var selectedAdmins = AdminListView.SelectedItems.Cast<AdminConfig>().ToList();
        if (selectedAdmins.Count != 1)
        {
            MessageBox.Show("Please select exactly one admin to edit.", "Info", MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var selectedAdmin = selectedAdmins[0];

        // Open edit window
        var editWindow = new AdminConfigWindow()
        {
            NoteText = selectedAdmin.Note,
            Secret = selectedAdmin.Secret,
            IsAdminEnabled = selectedAdmin.IsEnabled,
            AllowHeadlessClose = selectedAdmin.AllowHeadlessClose
        };

        if (editWindow.ShowDialog() == true)
        {
            // Update admin config
            selectedAdmin.Note = editWindow.NoteText;
            selectedAdmin.Secret = editWindow.Secret;
            selectedAdmin.IsEnabled = editWindow.IsAdminEnabled;
            selectedAdmin.AllowHeadlessClose = editWindow.AllowHeadlessClose;

            try
            {
                if (string.IsNullOrWhiteSpace(_databasePath))
                {
                    MessageBox.Show("Database path not configured.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                using var connection = new SqliteConnection($"Data Source={_databasePath}");
                connection.Open();
                EnsureSptCoffeeSchema(connection);

                var adminList = LoadAdminsFromDatabase(connection);
                var existing = adminList.FirstOrDefault(a => a.Secret == selectedAdmin.Secret);
                if (existing == null)
                {
                    MessageBox.Show("Selected admin was not found in database.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                existing.Note = selectedAdmin.Note;
                existing.Secret = selectedAdmin.Secret;
                existing.IsEnabled = selectedAdmin.IsEnabled;
                existing.AllowHeadlessClose = selectedAdmin.AllowHeadlessClose;

                SaveAdminsToDatabase(connection, adminList);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show("Failed to save admin configuration: " + ex.Message, "Error", MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            // Refresh list view
            RefreshAdminListView();
        }
    }

    private void RemoveAdmin_Click(object sender, RoutedEventArgs e)
    {
        var selectedAdmins = AdminListView.SelectedItems.Cast<AdminConfig>().ToList();
        if (selectedAdmins.Count == 0)
        {
            MessageBox.Show("Please select one or more admins to remove.", "Info", MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show($"Are you sure you want to remove {selectedAdmins.Count} selected admin(s)?", "Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(_databasePath))
            {
                MessageBox.Show("Database path not configured.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();
            EnsureSptCoffeeSchema(connection);

            var selectedSecrets = new HashSet<string>(
                selectedAdmins
                    .Select(a => a.Secret)
                    .Where(secret => !string.IsNullOrWhiteSpace(secret)),
                StringComparer.OrdinalIgnoreCase);

            var adminList = LoadAdminsFromDatabase(connection);
            adminList.RemoveAll(a => !string.IsNullOrWhiteSpace(a.Secret) && selectedSecrets.Contains(a.Secret));
            SaveAdminsToDatabase(connection, adminList);

            RefreshAdminListView();
        }
        catch (System.Exception ex)
        {
            MessageBox.Show("Failed to load admin configuration: " + ex.Message, "Error", MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }
    }

    // ──────────────────── Config management ────────────────────

    private List<ConfigInfo> LoadConfigsFromDatabase(SqliteConnection connection)
    {
        var configs = new List<ConfigInfo>();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT file_name, last_modified_utc, is_enforced
FROM configs ORDER BY file_name COLLATE NOCASE;";
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var lastModifiedRaw = reader.GetString(1);
            var parsed = DateTime.TryParse(lastModifiedRaw, out var parsedValue)
                ? parsedValue
                : DateTime.UtcNow;

            configs.Add(new ConfigInfo
            {
                FileName = reader.GetString(0),
                LastModified = DateTime.SpecifyKind(parsed, DateTimeKind.Utc),
                IsEnforced = reader.GetInt32(2) == 1
            });
        }

        return configs;
    }

    private static void UpsertConfigInDatabase(SqliteConnection connection, ConfigInfo config)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO configs(file_name, last_modified_utc, is_enforced, updated_utc)
VALUES($fileName, $lastModifiedUtc, $isEnforced, $updatedUtc)
ON CONFLICT(file_name) DO UPDATE SET
    last_modified_utc = excluded.last_modified_utc,
    is_enforced = excluded.is_enforced,
    updated_utc = excluded.updated_utc;";
        command.Parameters.AddWithValue("$fileName", config.FileName);
        command.Parameters.AddWithValue("$lastModifiedUtc", config.LastModified.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$isEnforced", config.IsEnforced ? 1 : 0);
        command.Parameters.AddWithValue("$updatedUtc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void DeleteConfigFromDatabase(SqliteConnection connection, string fileName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM configs WHERE file_name = $fileName COLLATE NOCASE;";
        command.Parameters.AddWithValue("$fileName", fileName);
        command.ExecuteNonQuery();
    }

    private void RefreshConfigListView()
    {
        if (string.IsNullOrWhiteSpace(_databasePath)) return;

        try
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();
            EnsureSptCoffeeSchema(connection);
            ConfigListView.ItemsSource = LoadConfigsFromDatabase(connection);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to load config list: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshConfigList_Click(object sender, RoutedEventArgs e) => RefreshConfigListView();

    private void AddConfig_Click(object sender, RoutedEventArgs e)
    {
        var editWindow = new ConfigEditWindow();
        if (editWindow.ShowDialog() != true) return;

        try
        {
            if (string.IsNullOrWhiteSpace(_databasePath))
            {
                MessageBox.Show("Database path not configured.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();
            EnsureSptCoffeeSchema(connection);
            UpsertConfigInDatabase(connection, editWindow.Result!);
            RefreshConfigListView();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to save config: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EditConfig_Click(object sender, RoutedEventArgs e)
    {
        var selectedConfigs = ConfigListView.SelectedItems.Cast<ConfigInfo>().ToList();
        if (selectedConfigs.Count != 1)
        {
            MessageBox.Show("Please select exactly one config to edit.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selected = selectedConfigs[0];

        var originalFileName = selected.FileName;
        var editWindow = new ConfigEditWindow(selected);
        if (editWindow.ShowDialog() != true) return;

        try
        {
            if (string.IsNullOrWhiteSpace(_databasePath))
            {
                MessageBox.Show("Database path not configured.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();
            EnsureSptCoffeeSchema(connection);

            if (!string.Equals(originalFileName, editWindow.Result!.FileName, StringComparison.OrdinalIgnoreCase))
            {
                DeleteConfigFromDatabase(connection, originalFileName);
            }

            UpsertConfigInDatabase(connection, editWindow.Result);
            RefreshConfigListView();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to save config: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveConfig_Click(object sender, RoutedEventArgs e)
    {
        var selectedConfigs = ConfigListView.SelectedItems.Cast<ConfigInfo>().ToList();
        if (selectedConfigs.Count == 0)
        {
            MessageBox.Show("Please select one or more configs to remove.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"Remove {selectedConfigs.Count} selected config(s) from the database?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            if (string.IsNullOrWhiteSpace(_databasePath))
            {
                MessageBox.Show("Database path not configured.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();
            EnsureSptCoffeeSchema(connection);

            var selectedFileNames = selectedConfigs
                .Select(c => c.FileName)
                .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var fileName in selectedFileNames)
            {
                DeleteConfigFromDatabase(connection, fileName);
            }

            RefreshConfigListView();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to remove config: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshInstalledPlugins_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            InstalledPluginsListView.ItemsSource = ScanInstalledPlugins();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to scan installed plugins: " + ex.Message, "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private List<InstalledPluginViewModel> ScanInstalledPlugins()
    {
        var excludedMods = new HashSet<string>(_excludedMods, StringComparer.OrdinalIgnoreCase);
        var excludedFolders = new HashSet<string>(_excludedModFolders, StringComparer.OrdinalIgnoreCase);

        // Load DB entries for cross-reference
        Dictionary<string, ModInfo> dbMods = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(_databasePath) && File.Exists(_databasePath))
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={_databasePath}");
                conn.Open();
                EnsureSptCoffeeSchema(conn);
                MigratePluginsSchema(conn);
                foreach (var m in LoadModsFromDatabase(conn))
                    dbMods[m.Name] = m;
            }
            catch { /* best-effort */ }
        }

        // Prefer the headless server plugins path; fall back to the main server path when unset.
        var pluginsPath = ResolveInstalledPluginsScanPath();
        var discovered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(pluginsPath))
        {
            // Folder mods
            foreach (var dir in Directory.GetDirectories(pluginsPath))
            {
                var name = Path.GetFileName(dir);
                if (excludedFolders.Contains(name)) continue;

                var dlls = Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories);
                if (dlls.Length == 0) continue;

                var ver = FileVersionInfo.GetVersionInfo(dlls[0]).FileVersion ?? "0";
                discovered[name] = ver;
            }

            // DLL mods
            foreach (var dll in Directory.GetFiles(pluginsPath, "*.dll", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(dll);
                if (excludedMods.Contains(name)) continue;

                var ver = FileVersionInfo.GetVersionInfo(dll).FileVersion ?? "0";
                discovered[name] = ver;
            }
        }

        var result = new List<InstalledPluginViewModel>();

        foreach (var (name, fileVer) in discovered
                     .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            string source = "local";

            dbMods.TryGetValue(name, out var dbEntry);
            string dbVer = dbEntry?.Version ?? "-";

            // Build status
            var statusParts = new List<string>();
            System.Windows.Media.Brush brush = System.Windows.Media.Brushes.LimeGreen;
            string weight = "Normal";

            if (dbEntry == null)
            {
                statusParts.Add("Not in database");
                brush = System.Windows.Media.Brushes.Gray;
            }
            else
            {
                bool versionMismatch = !string.IsNullOrWhiteSpace(dbVer) && dbVer != "-"
                    && !string.Equals(fileVer, dbVer, StringComparison.OrdinalIgnoreCase);

                if (versionMismatch)
                {
                    statusParts.Add("Outdated");
                    brush = System.Windows.Media.Brushes.Orange;
                    weight = "Bold";
                }

                if (!dbEntry.AllowOnHeadless)
                {
                    statusParts.Add("Not allowed on Headless");
                    brush = System.Windows.Media.Brushes.OrangeRed;
                    weight = "Bold";
                }

                if (statusParts.Count == 0)
                {
                    statusParts.Add("Up to date");
                    brush = System.Windows.Media.Brushes.LimeGreen;
                }
            }

            result.Add(new InstalledPluginViewModel
            {
                Name = name,
                FileVersion = fileVer,
                DbVersion = dbVer,
                Source = source,
                Status = string.Join(" · ", statusParts),
                StatusBrush = brush,
                StatusFontWeight = weight
            });
        }

        // Add database-only mods (not installed locally)
        foreach (var (name, dbEntry) in dbMods
                     .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (discovered.ContainsKey(name))
                continue; // Already processed as local

            // Skip database-only mods that are not allowed on headless
            if (!dbEntry.AllowOnHeadless)
                continue;

            var statusParts = new List<string>();
            var brush = System.Windows.Media.Brushes.LimeGreen;
            string weight = "Normal";

            statusParts.Add("Not installed");
            brush = System.Windows.Media.Brushes.Yellow;
            weight = "Bold";

            result.Add(new InstalledPluginViewModel
            {
                Name = name,
                FileVersion = "-",
                DbVersion = dbEntry.Version,
                Source = "Database",
                Status = string.Join(" · ", statusParts),
                StatusBrush = brush,
                StatusFontWeight = weight
            });
        }

        return result;
    }

    private void AllowSelectedPlugin_Click(object sender, RoutedEventArgs e)
    {
        var selectedPlugins = InstalledPluginsListView.SelectedItems.Cast<InstalledPluginViewModel>().ToList();
        if (selectedPlugins.Count == 0)
        {
            MessageBox.Show("Please select one or more mods to allow on headless.", "Info", MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(_databasePath))
            {
                MessageBox.Show("Database path not configured.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();
            EnsureSptCoffeeSchema(connection);
            MigratePluginsSchema(connection);

            // Load mods once and upsert all selected entries in one pass.
            var mods = LoadModsFromDatabase(connection);
            var upsertCount = 0;
            foreach (var selected in selectedPlugins)
            {
                var modToUpdate = mods.FirstOrDefault(m => string.Equals(m.Name, selected.Name, StringComparison.OrdinalIgnoreCase));

                if (modToUpdate == null)
                {
                    modToUpdate = new ModInfo
                    {
                        Name = selected.Name,
                        Version = selected.DbVersion != "-" ? selected.DbVersion : selected.FileVersion,
                        FileName = selected.Name + ".zip",
                        IsFolderMod = false,
                        AllowOnHeadless = true
                    };
                    mods.Add(modToUpdate);
                }
                else
                {
                    modToUpdate.AllowOnHeadless = true;
                }

                UpsertModInDatabase(connection, modToUpdate);
                upsertCount++;
            }

            RefreshInstalledPlugins_Click(this, new RoutedEventArgs());
            MessageBox.Show($"Updated {upsertCount} mod(s) to allow on headless.", "Success",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to update mod: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void UpdateSelectedPlugin_Click(object sender, RoutedEventArgs e)
    {
        var selectedPlugins = InstalledPluginsListView.SelectedItems.Cast<InstalledPluginViewModel>().ToList();
        if (selectedPlugins.Count == 0)
        {
            MessageBox.Show("Please select one or more plugins to update.", "Info", MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await UpdateInstalledPluginsAsync(selectedPlugins.Select(p => p.Name).ToList(), "selected");
    }

    private async void UpdateAllPlugins_Click(object sender, RoutedEventArgs e)
    {
        var allPlugins = (InstalledPluginsListView.ItemsSource as IEnumerable<InstalledPluginViewModel>)?.ToList() ?? new List<InstalledPluginViewModel>();
        if (allPlugins.Count == 0)
        {
            MessageBox.Show("No plugins available to update.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var candidates = allPlugins
            .Where(p => p.Status.Contains("Outdated", StringComparison.OrdinalIgnoreCase)
                        || p.Status.Contains("Not installed", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
        {
            MessageBox.Show("All installed plugins are already up to date.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await UpdateInstalledPluginsAsync(candidates, "all");
    }

    private async Task UpdateInstalledPluginsAsync(List<string> pluginNames, string scopeLabel)
    {
        if (pluginNames.Count == 0)
            return;

        if (string.IsNullOrWhiteSpace(_databasePath) || !File.Exists(_databasePath))
        {
            MessageBox.Show("Database path is not configured or does not exist.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var targetPluginsPath = ResolveHeadlessPluginsPath(createIfMissing: true);
        if (string.IsNullOrWhiteSpace(targetPluginsPath))
        {
            MessageBox.Show("Headless folder is not configured. Set it in Settings before updating plugins.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Dictionary<string, ModInfo> dbMods;
        try
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();
            EnsureSptCoffeeSchema(connection);
            MigratePluginsSchema(connection);
            dbMods = LoadModsFromDatabase(connection).ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to read plugins from database: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        UpdateSelectedPluginButton.IsEnabled = false;
        UpdateAllPluginsButton.IsEnabled = false;

        var updated = 0;
        var skipped = 0;
        var failed = new List<string>();

        try
        {
            foreach (var pluginName in pluginNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!dbMods.TryGetValue(pluginName, out var dbMod))
                {
                    skipped++;
                    continue;
                }

                if (!dbMod.AllowOnHeadless)
                {
                    skipped++;
                    continue;
                }

                try
                {
                    await DownloadAndInstallHeadlessPluginAsync(dbMod, targetPluginsPath);
                    updated++;
                }
                catch (Exception ex)
                {
                    failed.Add($"{pluginName}: {ex.Message}");
                }
            }

            RefreshInstalledPlugins_Click(this, new RoutedEventArgs());

            var resultText = $"Updated {updated} plugin(s) to headless server plugins. Skipped: {skipped}.";
            if (failed.Count > 0)
            {
                var details = string.Join("\n", failed.Take(5));
                if (failed.Count > 5)
                    details += $"\n...and {failed.Count - 5} more.";

                MessageBox.Show($"{resultText}\nFailed: {failed.Count}\n\n{details}",
                    "Plugin update completed with errors", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show($"{resultText}\nScope: {scopeLabel}.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        finally
        {
            UpdateSelectedPluginButton.IsEnabled = true;
            UpdateAllPluginsButton.IsEnabled = true;
        }
    }

    private async Task DownloadAndInstallHeadlessPluginAsync(ModInfo dbMod, string targetPluginsPath)
    {
        var downloadUrl = $"{BaseUrl}/api/plugins/{Uri.EscapeDataString(dbMod.Name)}/zip";
        var tempZipPath = Path.Combine(Path.GetTempPath(), $"sptcoffee_{dbMod.Name}_{Guid.NewGuid():N}.zip");
        var extractPath = Path.Combine(Path.GetTempPath(), $"sptcoffee_extract_{dbMod.Name}_{Guid.NewGuid():N}");

        try
        {
            using (var response = await PluginDownloadHttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var sourceStream = await response.Content.ReadAsStreamAsync();
                await using var targetStream = File.Create(tempZipPath);
                await sourceStream.CopyToAsync(targetStream);
            }

            ZipFile.ExtractToDirectory(tempZipPath, extractPath);
            Directory.CreateDirectory(targetPluginsPath);

            if (dbMod.IsFolderMod)
            {
                var sourceFolder = ResolveFolderModSourceDirectory(extractPath, dbMod.Name);
                var destinationFolder = Path.Combine(targetPluginsPath, dbMod.Name);

                if (Directory.Exists(destinationFolder))
                    Directory.Delete(destinationFolder, true);

                CopyDirectoryRecursive(sourceFolder, destinationFolder);
            }
            else
            {
                var dllFiles = Directory.GetFiles(extractPath, "*.dll", SearchOption.AllDirectories);
                var expectedDll = dbMod.Name + ".dll";
                var sourceDll = dllFiles.FirstOrDefault(f =>
                                    string.Equals(Path.GetFileName(f), expectedDll, StringComparison.OrdinalIgnoreCase))
                                ?? dllFiles.FirstOrDefault();

                if (sourceDll == null)
                    throw new InvalidDataException("Downloaded plugin package does not contain a DLL.");

                var destinationDll = Path.Combine(targetPluginsPath, Path.GetFileName(sourceDll));
                if (File.Exists(destinationDll))
                    File.Delete(destinationDll);

                File.Copy(sourceDll, destinationDll, true);
            }
        }
        finally
        {
            if (File.Exists(tempZipPath))
                File.Delete(tempZipPath);
            if (Directory.Exists(extractPath))
                Directory.Delete(extractPath, true);
        }
    }

    private static string ResolveFolderModSourceDirectory(string extractPath, string modName)
    {
        var bundledPluginFolder = Path.Combine(extractPath, "BepInEx", "plugins", modName);
        if (Directory.Exists(bundledPluginFolder))
            return bundledPluginFolder;

        var bundledPluginsRoot = Path.Combine(extractPath, "BepInEx", "plugins");
        if (Directory.Exists(bundledPluginsRoot))
        {
            var namedInBundled = Path.Combine(bundledPluginsRoot, modName);
            if (Directory.Exists(namedInBundled))
                return namedInBundled;

            var bundledSubdirs = Directory.GetDirectories(bundledPluginsRoot);
            if (bundledSubdirs.Length == 1)
                return bundledSubdirs[0];
        }

        var directFolder = Path.Combine(extractPath, modName);
        if (Directory.Exists(directFolder))
            return directFolder;

        var subDirectories = Directory.GetDirectories(extractPath);
        if (subDirectories.Length == 1)
            return subDirectories[0];

        var bestMatch = subDirectories.FirstOrDefault(dir =>
            string.Equals(Path.GetFileName(dir), modName, StringComparison.OrdinalIgnoreCase)
            || File.Exists(Path.Combine(dir, modName + ".dll")));
        if (!string.IsNullOrWhiteSpace(bestMatch))
            return bestMatch;

        return extractPath;
    }

    private string ResolveInstalledPluginsScanPath()
    {
        var headlessPluginsPath = ResolveHeadlessPluginsPath(createIfMissing: false);
        if (!string.IsNullOrWhiteSpace(headlessPluginsPath))
            return headlessPluginsPath;

        return Path.Combine(Config.SptServerFolder, "BepInEx", "plugins");
    }

    private string? ResolveHeadlessPluginsPath(bool createIfMissing)
    {
        if (string.IsNullOrWhiteSpace(Config.HeadlessFolder))
            return null;

        var candidates = new[]
        {
            Path.Combine(Config.HeadlessFolder, "server", "BepInEx", "plugins"),
            Path.Combine(Config.HeadlessFolder, "Server", "BepInEx", "plugins"),
            Path.Combine(Config.HeadlessFolder, "BepInEx", "plugins")
        };

        var resolved = candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
        if (createIfMissing)
            Directory.CreateDirectory(resolved);

        return resolved;
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destinationFile = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, destinationFile, true);
        }

        foreach (var directory in Directory.GetDirectories(sourceDir))
        {
            var destinationSubDirectory = Path.Combine(destinationDir, Path.GetFileName(directory));
            CopyDirectoryRecursive(directory, destinationSubDirectory);
        }
    }

    // ──────────────────── Pending Changes management ────────────────────

    private void LoadPendingChangesFromDatabase()
    {
        if (string.IsNullOrWhiteSpace(_databasePath))
            return;

        try
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();
            EnsureSptCoffeeSchema(connection);
            MigratePendingChangesSchema(connection);

            _pendingChanges.Clear();
            _serverConfigConflictDecisions.Clear();

            var dbMods = LoadModsFromDatabase(connection).ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT mod_name, change_type, old_version, new_version,
       file_name, is_folder_mod, allow_on_headless, is_optional, optional_default_state
FROM pending_changes 
ORDER BY id;";
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                string modName = reader.GetString(0);
                string changeType = reader.GetString(1);
                string? oldVersion = reader.IsDBNull(2) ? null : reader.GetString(2);
                string? newVersion = reader.IsDBNull(3) ? null : reader.GetString(3);
                string fileName = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                bool isFolderMod = !reader.IsDBNull(5) && reader.GetInt32(5) == 1;
                bool allowOnHeadless = !reader.IsDBNull(6) && reader.GetInt32(6) == 1;
                bool isOptional = !reader.IsDBNull(7) && reader.GetInt32(7) == 1;
                bool optionalDefaultState = !reader.IsDBNull(8) && reader.GetInt32(8) == 1;

                dbMods.TryGetValue(modName, out var dbMod);
                var mod = new ModInfo
                {
                    Name = modName,
                    Version = string.IsNullOrWhiteSpace(oldVersion) ? (dbMod?.Version ?? string.Empty) : oldVersion,
                    FileName = string.IsNullOrWhiteSpace(fileName) ? (dbMod?.FileName ?? (modName + ".zip")) : fileName,
                    IsFolderMod = isFolderMod || (dbMod?.IsFolderMod ?? false),
                    AllowOnHeadless = allowOnHeadless || (dbMod?.AllowOnHeadless ?? false),
                    IsOptional = isOptional || (dbMod?.IsOptional ?? false),
                    OptionalDefaultState = optionalDefaultState || (dbMod?.OptionalDefaultState ?? false),
                    PendingChangeState = changeType,
                    NewVersion = string.IsNullOrWhiteSpace(newVersion) ? null : newVersion
                };
                _pendingChanges[modName] = mod;
            }

            RefreshModListView();
            RefreshPendingChanges_Internal();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load pending changes: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SavePendingChangesToDatabase()
    {
        if (string.IsNullOrWhiteSpace(_databasePath))
            return;

        try
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();
            EnsureSptCoffeeSchema(connection);
            MigratePendingChangesSchema(connection);

            using var tx = connection.BeginTransaction();

            // Clear existing pending changes
            using (var clearCmd = connection.CreateCommand())
            {
                clearCmd.Transaction = tx;
                clearCmd.CommandText = "DELETE FROM pending_changes;";
                clearCmd.ExecuteNonQuery();
            }

            // Insert current pending changes
            foreach (var (name, mod) in _pendingChanges)
            {
                using var insertCmd = connection.CreateCommand();
                insertCmd.Transaction = tx;
                insertCmd.CommandText = @"
INSERT INTO pending_changes(
    mod_name, change_type, old_version, new_version, created_utc,
    file_name, is_folder_mod, allow_on_headless, is_optional, optional_default_state)
VALUES($modName, $changeType, $oldVersion, $newVersion, $createdUtc, $fileName, $isFolderMod, $allowOnHeadless, $isOptional, $optionalDefaultState);";
                insertCmd.Parameters.AddWithValue("$modName", name);
                insertCmd.Parameters.AddWithValue("$changeType", mod.PendingChangeState);
                insertCmd.Parameters.AddWithValue("$oldVersion", mod.Version ?? string.Empty);
                insertCmd.Parameters.AddWithValue("$newVersion", mod.NewVersion ?? string.Empty);
                insertCmd.Parameters.AddWithValue("$createdUtc", DateTime.UtcNow.ToString("O"));
                insertCmd.Parameters.AddWithValue("$fileName", mod.FileName ?? string.Empty);
                insertCmd.Parameters.AddWithValue("$isFolderMod", mod.IsFolderMod ? 1 : 0);
                insertCmd.Parameters.AddWithValue("$allowOnHeadless", mod.AllowOnHeadless ? 1 : 0);
                insertCmd.Parameters.AddWithValue("$isOptional", mod.IsOptional ? 1 : 0);
                insertCmd.Parameters.AddWithValue("$optionalDefaultState", mod.OptionalDefaultState ? 1 : 0);
                insertCmd.ExecuteNonQuery();
            }

            tx.Commit();

            // Keep server pending changes persisted together with client pending changes.
            SaveServerPendingChangesToDatabase();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save pending changes: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshPendingChanges_Internal()
    {
        try
        {
            PendingChangesListView.ItemsSource = BuildUnifiedPendingList();
            UpdatePendingChangesButtonState();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to refresh pending changes: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private List<PendingChangeEntry> BuildUnifiedPendingList()
    {
        var list = new List<PendingChangeEntry>();

        foreach (var mod in _pendingChanges.Values)
        {
            list.Add(new PendingChangeEntry
            {
                Name = mod.Name,
                ModType = "Client",
                PendingChangeState = mod.PendingChangeState,
                Version = mod.Version,
                NewVersion = mod.NewVersion
            });
        }

        foreach (var mod in _pendingServerChanges.Values)
        {
            list.Add(new PendingChangeEntry
            {
                Name = mod.Name,
                ModType = "Server",
                PendingChangeState = mod.PendingChangeState,
                Version = mod.Version,
                NewVersion = mod.NewVersion
            });
        }

        return list;
    }

    private void RefreshPendingChanges_Click(object sender, RoutedEventArgs e)
    {
        RefreshPendingChanges_Internal();
    }

    private void ShowPendingTempDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            MessageBox.Show(BuildPendingTempDiagnosticsReport(), "Pending Temp Diagnostics", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to generate temp diagnostics: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string BuildPendingTempDiagnosticsReport()
    {
        if (_exeFolder == null)
            return "Executable folder not determined.";

        var pendingRoot = GetPendingTempFolder(_exeFolder);
        var pendingClientFolder = GetPendingClientFilesFolder(_exeFolder);
        var pendingServerFolder = GetPendingServerFilesFolder(_exeFolder);
        var clientStorageFolder = GetPluginZipFolder(_exeFolder);
        var serverStorageFolder = GetServerModZipFolder(_exeFolder);

        var stagedClientFiles = Directory.Exists(pendingClientFolder)
            ? Directory.GetFiles(pendingClientFolder, "*", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();
        var stagedServerFiles = Directory.Exists(pendingServerFolder)
            ? Directory.GetFiles(pendingServerFolder, "*.zip", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();

        var missingClient = new List<string>();
        foreach (var mod in _pendingChanges.Values.Where(m =>
                     (string.Equals(m.PendingChangeState, "add", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(m.PendingChangeState, "update", StringComparison.OrdinalIgnoreCase))
                     && !string.IsNullOrWhiteSpace(m.FileName)))
        {
            var inPending = File.Exists(Path.Combine(pendingClientFolder, mod.FileName));
            var inStorage = File.Exists(Path.Combine(clientStorageFolder, mod.FileName));
            if (!inPending && !inStorage)
                missingClient.Add($"- {mod.Name} ({mod.FileName})");
        }

        bool NeedsServerPayload(string state)
            => state.StartsWith("add", StringComparison.OrdinalIgnoreCase)
               || state.StartsWith("update", StringComparison.OrdinalIgnoreCase);

        var missingServer = new List<string>();
        foreach (var mod in _pendingServerChanges.Values.Where(m =>
                     NeedsServerPayload(m.PendingChangeState)
                     && !string.IsNullOrWhiteSpace(m.FileName)))
        {
            var inPending = File.Exists(Path.Combine(pendingServerFolder, mod.FileName));
            var inStorage = File.Exists(Path.Combine(serverStorageFolder, mod.FileName));
            if (!inPending && !inStorage)
                missingServer.Add($"- {mod.Name} ({mod.FileName})");
        }

        var report = new System.Text.StringBuilder();
        report.AppendLine("Pending temp staging summary");
        report.AppendLine($"Pending root: {pendingRoot}");
        report.AppendLine();
        report.AppendLine($"Client staged files: {stagedClientFiles.Length}");
        report.AppendLine($"Server staged zips: {stagedServerFiles.Length}");
        report.AppendLine($"Pending client entries: {_pendingChanges.Count}");
        report.AppendLine($"Pending server entries: {_pendingServerChanges.Count}");
        report.AppendLine();

        report.AppendLine($"Missing client payloads: {missingClient.Count}");
        if (missingClient.Count > 0)
        {
            foreach (var line in missingClient)
                report.AppendLine(line);
            report.AppendLine();
        }

        report.AppendLine($"Missing server payloads: {missingServer.Count}");
        if (missingServer.Count > 0)
        {
            foreach (var line in missingServer)
                report.AppendLine(line);
            report.AppendLine();
        }

        if (missingClient.Count == 0 && missingServer.Count == 0)
            report.AppendLine("All required pending payload files are available in temp or storage.");

        return report.ToString().TrimEnd();
    }

    private void RevertSelectedPendingChange_Click(object sender, RoutedEventArgs e)
    {
        var selectedChanges = PendingChangesListView.SelectedItems.Cast<PendingChangeEntry>().ToList();
        if (selectedChanges.Count == 0)
        {
            MessageBox.Show("Please select one or more pending changes to revert.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"Revert {selectedChanges.Count} selected pending change(s)?", "Confirm Revert",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var revertedCount = 0;
        foreach (var selected in selectedChanges)
        {
                if (selected.IsServerMod)
                {
                    if (_pendingServerChanges.TryGetValue(selected.Name, out var pendingServer)
                        && _pendingServerChanges.Remove(selected.Name))
                    {
                        RemoveStagedPendingFile(pendingServer.FileName, server: true);
                        revertedCount++;
                    }
                }
                else
                {
                    if (_pendingChanges.TryGetValue(selected.Name, out var pendingClient)
                        && _pendingChanges.Remove(selected.Name))
                    {
                        RemoveStagedPendingFile(pendingClient.FileName, server: false);
                        revertedCount++;
                    }
                }
        }

        SavePendingChangesToDatabase();
        SaveServerPendingChangesToDatabase();
        RefreshModListView();
        RefreshServerModsListView();
        RefreshPendingChanges_Internal();
        MessageBox.Show($"Reverted {revertedCount} pending change(s).", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RestartServersCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializingRestartCheckbox)
        {
            return;
        }

        _restartServersIfUpdateStartedOnline = RestartServersCheckBox.IsChecked == true;
        SaveConfig(Config);
    }

    private void UpdatePendingChangesButtonState()
    {
        bool launcherRunning = IsProcessRunningRegex(@"^SPTServerConsole$");
        bool sptServerRunning = IsProcessRunningRegex(@"^SPT\.Server$");
        bool headlessManagerRunning = IsProcessRunningRegex(@"^FikaHeadlessManager$");
        bool headlessClientRunning = IsProcessRunningRegex(@"^EscapeFromTarkov$");

        bool anyServerRunning = launcherRunning || sptServerRunning || headlessManagerRunning || headlessClientRunning;
        bool hasPendingChanges = _pendingChanges.Count > 0 || _pendingServerChanges.Count > 0;

        if (!hasPendingChanges)
        {
            UpdateChangesButton.IsEnabled = false;
            UpdateChangesButton.Content = "Update Changes (No pending changes)";
            UpdateChangesButton.Background = Brushes.Gray;
        }
        else if (anyServerRunning)
        {
            UpdateChangesButton.IsEnabled = true;
            UpdateChangesButton.Content = "Update Changes (Servers online)";
            UpdateChangesButton.Background = Brushes.Orange;
        }
        else
        {
            UpdateChangesButton.IsEnabled = true;
            UpdateChangesButton.Content = "Update Changes";
            UpdateChangesButton.Background = Brushes.Green;
        }
    }

    private async void UpdatePendingChanges_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingChanges.Count == 0 && _pendingServerChanges.Count == 0)
        {
            MessageBox.Show("No pending changes to apply.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool launcherRunning = IsProcessRunningRegex(@"^SPTServerConsole$");
        bool sptServerRunning = IsProcessRunningRegex(@"^SPT\.Server$");
        bool headlessManagerRunning = IsProcessRunningRegex(@"^FikaHeadlessManager$");
        bool headlessClientRunning = IsProcessRunningRegex(@"^EscapeFromTarkov$");

        bool anyServerRunning = launcherRunning || sptServerRunning || headlessManagerRunning || headlessClientRunning;
        bool restartServers = RestartServersCheckBox.IsChecked == true;

        if (anyServerRunning)
        {
            var result = MessageBox.Show(
                "Servers are online. Are you sure you want to close them and apply changes?\n\n" +
                (restartServers ? "Servers will be restarted after changes are applied." : ""),
                "Confirm",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            // Close all servers
            StopAllServers_Click(this, new RoutedEventArgs());

            // Wait for servers to stop
            await Task.Delay(3000);
        }

        try
        {
            if (string.IsNullOrWhiteSpace(_databasePath))
            {
                MessageBox.Show("Database path not configured.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var profileBackupPath = BackupUserProfilesBeforeApplyChanges();

            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();
            EnsureSptCoffeeSchema(connection);
            MigratePluginsSchema(connection);

            // Apply all pending changes
            foreach (var (name, mod) in _pendingChanges)
            {
                if (mod.PendingChangeState == "add")
                {
                    mod.Version = NormalizeVersionForStorage(mod.Version, "0.0.0");
                    EnsureClientPendingFileInStorage(mod);
                    UpsertModInDatabase(connection, mod);
                }
                else if (mod.PendingChangeState == "delete")
                {
                    DeleteModFromDatabase(connection, name);
                }
                else if (mod.PendingChangeState == "update")
                {
                    // For update, just update the version and new version becomes the version
                    if (!string.IsNullOrWhiteSpace(mod.NewVersion))
                    {
                        EnsureClientPendingFileInStorage(mod);
                        mod.Version = NormalizeVersionForStorage(mod.NewVersion, NormalizeVersionForStorage(mod.Version, "0.0.0"));
                        mod.NewVersion = null; // Clear the new version after applying
                        UpsertModInDatabase(connection, mod);
                    }
                }
            }

            // Apply server mod pending changes
            foreach (var (name, mod) in _pendingServerChanges)
            {
                var state = mod.PendingChangeState?.Trim().ToLowerInvariant() ?? string.Empty;

                switch (state)
                {
                    case ServerStateAddBoth:
                    case ServerStateUpdateBoth:
                        UpsertServerModFromPending(connection, mod, name);
                        UpsertLocalServerModFromPending(mod, name);
                        break;

                    case ServerStateDeleteBoth:
                        DeleteServerModFromDatabase(connection, name);
                        RemoveLocalServerMod(name);
                        break;

                    case ServerStateAddDb:
                    case ServerStateUpdateDb:
                        UpsertServerModFromPending(connection, mod, name);
                        break;

                    case ServerStateDeleteDb:
                        DeleteServerModFromDatabase(connection, name);
                        break;

                    case ServerStateAddLocal:
                    case ServerStateUpdateLocal:
                        UpsertLocalServerModFromPending(mod, name);
                        break;

                    case ServerStateDeleteLocal:
                        RemoveLocalServerMod(name);
                        break;

                    // Backward-compatible old states
                    case "add":
                    case "update":
                        UpsertServerModFromPending(connection, mod, name);
                        break;
                    case "delete":
                        DeleteServerModFromDatabase(connection, name);
                        break;
                }
            }

            // Clear pending changes
            _pendingChanges.Clear();
            _pendingServerChanges.Clear();
            _serverConfigConflictDecisions.Clear();
            SavePendingChangesToDatabase();
            SaveServerPendingChangesToDatabase();
            ClearPendingTempStorage();

            var successMessage = string.IsNullOrWhiteSpace(profileBackupPath)
                ? "Pending changes applied successfully."
                : $"Pending changes applied successfully.\nProfile backup: {profileBackupPath}";
            MessageBox.Show(successMessage, "Success", MessageBoxButton.OK, MessageBoxImage.Information);

            RefreshModListView();
            RefreshServerModsListView();
            RefreshPendingChanges_Internal();

            // Restart servers if option is checked and they were running
            if (anyServerRunning && restartServers)
            {
                MessageBox.Show("Restarting servers...", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                StartAllServers_Click(this, new RoutedEventArgs());
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to apply pending changes: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ──────────────────── Server Mod DB helpers ────────────────────

    private List<ServerModInfo> LoadServerModsFromDatabase(SqliteConnection connection)
    {
        var mods = new List<ServerModInfo>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, version, file_name FROM server_plugins ORDER BY name COLLATE NOCASE;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            mods.Add(new ServerModInfo
            {
                Name = reader.GetString(0),
                Version = reader.GetString(1),
                FileName = reader.GetString(2)
            });
        }
        return mods;
    }

    private void UpsertServerModInDatabase(SqliteConnection connection, ServerModInfo mod)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO server_plugins(name, version, file_name, updated_utc)
VALUES($name, $version, $fileName, $updatedUtc)
ON CONFLICT(name) DO UPDATE SET
    version = excluded.version,
    file_name = excluded.file_name,
    updated_utc = excluded.updated_utc;";
        command.Parameters.AddWithValue("$name", mod.Name);
        command.Parameters.AddWithValue("$version", mod.Version);
        command.Parameters.AddWithValue("$fileName", mod.FileName);
        command.Parameters.AddWithValue("$updatedUtc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private void DeleteServerModFromDatabase(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM server_plugins WHERE name = $name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();
    }

    // ──────────────────── Server Mod scan ────────────────────

    private string ExtractServerModVersion(string modFolderPath)
    {
        // Use DLL metadata as the single source of truth for server mod versions.
        var dlls = Directory.GetFiles(modFolderPath, "*.dll", SearchOption.AllDirectories);
        if (dlls.Length > 0)
            return FileVersionInfo.GetVersionInfo(dlls[0]).FileVersion ?? "0.0.0";

        return "0.0.0";
    }

    private string ZipServerModFolder(string modName, string modFolderPath)
    {
        if (_exeFolder == null) throw new InvalidOperationException("Exe folder not determined.");

        var zipFolder = GetServerModZipFolder(_exeFolder);
        Directory.CreateDirectory(zipFolder);

        var zipPath = Path.Combine(zipFolder, modName + ".zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);

        ZipFile.CreateFromDirectory(modFolderPath, zipPath);
        return modName + ".zip";
    }

    private void UpsertServerModFromPending(SqliteConnection connection, ServerModInfo mod, string fallbackName)
    {
        var targetVersion = string.IsNullOrWhiteSpace(mod.NewVersion) ? mod.Version : mod.NewVersion!;
        targetVersion = NormalizeVersionForStorage(targetVersion, "0.0.0");

        var targetName = string.IsNullOrWhiteSpace(mod.Name) ? fallbackName : mod.Name;
        var targetFileName = EnsureServerModZipExistsForDatabase(targetName, mod.FileName);

        UpsertServerModInDatabase(connection, new ServerModInfo
        {
            Name = targetName,
            Version = targetVersion,
            FileName = targetFileName
        });
    }

    private string EnsureServerModZipExistsForDatabase(string modName, string? preferredFileName)
    {
        if (_exeFolder == null)
            throw new InvalidOperationException("Executable folder not determined.");

        var zipFolder = GetServerModZipFolder(_exeFolder);
        Directory.CreateDirectory(zipFolder);

        var fileName = string.IsNullOrWhiteSpace(preferredFileName) ? (modName + ".zip") : preferredFileName;
        var zipPath = Path.Combine(zipFolder, fileName);
        if (File.Exists(zipPath))
            return fileName;

        var pendingZipPath = Path.Combine(GetPendingServerFilesFolder(_exeFolder), fileName);
        if (File.Exists(pendingZipPath))
        {
            File.Copy(pendingZipPath, zipPath, true);
            return fileName;
        }

        // Zip missing: rebuild from local server mod folder when available.
        var localModFolder = Path.Combine(Config.SptServerFolder, "SPT", "user", "mods", modName);
        if (Directory.Exists(localModFolder))
        {
            return ZipServerModFolder(modName, localModFolder);
        }

        throw new FileNotFoundException(
            $"Server mod zip is required for database mod '{modName}', but no zip or local mod folder was found.",
            zipPath);
    }

    private void UpsertLocalServerModFromPending(ServerModInfo mod, string fallbackName)
    {
        if (_exeFolder == null)
            throw new InvalidOperationException("Executable folder not determined.");

        var targetName = string.IsNullOrWhiteSpace(mod.Name) ? fallbackName : mod.Name;
        var zipFileName = string.IsNullOrWhiteSpace(mod.FileName) ? (targetName + ".zip") : mod.FileName;
        var zipPath = ResolvePendingServerZipPath(zipFileName);

        var serverModsRoot = Path.Combine(Config.SptServerFolder, "SPT", "user", "mods");
        Directory.CreateDirectory(serverModsRoot);

        var targetFolder = Path.Combine(serverModsRoot, targetName);
        var tempExtractRoot = Path.Combine(GetPendingServerFilesFolder(_exeFolder), "_extract");
        Directory.CreateDirectory(tempExtractRoot);

        var tempTargetFolder = Path.Combine(tempExtractRoot, targetName + "_" + Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(zipPath, tempTargetFolder);
        var extractedSourceFolder = ResolveServerModSourceDirectory(tempTargetFolder, targetName);

        var backupFolder = Path.Combine(tempExtractRoot, targetName + "_backup_" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(targetFolder))
        {
            if (Directory.Exists(backupFolder))
                Directory.Delete(backupFolder, true);
            Directory.Move(targetFolder, backupFolder);
        }

        Directory.Move(extractedSourceFolder, targetFolder);


        if (Directory.Exists(backupFolder))
            Directory.Delete(backupFolder, true);

        if (Directory.Exists(tempTargetFolder))
            Directory.Delete(tempTargetFolder, true);
    }

    private static string ResolveServerModSourceDirectory(string extractRoot, string modName)
    {
        var sptRoot = Path.Combine(extractRoot, "SPT", "user", "mods", modName);
        if (Directory.Exists(sptRoot))
            return sptRoot;

        var directModFolder = Path.Combine(extractRoot, modName);
        if (Directory.Exists(directModFolder))
            return directModFolder;

        var subDirectories = Directory.GetDirectories(extractRoot);
        var namedMatch = subDirectories.FirstOrDefault(dir =>
            string.Equals(Path.GetFileName(dir), modName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(namedMatch))
            return namedMatch;

        if (subDirectories.Length == 1)
            return subDirectories[0];

        return extractRoot;
    }

    private static List<string> GetConfigConflictRelativePaths(string incomingRoot, string existingRoot)
    {
        var conflicts = new List<string>();
        if (!Directory.Exists(incomingRoot) || !Directory.Exists(existingRoot))
            return conflicts;

        foreach (var incomingFile in Directory.GetFiles(incomingRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(incomingRoot, incomingFile);
            if (!IsConfigLikePath(relativePath))
                continue;

            var existingFile = Path.Combine(existingRoot, relativePath);
            if (File.Exists(existingFile))
                conflicts.Add(relativePath);
        }

        return conflicts;
    }

    private bool ResolveServerConfigConflictDecision(string modName, IReadOnlyCollection<string> conflictPaths)
    {
        if (conflictPaths.Count == 0)
            return false;

        if (_serverConfigConflictDecisions.TryGetValue(modName, out var preselectedDecision))
            return preselectedDecision;

        var examples = string.Join("\n", conflictPaths.Take(5).Select(p => " - " + p.Replace('\\', '/')));
        if (conflictPaths.Count > 5)
            examples += $"\n - ...and {conflictPaths.Count - 5} more";

        var result = MessageBox.Show(
            $"Mod '{modName}' has {conflictPaths.Count} config file conflict(s):\n\n{examples}\n\n" +
            "Yes = keep old config files\nNo = override with new files",
            "Config Conflict Detected",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        var keepOld = result == MessageBoxResult.Yes;
        _serverConfigConflictDecisions[modName] = keepOld;
        return keepOld;
    }

    private static void RestoreOldConfigFiles(string backupRoot, string targetRoot, IEnumerable<string> relativeConfigPaths)
    {
        foreach (var relativePath in relativeConfigPaths)
        {
            var backupFile = Path.Combine(backupRoot, relativePath);
            if (!File.Exists(backupFile))
                continue;

            var targetFile = Path.Combine(targetRoot, relativePath);
            var targetDirectory = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
                Directory.CreateDirectory(targetDirectory);

            File.Copy(backupFile, targetFile, true);
        }
    }

    private void RemoveLocalServerMod(string modName)
    {
        var serverModsRoot = Path.Combine(Config.SptServerFolder, "SPT", "user", "mods");
        var targetFolder = Path.Combine(serverModsRoot, modName);
        if (Directory.Exists(targetFolder))
            Directory.Delete(targetFolder, true);
    }

    private void RefreshServerModsListView()
    {
        try
        {
            ServerModsListView.ItemsSource = ScanServerMods();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to scan server mods: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private List<ServerModViewModel> ScanServerMods()
    {
        var serverModsPath = Path.Combine(Config.SptServerFolder, "SPT", "user", "mods");

        // Load DB entries
        var dbMods = new Dictionary<string, ServerModInfo>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(_databasePath) && File.Exists(_databasePath))
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={_databasePath}");
                conn.Open();
                EnsureSptCoffeeSchema(conn);
                foreach (var m in LoadServerModsFromDatabase(conn))
                    dbMods[m.Name] = m;
            }
            catch { /* best-effort */ }
        }

        // Discover local mods
        var discovered = new Dictionary<string, (string Version, string FolderPath)>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(serverModsPath))
        {
            foreach (var modDir in Directory.GetDirectories(serverModsPath))
            {
                var name = Path.GetFileName(modDir);
                var version = ExtractServerModVersion(modDir);
                discovered[name] = (version, modDir);
            }
        }

        var result = new List<ServerModViewModel>();

        // Local mods
        foreach (var (name, info) in discovered.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            dbMods.TryGetValue(name, out var dbEntry);
            string dbVer = dbEntry?.Version ?? "-";
            string localVer = info.Version;

            string status;
            Brush brush;
            string weight = "Normal";

            if (dbEntry == null)
            {
                status = "Not in database";
                brush = Brushes.Gray;
            }
            else if (!string.Equals(localVer, dbVer, StringComparison.OrdinalIgnoreCase))
            {
                status = "Outdated";
                brush = Brushes.Orange;
                weight = "Bold";
            }
            else
            {
                status = "Up to date";
                brush = Brushes.LimeGreen;
            }

            string pendingState = _pendingServerChanges.TryGetValue(name, out var pending) ? pending.PendingChangeState : string.Empty;

            result.Add(new ServerModViewModel
            {
                Name = name,
                LocalVersion = localVer,
                DbVersion = dbVer,
                FileName = dbEntry?.FileName ?? (name + ".zip"),
                Status = status,
                StatusBrush = brush,
                StatusFontWeight = weight,
                PendingChangeState = pendingState
            });
        }

        // DB-only mods (not installed locally)
        foreach (var (name, dbEntry) in dbMods.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (discovered.ContainsKey(name)) continue;

            string pendingState = _pendingServerChanges.TryGetValue(name, out var pending) ? pending.PendingChangeState : string.Empty;

            result.Add(new ServerModViewModel
            {
                Name = name,
                LocalVersion = "-",
                DbVersion = dbEntry.Version,
                FileName = dbEntry.FileName,
                Status = "Not installed locally",
                StatusBrush = Brushes.Yellow,
                StatusFontWeight = "Bold",
                PendingChangeState = pendingState
            });
        }

        return result;
    }

    // ──────────────────── Server Mod button handlers ────────────────────

    private void RefreshServerMods_Click(object sender, RoutedEventArgs e) => RefreshServerModsListView();

    private static bool HasLocal(ServerModViewModel mod) => !string.Equals(mod.LocalVersion, "-", StringComparison.OrdinalIgnoreCase);
    private static bool HasDb(ServerModViewModel mod) => !string.Equals(mod.DbVersion, "-", StringComparison.OrdinalIgnoreCase);

    private sealed class BundleZipContents
    {
        public string? ClientModName { get; set; }
        /// <summary>
        /// True when the client mod is stored in a sub-folder under BepInEx/plugins (IsFolderMod).
        /// False when the client mod is a DLL placed directly under BepInEx/plugins.
        /// Null when no client mod was detected.
        /// </summary>
        public bool? IsClientFolderMod { get; set; }
        public string? ServerModName { get; set; }
        public string? ServerModVersion { get; set; }
    }

    private static BundleZipContents InspectBundleZip(string zipPath)
    {
        var result = new BundleZipContents();

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                var fullName = entry.FullName.Replace('\\', '/').Trim('/');
                if (string.IsNullOrWhiteSpace(fullName))
                    continue;

                var segments = fullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0)
                    continue;

                // Detect client mod under BepInEx/plugins
                if (result.ClientModName == null && TryGetSegmentAfterPath(segments, "BepInEx", "plugins", out var clientSegment))
                {
                    // BepInEx/plugins/[clientSegment]/... → folder mod
                    // BepInEx/plugins/[clientSegment.dll]   → single DLL, not folder mod
                    bool isFolderMod = !clientSegment.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                    result.ClientModName = isFolderMod ? clientSegment : Path.GetFileNameWithoutExtension(clientSegment);
                    result.IsClientFolderMod = isFolderMod;
                }

                // Detect server mod under SPT/user/mods
                if (result.ServerModName == null && TryGetSegmentAfterPath(segments, "SPT", "user", "mods", out var serverSegment))
                {
                    result.ServerModName = serverSegment;
                }

                // Fallback server-mod detection for ZIPs that are already rooted at <ServerModName>/...
                if (result.ServerModName == null
                    && fullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    && !PathContainsArchiveSegment(fullName, "BepInEx"))
                {
                    string? candidateName = null;

                    if (segments.Length >= 4
                        && string.Equals(segments[0], "SPT", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(segments[1], "user", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(segments[2], "mods", StringComparison.OrdinalIgnoreCase))
                    {
                        candidateName = segments[3];
                    }
                    else if (segments.Length >= 2)
                    {
                        candidateName = segments[0];
                        if (string.Equals(candidateName, "SPT", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(candidateName, "user", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(candidateName, "mods", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(candidateName, "BepInEx", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(candidateName, "plugins", StringComparison.OrdinalIgnoreCase))
                        {
                            candidateName = null;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(candidateName))
                    {
                        result.ServerModName = candidateName;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(result.ServerModName))
            {
                result.ServerModVersion = ExtractServerModVersionFromZip(zipPath, result.ServerModName);
            }

            if (result.ServerModName == null)
            {
                var fallbackVersion = ExtractServerModVersionFromZip(zipPath);
                if (!string.Equals(fallbackVersion, "0.0.0", StringComparison.OrdinalIgnoreCase))
                {
                    result.ServerModName = Path.GetFileNameWithoutExtension(zipPath);
                    result.ServerModVersion = fallbackVersion;
                }
            }
        }
        catch
        {
            // Keep detection optional: callers should still proceed with regular add/update behavior.
        }

        return result;
    }

    private static bool TryGetSegmentAfterPath(string[] segments, string first, string second, out string value)
    {
        for (var i = 0; i <= segments.Length - 3; i++)
        {
            if (string.Equals(segments[i], first, StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[i + 1], second, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(segments[i + 2]))
            {
                value = segments[i + 2];
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetSegmentAfterPath(string[] segments, string first, string second, string third, out string value)
    {
        for (var i = 0; i <= segments.Length - 4; i++)
        {
            if (string.Equals(segments[i], first, StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[i + 1], second, StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[i + 2], third, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(segments[i + 3]))
            {
                value = segments[i + 3];
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static string? TryReadVersionFromPackageJson(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            if (doc.RootElement.TryGetProperty("version", out var versionProperty))
            {
                return versionProperty.GetString();
            }
        }
        catch
        {
            // Ignore malformed package.json in bundle detection.
        }

        return null;
    }

    private sealed class IncomingPackagePreview
    {
        public List<FileChangePreviewItem> Files { get; } = new();
        public bool HasConfigConflicts { get; set; }
        public HashSet<string> ServerModsWithConfigConflicts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string ActionSummary { get; set; } = "Pending file changes";
        public string DetectedType { get; set; } = "Unknown";
        public string? DetectedPluginName { get; set; }
        public string? DetectedServerModName { get; set; }
        public string PluginAction { get; set; } = "None";
        public string? PluginOldVersion { get; set; }
        public string? PluginNewVersion { get; set; }
        public bool PluginIsFolderMod { get; set; }
        public bool PluginAllowOnHeadless { get; set; }
        public bool PluginIsOptional { get; set; }
        public bool PluginOptionalDefaultState { get; set; }
        public string ServerAction { get; set; } = "None";
        public string? ServerOldVersion { get; set; }
        public string? ServerNewVersion { get; set; }
    }

    private bool ShowIncomingFilePreview(
        string sourcePath,
        string? fallbackClientModName,
        string? fallbackServerModName,
        out bool keepOldConfigs,
        out List<string> serverModsWithConfigConflicts,
        out List<string> excludedSourcePaths,
        out string? selectedPluginVersion,
        out string? selectedServerVersion,
        out bool selectedPluginIsFolderMod,
        out bool selectedPluginAllowOnHeadless,
        out bool selectedPluginIsOptional,
        out bool selectedPluginOptionalDefaultState)
    {
        keepOldConfigs = false;
        serverModsWithConfigConflicts = new List<string>();
        excludedSourcePaths = new List<string>();
        selectedPluginVersion = null;
        selectedServerVersion = null;
        selectedPluginIsFolderMod = false;
        selectedPluginAllowOnHeadless = false;
        selectedPluginIsOptional = false;
        selectedPluginOptionalDefaultState = false;

        var preview = BuildIncomingPackagePreview(sourcePath, fallbackClientModName, fallbackServerModName);
        if (preview.Files.Count == 0)
            return true;

        var savedExcludedPaths = LoadSavedPreviewExcludedPaths();
        foreach (var item in preview.Files)
        {
            if (savedExcludedPaths.Contains(NormalizeArchivePath(item.SourceRelativePath)))
            {
                item.IsIncluded = false;
            }
        }

        var window = new FileChangePreviewWindow(
            preview.Files,
            preview.HasConfigConflicts,
            preview.ActionSummary,
            preview.DetectedType,
            preview.DetectedPluginName,
            preview.DetectedServerModName,
            preview.PluginAction,
            preview.PluginOldVersion,
            preview.PluginNewVersion,
            preview.PluginIsFolderMod,
            preview.PluginAllowOnHeadless,
            preview.PluginIsOptional,
            preview.PluginOptionalDefaultState,
            preview.ServerAction,
            preview.ServerOldVersion,
            preview.ServerNewVersion)
        {
            Owner = this
        };

        if (window.ShowDialog() != true)
            return false;

        keepOldConfigs = false;
        serverModsWithConfigConflicts = preview.ServerModsWithConfigConflicts.ToList();
        excludedSourcePaths = window.ExcludedSourcePaths.ToList();
        selectedPluginVersion = window.SelectedPluginVersion;
        selectedServerVersion = window.SelectedServerVersion;
        selectedPluginIsFolderMod = window.SelectedPluginIsFolderMod;
        selectedPluginAllowOnHeadless = window.SelectedPluginAllowOnHeadless;
        selectedPluginIsOptional = window.SelectedPluginIsOptional;
        selectedPluginOptionalDefaultState = window.SelectedPluginOptionalDefaultState;
        SavePreviewExcludedPaths(excludedSourcePaths);
        return true;
    }

    private HashSet<string> LoadSavedPreviewExcludedPaths()
    {
        if (_savedPreviewExcludedPaths != null)
            return _savedPreviewExcludedPaths;

        _savedPreviewExcludedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(_databasePath) || !File.Exists(_databasePath))
            return _savedPreviewExcludedPaths;

        try
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();
            EnsureSptCoffeeSchema(connection);

            var raw = GetSetting(connection, PreviewExcludedPathsSettingKey);
            if (string.IsNullOrWhiteSpace(raw))
                return _savedPreviewExcludedPaths;

            var values = JsonSerializer.Deserialize<List<string>>(raw) ?? new List<string>();
            _savedPreviewExcludedPaths = new HashSet<string>(
                values
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(NormalizeArchivePath),
                StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _savedPreviewExcludedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return _savedPreviewExcludedPaths;
    }

    private void SavePreviewExcludedPaths(IEnumerable<string> excludedSourcePaths)
    {
        var normalized = excludedSourcePaths
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(NormalizeArchivePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _savedPreviewExcludedPaths = new HashSet<string>(normalized, StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(_databasePath))
            return;

        try
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();
            EnsureSptCoffeeSchema(connection);

            var json = JsonSerializer.Serialize(normalized);
            UpsertSetting(connection, PreviewExcludedPathsSettingKey, json);
        }
        catch
        {
            // Keep preview flow non-blocking if settings persistence fails.
        }
    }

    private IncomingPackagePreview BuildIncomingPackagePreview(string sourcePath, string? fallbackClientModName, string? fallbackServerModName)
    {
        var preview = new IncomingPackagePreview();
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return preview;

        if (sourcePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            preview.DetectedType = "Plugin";
            preview.DetectedPluginName = string.IsNullOrWhiteSpace(fallbackClientModName)
                ? Path.GetFileNameWithoutExtension(sourcePath)
                : fallbackClientModName;

            var pluginCurrent = !string.IsNullOrWhiteSpace(preview.DetectedPluginName)
                ? TryGetCurrentClientMod(preview.DetectedPluginName)
                : null;
            var detectedPluginVersion = FileVersionInfo.GetVersionInfo(sourcePath).FileVersion;
            preview.PluginOldVersion = pluginCurrent?.Version;
            preview.PluginNewVersion = NormalizeVersionForStorage(detectedPluginVersion, pluginCurrent?.Version ?? "0.0.0");
            preview.PluginAction = pluginCurrent == null ? "Add" : "Update";
            preview.PluginIsFolderMod = false;
            preview.PluginAllowOnHeadless = pluginCurrent?.AllowOnHeadless ?? false;
            preview.PluginIsOptional = pluginCurrent?.IsOptional ?? false;
            preview.PluginOptionalDefaultState = pluginCurrent?.OptionalDefaultState ?? false;
            preview.ServerAction = "None";
            preview.ActionSummary = $"Plugin: {preview.DetectedPluginName} | Action: {preview.PluginAction}";

            var fileName = Path.GetFileName(sourcePath);
            var existsInOld = ClientFileExistsInOld(fileName);
            var relativePath = fileName;
            preview.Files.Add(new FileChangePreviewItem
            {
                FileType = "Plugin",
                FileName = fileName,
                Location = ".",
                ExistsInOld = existsInOld,
                SourceRelativePath = fileName
            });

            if (existsInOld && IsConfigLikePath(relativePath))
                preview.HasConfigConflicts = true;

            return preview;
        }

        if (!sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return preview;

        var detectedFromBundle = InspectBundleZip(sourcePath);
        preview.DetectedPluginName = string.IsNullOrWhiteSpace(detectedFromBundle.ClientModName)
            ? fallbackClientModName
            : detectedFromBundle.ClientModName;
        preview.DetectedServerModName = string.IsNullOrWhiteSpace(detectedFromBundle.ServerModName)
            ? fallbackServerModName
            : detectedFromBundle.ServerModName;
        preview.DetectedType = !string.IsNullOrWhiteSpace(preview.DetectedPluginName) && !string.IsNullOrWhiteSpace(preview.DetectedServerModName)
            ? "Both"
            : !string.IsNullOrWhiteSpace(preview.DetectedPluginName)
                ? "Plugin"
                : !string.IsNullOrWhiteSpace(preview.DetectedServerModName)
                    ? "Server Mod"
                    : "Unknown";

        var currentClient = !string.IsNullOrWhiteSpace(preview.DetectedPluginName)
            ? TryGetCurrentClientMod(preview.DetectedPluginName)
            : null;
        var currentServerVersion = !string.IsNullOrWhiteSpace(preview.DetectedServerModName)
            ? GetCurrentServerVersionForPending(preview.DetectedServerModName)
            : string.Empty;
        var detectedServerVersion = !string.IsNullOrWhiteSpace(detectedFromBundle.ServerModVersion)
            ? detectedFromBundle.ServerModVersion
            : ExtractServerModVersionFromZip(sourcePath, preview.DetectedServerModName);
        var detectedPluginVersionFromZip = TryExtractPluginVersionFromZip(sourcePath, preview.DetectedPluginName);

        preview.PluginOldVersion = currentClient?.Version;
        preview.PluginNewVersion = IsVersionPlaceholder(detectedPluginVersionFromZip) ? null : detectedPluginVersionFromZip;
        preview.PluginAction = string.IsNullOrWhiteSpace(preview.DetectedPluginName)
            ? "None"
            : currentClient == null ? "Add" : "Update";
        preview.PluginIsFolderMod = detectedFromBundle.IsClientFolderMod ?? true;
        preview.PluginAllowOnHeadless = currentClient?.AllowOnHeadless ?? false;
        preview.PluginIsOptional = currentClient?.IsOptional ?? false;
        preview.PluginOptionalDefaultState = currentClient?.OptionalDefaultState ?? false;

        preview.ServerOldVersion = currentServerVersion;
        preview.ServerNewVersion = IsVersionPlaceholder(detectedServerVersion) ? null : detectedServerVersion;
        preview.ServerAction = string.IsNullOrWhiteSpace(preview.DetectedServerModName)
            ? "None"
            : string.IsNullOrWhiteSpace(currentServerVersion) ? "Add" : "Update";

        if (string.Equals(preview.PluginAction, "None", StringComparison.OrdinalIgnoreCase))
        {
            preview.PluginOldVersion = null;
            preview.PluginNewVersion = null;
            preview.PluginIsFolderMod = false;
            preview.PluginAllowOnHeadless = false;
            preview.PluginIsOptional = false;
            preview.PluginOptionalDefaultState = false;
        }

        if (string.Equals(preview.ServerAction, "None", StringComparison.OrdinalIgnoreCase))
        {
            preview.ServerOldVersion = null;
            preview.ServerNewVersion = null;
        }

        var actionParts = new List<string>();
        if (!string.Equals(preview.PluginAction, "None", StringComparison.OrdinalIgnoreCase))
            actionParts.Add($"Plugin: {preview.PluginAction}");
        if (!string.Equals(preview.ServerAction, "None", StringComparison.OrdinalIgnoreCase))
            actionParts.Add($"Server Mod: {preview.ServerAction}");

        var nameSummary = BuildDetectedNamesSummary(preview.DetectedPluginName, preview.DetectedServerModName);
        var actionSummary = actionParts.Count == 0 ? "No detected add/update action" : string.Join(" | ", actionParts);
        preview.ActionSummary = string.IsNullOrWhiteSpace(nameSummary)
            ? actionSummary
            : $"{nameSummary} | {actionSummary}";

        try
        {
            using var archive = ZipFile.OpenRead(sourcePath);
            foreach (var entry in archive.Entries)
            {
                var normalized = entry.FullName.Replace('\\', '/').Trim('/');
                if (string.IsNullOrWhiteSpace(normalized) || normalized.EndsWith("/", StringComparison.Ordinal))
                    continue;

                var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0)
                    continue;

                if (IsStructureOnlyArchivePath(segments))
                    continue;

                if (TryFindPathIndex(segments, "BepInEx", "plugins", out var pluginIndex) && pluginIndex + 2 < segments.Length)
                {
                    var pluginSegment = segments[pluginIndex + 2];
                    var isDirectDll = pluginSegment.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                    if (!isDirectDll && segments.Length == pluginIndex + 3)
                        continue;

                    var relativeUnderPlugins = string.Join(Path.DirectorySeparatorChar.ToString(), segments.Skip(pluginIndex + 2));
                    var relativeUnderPlugin = isDirectDll
                        ? string.Join('/', segments.Skip(pluginIndex + 2))
                        : string.Join('/', segments.Skip(pluginIndex + 3));

                    var locationParts = relativeUnderPlugin.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
                    if (locationParts.Count > 0)
                        locationParts.RemoveAt(locationParts.Count - 1);
                    var location = locationParts.Count == 0 ? "." : string.Join('/', locationParts);

                    var existsInOld = ClientFileExistsInOld(relativeUnderPlugins);
                    preview.Files.Add(new FileChangePreviewItem
                    {
                        FileType = "Plugin",
                        FileName = Path.GetFileName(normalized),
                        Location = location,
                        ExistsInOld = existsInOld,
                        SourceRelativePath = normalized
                    });

                    if (existsInOld && IsConfigLikePath(relativeUnderPlugin))
                        preview.HasConfigConflicts = true;

                    continue;
                }

                if (TryFindPathIndex(segments, "SPT", "user", "mods", out var serverIndex) && serverIndex + 3 < segments.Length)
                {
                    var serverModName = segments[serverIndex + 3];
                    var relativeUnderMod = string.Join('/', segments.Skip(serverIndex + 4));
                    if (string.IsNullOrWhiteSpace(relativeUnderMod))
                        continue;

                    var locationParts = relativeUnderMod.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
                    if (locationParts.Count > 0)
                        locationParts.RemoveAt(locationParts.Count - 1);
                    var location = locationParts.Count == 0 ? "." : string.Join('/', locationParts);
                    var oldPath = Path.Combine(Config.SptServerFolder, "SPT", "user", "mods", serverModName,
                        string.Join(Path.DirectorySeparatorChar.ToString(), segments.Skip(serverIndex + 4)));
                    var existsInOld = File.Exists(oldPath);

                    preview.Files.Add(new FileChangePreviewItem
                    {
                        FileType = "Server Mod",
                        FileName = Path.GetFileName(normalized),
                        Location = location,
                        ExistsInOld = existsInOld,
                        SourceRelativePath = normalized
                    });

                    if (existsInOld && IsConfigLikePath(relativeUnderMod))
                    {
                        preview.HasConfigConflicts = true;
                        preview.ServerModsWithConfigConflicts.Add(serverModName);
                    }

                    continue;
                }

                preview.Files.Add(new FileChangePreviewItem
                {
                    FileType = "Other",
                    FileName = Path.GetFileName(normalized),
                    Location = segments.Length <= 1 ? "." : string.Join('/', segments.Take(segments.Length - 1)),
                    ExistsInOld = ResolveOtherEntryExistsInOld(normalized, preview.DetectedServerModName),
                    SourceRelativePath = normalized
                });

                if (preview.Files[^1].ExistsInOld && IsConfigLikePath(normalized))
                {
                    preview.HasConfigConflicts = true;
                    if (!string.IsNullOrWhiteSpace(preview.DetectedServerModName)
                        && normalized.StartsWith(preview.DetectedServerModName + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        preview.ServerModsWithConfigConflicts.Add(preview.DetectedServerModName);
                    }
                }
            }
        }
        catch
        {
            // If preview parsing fails we continue without blocking queueing.
        }

        if (preview.ServerModsWithConfigConflicts.Count == 0 && !string.IsNullOrWhiteSpace(fallbackServerModName) && preview.HasConfigConflicts)
        {
            preview.ServerModsWithConfigConflicts.Add(fallbackServerModName);
        }

        return preview;
    }

    private static string CreateFilteredZipFromSelection(string sourceZipPath, IReadOnlyCollection<string> excludedSourcePaths)
    {
        var excluded = new HashSet<string>(excludedSourcePaths.Select(NormalizeArchivePath), StringComparer.OrdinalIgnoreCase);
        var tempZipPath = Path.Combine(Path.GetTempPath(), $"sptcoffee_filtered_{Guid.NewGuid():N}.zip");

        using (var sourceArchive = ZipFile.OpenRead(sourceZipPath))
        using (var targetArchive = ZipFile.Open(tempZipPath, ZipArchiveMode.Create))
        {
            var includedFiles = 0;

            foreach (var sourceEntry in sourceArchive.Entries)
            {
                var normalized = NormalizeArchivePath(sourceEntry.FullName);
                if (string.IsNullOrWhiteSpace(normalized) || normalized.EndsWith("/", StringComparison.Ordinal))
                    continue;

                if (excluded.Contains(normalized))
                    continue;

                var targetEntry = targetArchive.CreateEntry(sourceEntry.FullName, CompressionLevel.Optimal);
                using var sourceStream = sourceEntry.Open();
                using var targetStream = targetEntry.Open();
                sourceStream.CopyTo(targetStream);
                includedFiles++;
            }

            if (includedFiles == 0)
                throw new InvalidOperationException("No files left after exclusions. Select at least one file.");
        }

        return tempZipPath;
    }

    private static string NormalizeArchivePath(string path)
    {
        return path.Replace('\\', '/').Trim('/');
    }

    private static string ConvertArchiveToZipIfNeeded(string sourcePath, out string? tempZipPath)
    {
        tempZipPath = null;

        if (string.IsNullOrWhiteSpace(sourcePath))
            return sourcePath;

        if (!sourcePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            return sourcePath;

        var extractFolder = Path.Combine(Path.GetTempPath(), $"sptcoffee_7z_extract_{Guid.NewGuid():N}");
        tempZipPath = Path.Combine(Path.GetTempPath(), $"sptcoffee_7z_converted_{Guid.NewGuid():N}.zip");

        try
        {
            Directory.CreateDirectory(extractFolder);
            using var archive = ArchiveFactory.Open(sourcePath);
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            {
                entry.WriteToDirectory(extractFolder, new ExtractionOptions
                {
                    ExtractFullPath = true,
                    Overwrite = true
                });
            }

            ZipFile.CreateFromDirectory(extractFolder, tempZipPath);
            return tempZipPath;
        }
        finally
        {
            if (Directory.Exists(extractFolder))
                Directory.Delete(extractFolder, true);
        }
    }

    private static void CleanupTempWorkingFile(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            File.Delete(path);
    }

    private static string ExtractZipToTempFolder(string zipPath)
    {
        var tempExtractRoot = Path.Combine(Path.GetTempPath(), $"sptcoffee_zip_extract_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempExtractRoot);
        ZipFile.ExtractToDirectory(zipPath, tempExtractRoot);
        return tempExtractRoot;
    }

    private static bool PathContainsArchiveSegment(string entryPath, string segment)
    {
        var normalizedPath = "/" + NormalizeArchivePath(entryPath) + "/";
        var normalizedSegment = "/" + NormalizeArchivePath(segment) + "/";
        return normalizedPath.Contains(normalizedSegment, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathContainsArchivePath(string entryPath, string relativePath)
    {
        var normalizedPath = "/" + NormalizeArchivePath(entryPath) + "/";
        var normalizedRelative = "/" + NormalizeArchivePath(relativePath) + "/";
        return normalizedPath.Contains(normalizedRelative, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVersionPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        return string.Equals(value, "custom", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "-", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVersionForStorage(string? rawVersion, string fallback)
    {
        return IsVersionPlaceholder(rawVersion) ? fallback : rawVersion!.Trim();
    }

    private static string BuildDetectedNamesSummary(string? pluginName, string? serverModName)
    {
        if (string.IsNullOrWhiteSpace(pluginName) && string.IsNullOrWhiteSpace(serverModName))
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(pluginName)
            && !string.IsNullOrWhiteSpace(serverModName)
            && string.Equals(pluginName, serverModName, StringComparison.OrdinalIgnoreCase))
        {
            return $"Detected Mod: {pluginName}";
        }

        if (!string.IsNullOrWhiteSpace(pluginName) && !string.IsNullOrWhiteSpace(serverModName))
            return $"Plugin: {pluginName} | Server Mod: {serverModName}";

        return !string.IsNullOrWhiteSpace(pluginName)
            ? $"Plugin: {pluginName}"
            : $"Server Mod: {serverModName}";
    }

    private static bool IsStructureOnlyArchivePath(string[] segments)
    {
        if (segments.Length == 1)
        {
            return string.Equals(segments[0], "BepInEx", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(segments[0], "plugins", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(segments[0], "SPT", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(segments[0], "user", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(segments[0], "mods", StringComparison.OrdinalIgnoreCase);
        }

        if (segments.Length == 2)
        {
            return (string.Equals(segments[0], "BepInEx", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(segments[1], "plugins", StringComparison.OrdinalIgnoreCase))
                   || (string.Equals(segments[0], "SPT", StringComparison.OrdinalIgnoreCase)
                       && string.Equals(segments[1], "user", StringComparison.OrdinalIgnoreCase));
        }

        if (segments.Length == 3)
        {
            return string.Equals(segments[0], "SPT", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(segments[1], "user", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(segments[2], "mods", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private bool ClientFileExistsInOld(string relativeUnderPlugins)
    {
        var pluginCandidates = new List<string>
        {
            Path.Combine(Config.SptServerFolder, "BepInEx", "plugins")
        };

        if (!string.IsNullOrWhiteSpace(Config.AdditionalModsPath))
        {
            pluginCandidates.Add(Path.Combine(Config.AdditionalModsPath, "BepInEx", "plugins"));
        }

        foreach (var pluginRoot in pluginCandidates)
        {
            var oldPath = Path.Combine(pluginRoot, relativeUnderPlugins.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(oldPath))
                return true;
        }

        return false;
    }

    private bool ResolveOtherEntryExistsInOld(string normalizedPath, string? detectedServerModName)
    {
        var normalized = NormalizeArchivePath(normalizedPath);

        if (PathContainsArchivePath(normalized, "BepInEx/config"))
        {
            var afterConfig = normalized.Split(new[] { "BepInEx/config/" }, StringSplitOptions.None).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(afterConfig))
            {
                var mainConfigPath = Path.Combine(Config.SptServerFolder, "BepInEx", "config", afterConfig.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(mainConfigPath))
                    return true;

                if (!string.IsNullOrWhiteSpace(Config.AdditionalModsPath))
                {
                    var secondaryConfigPath = Path.Combine(Config.AdditionalModsPath, "BepInEx", "config", afterConfig.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(secondaryConfigPath))
                        return true;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(detectedServerModName)
            && normalized.StartsWith(detectedServerModName + "/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = normalized.Substring(detectedServerModName.Length + 1);
            var oldPath = Path.Combine(Config.SptServerFolder, "SPT", "user", "mods", detectedServerModName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(oldPath))
                return true;
        }

        return false;
    }

    private static bool IsConfigLikePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        var normalized = relativePath.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);
        var extension = Path.GetExtension(fileName);

        if (fileName.Contains("config", StringComparison.OrdinalIgnoreCase))
            return true;
        if (normalized.Contains("/config/", StringComparison.OrdinalIgnoreCase))
            return true;

        return extension.Equals(".cfg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".toml", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".ini", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryFindPathIndex(string[] segments, string first, string second, out int index)
    {
        for (var i = 0; i <= segments.Length - 2; i++)
        {
            if (string.Equals(segments[i], first, StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[i + 1], second, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }

    private static bool TryFindPathIndex(string[] segments, string first, string second, string third, out int index)
    {
        for (var i = 0; i <= segments.Length - 3; i++)
        {
            if (string.Equals(segments[i], first, StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[i + 1], second, StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[i + 2], third, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }

    private static string PromoteServerStateToUpdate(string state)
    {
        if (string.Equals(state, ServerStateAddBoth, StringComparison.OrdinalIgnoreCase))
            return ServerStateUpdateBoth;
        if (string.Equals(state, ServerStateAddDb, StringComparison.OrdinalIgnoreCase))
            return ServerStateUpdateDb;
        if (string.Equals(state, ServerStateAddLocal, StringComparison.OrdinalIgnoreCase))
            return ServerStateUpdateLocal;
        if (string.Equals(state, "add", StringComparison.OrdinalIgnoreCase))
            return "update";
        return state;
    }

    private string? QueueServerPendingFromBundleZip(string? zipPath, string serverPendingState, string? overrideServerVersion = null)
    {
        if (string.IsNullOrWhiteSpace(zipPath)
            || !zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(zipPath))
        {
            return null;
        }

        var bundle = InspectBundleZip(zipPath);
        if (string.IsNullOrWhiteSpace(bundle.ServerModName))
            return null;

        var storedFileName = CopyServerModZipToStorage(zipPath, bundle.ServerModName + ".zip");
        var storedZipPath = ResolvePendingServerZipPath(storedFileName);
        var serverVersion = string.IsNullOrWhiteSpace(bundle.ServerModVersion)
            ? ExtractServerModVersionFromZip(storedZipPath, bundle.ServerModName)
            : bundle.ServerModVersion!;
        if (!string.IsNullOrWhiteSpace(overrideServerVersion))
            serverVersion = overrideServerVersion;

        var oldVersion = GetCurrentServerVersionForPending(bundle.ServerModName);
        serverVersion = NormalizeVersionForStorage(serverVersion, serverVersion);
        if (IsVersionPlaceholder(serverVersion))
            throw new InvalidDataException("Server mod version could not be read from the selected archive.");
        var effectiveServerState = string.IsNullOrWhiteSpace(oldVersion)
            ? serverPendingState
            : PromoteServerStateToUpdate(serverPendingState);
        _pendingServerChanges[bundle.ServerModName] = new ServerModInfo
        {
            Name = bundle.ServerModName,
            Version = oldVersion,
            NewVersion = serverVersion,
            FileName = storedFileName,
            PendingChangeState = effectiveServerState
        };

        return bundle.ServerModName;
    }

    private string GetCurrentServerVersionForPending(string serverModName)
    {
        if (_pendingServerChanges.TryGetValue(serverModName, out var pendingServer))
        {
            return string.IsNullOrWhiteSpace(pendingServer.NewVersion)
                ? pendingServer.Version
                : pendingServer.NewVersion!;
        }

        if (ServerModsListView.ItemsSource is IEnumerable<ServerModViewModel> serverMods)
        {
            var existing = serverMods.FirstOrDefault(m => string.Equals(m.Name, serverModName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (!string.Equals(existing.DbVersion, "-", StringComparison.OrdinalIgnoreCase))
                    return existing.DbVersion;

                if (!string.Equals(existing.LocalVersion, "-", StringComparison.OrdinalIgnoreCase))
                    return existing.LocalVersion;
            }
        }

        return string.Empty;
    }

    private void QueueClientPendingFromBundleZip(
        string zipPath,
        string clientPendingState,
        string? overridePluginVersion = null,
        bool? overrideIsFolderMod = null,
        bool? overrideAllowOnHeadless = null,
        bool? overrideIsOptional = null,
        bool? overrideOptionalDefaultState = null)
    {
        if (string.IsNullOrWhiteSpace(zipPath)
            || !zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(zipPath))
        {
            return;
        }

        var bundle = InspectBundleZip(zipPath);
        if (string.IsNullOrWhiteSpace(bundle.ClientModName))
            return;

        var detectedPluginVersion = NormalizeVersionForStorage(overridePluginVersion, TryExtractPluginVersionFromZip(zipPath, bundle.ClientModName) ?? string.Empty);
        var existingClient = TryGetCurrentClientMod(bundle.ClientModName);
        var isFolderMod = bundle.IsClientFolderMod ?? true; // default true for ZIPs when uncertain
        if (overrideIsFolderMod.HasValue)
            isFolderMod = overrideIsFolderMod.Value;
        var pendingClient = existingClient ?? new ModInfo
        {
            Name = bundle.ClientModName,
            Version = existingClient?.Version ?? "0.0.0",
            IsFolderMod = isFolderMod,
            AllowOnHeadless = existingClient?.AllowOnHeadless ?? false,
            IsOptional = existingClient?.IsOptional ?? false,
            OptionalDefaultState = existingClient?.OptionalDefaultState ?? false
        };

        pendingClient.Name = bundle.ClientModName;
        pendingClient.IsFolderMod = isFolderMod;
        if (overrideAllowOnHeadless.HasValue)
            pendingClient.AllowOnHeadless = overrideAllowOnHeadless.Value;
        if (overrideIsOptional.HasValue)
            pendingClient.IsOptional = overrideIsOptional.Value;
        if (overrideOptionalDefaultState.HasValue)
            pendingClient.OptionalDefaultState = overrideOptionalDefaultState.Value;
        pendingClient.FileName = StageClientPendingFile(zipPath, bundle.ClientModName + ".zip");

        var shouldQueueAsUpdate = existingClient != null
                                  && (string.Equals(clientPendingState, "update", StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(clientPendingState, "add", StringComparison.OrdinalIgnoreCase));

        if (shouldQueueAsUpdate)
        {
            pendingClient.PendingChangeState = "update";
            pendingClient.NewVersion = NormalizeVersionForStorage(detectedPluginVersion, NormalizeVersionForStorage(pendingClient.Version, "0.0.0"));
            if (string.IsNullOrWhiteSpace(pendingClient.Version))
                pendingClient.Version = "0.0.0";
        }
        else
        {
            pendingClient.PendingChangeState = "add";
            pendingClient.NewVersion = null;
            pendingClient.Version = NormalizeVersionForStorage(detectedPluginVersion, NormalizeVersionForStorage(pendingClient.Version, "0.0.0"));
        }

        _pendingChanges[pendingClient.Name] = pendingClient;
    }

    private ModInfo? TryGetCurrentClientMod(string modName)
    {
        if (_pendingChanges.TryGetValue(modName, out var pending))
        {
            return CloneModInfo(pending);
        }

        if (ModListView.ItemsSource is IEnumerable<ModInfo> mods)
        {
            var existing = mods.FirstOrDefault(m => string.Equals(m.Name, modName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                return CloneModInfo(existing);
            }
        }

        return null;
    }

    private static ModInfo CloneModInfo(ModInfo source)
    {
        return new ModInfo
        {
            Name = source.Name,
            Version = source.Version,
            FileName = source.FileName,
            IsFolderMod = source.IsFolderMod,
            AllowOnHeadless = source.AllowOnHeadless,
            IsOptional = source.IsOptional,
            OptionalDefaultState = source.OptionalDefaultState,
            PendingChangeState = source.PendingChangeState,
            NewVersion = source.NewVersion
        };
    }

    private bool TryQueueServerChangeFromLocal(ServerModViewModel selected, string pendingState, string oldVersion)
    {
        var serverModsPath = Path.Combine(Config.SptServerFolder, "SPT", "user", "mods");
        var modFolderPath = Path.Combine(serverModsPath, selected.Name);
        if (!Directory.Exists(modFolderPath))
            return false;

        var zipFileName = ZipServerModFolder(selected.Name, modFolderPath);
        _pendingServerChanges[selected.Name] = new ServerModInfo
        {
            Name = selected.Name,
            Version = oldVersion,
            NewVersion = selected.LocalVersion,
            FileName = zipFileName,
            PendingChangeState = pendingState
        };
        return true;
    }

    private bool TryQueueServerChangeFromZip(ServerModViewModel selected, string pendingState, string oldVersion, out bool canceled)
    {
        canceled = false;
        string? tempFilteredZipPath = null;
        string? tempConvertedArchivePath = null;

        var picker = new OpenFileDialog
        {
            Title = $"Select server mod archive for '{selected.Name}'",
            Filter = "Archive files (*.zip;*.7z)|*.zip;*.7z|ZIP files (*.zip)|*.zip|7Z files (*.7z)|*.7z|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            FileName = selected.FileName
        };

        if (picker.ShowDialog() != true)
        {
            canceled = true;
            return false;
        }

        try
        {
            var sourceArchivePath = ConvertArchiveToZipIfNeeded(picker.FileName, out tempConvertedArchivePath);

            if (!ShowIncomingFilePreview(sourceArchivePath, null, selected.Name,
                    out var keepOldConfigs, out var serverModsWithConfigConflicts, out var excludedSourcePaths,
                    out var selectedPluginVersion, out var selectedServerVersion,
                    out var selectedPluginIsFolderMod, out var selectedPluginAllowOnHeadless,
                    out var selectedPluginIsOptional, out var selectedPluginOptionalDefaultState))
            {
                canceled = true;
                return false;
            }

            var selectedZipPath = sourceArchivePath;
            if (selectedZipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && excludedSourcePaths.Count > 0)
            {
                tempFilteredZipPath = CreateFilteredZipFromSelection(selectedZipPath, excludedSourcePaths);
                selectedZipPath = tempFilteredZipPath;
            }

            if (serverModsWithConfigConflicts.Count == 0)
            {
                _ = keepOldConfigs;
            }
            else
            {
                _ = keepOldConfigs;
                _ = serverModsWithConfigConflicts;
            }

            var storedFileName = CopyServerModZipToStorage(selectedZipPath, selected.Name + ".zip");
            var storedZipPath = ResolvePendingServerZipPath(storedFileName);
            var newVersion = ExtractServerModVersionFromZip(storedZipPath, selected.Name);
            newVersion = NormalizeVersionForStorage(selectedServerVersion, newVersion);
            if (IsVersionPlaceholder(newVersion))
                throw new InvalidDataException("Server mod version could not be read from the selected archive.");
            var effectivePendingState = string.IsNullOrWhiteSpace(oldVersion)
                ? pendingState
                : PromoteServerStateToUpdate(pendingState);

            var infoText =
                $"Mod: {selected.Name}\n" +
                $"Old DB Version: {selected.DbVersion}\n" +
                $"Old Local Version: {selected.LocalVersion}\n" +
                $"New Version (from ZIP): {newVersion}\n" +
                $"ZIP File: {storedFileName}\n\n" +
                $"Queued action: {effectivePendingState}\n" +
                "Queue this change?";

            var confirm = MessageBox.Show(infoText, "Confirm Server Mod File", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                return false;
            }

            _pendingServerChanges[selected.Name] = new ServerModInfo
            {
                Name = selected.Name,
                Version = oldVersion,
                NewVersion = newVersion,
                FileName = storedFileName,
                PendingChangeState = effectivePendingState
            };

            var clientPendingState = string.Equals(effectivePendingState, ServerStateUpdateBoth, StringComparison.OrdinalIgnoreCase)
                || string.Equals(effectivePendingState, ServerStateUpdateDb, StringComparison.OrdinalIgnoreCase)
                || string.Equals(effectivePendingState, ServerStateUpdateLocal, StringComparison.OrdinalIgnoreCase)
                ? "update"
                : "add";
            QueueClientPendingFromBundleZip(
                selectedZipPath,
                clientPendingState,
                selectedPluginVersion,
                selectedPluginIsFolderMod,
                selectedPluginAllowOnHeadless,
                selectedPluginIsOptional,
                selectedPluginOptionalDefaultState);

            return true;
        }
        finally
        {
            CleanupTempFilteredZip(tempFilteredZipPath);
            CleanupTempWorkingFile(tempConvertedArchivePath);
        }
    }

    private static void CleanupTempFilteredZip(string? tempFilteredZipPath)
    {
        if (!string.IsNullOrWhiteSpace(tempFilteredZipPath) && File.Exists(tempFilteredZipPath))
        {
            File.Delete(tempFilteredZipPath);
        }
    }

    private string CopyServerModZipToStorage(string sourceZipPath, string? preferredFileName = null)
    {
        if (_exeFolder == null)
            throw new InvalidOperationException("Executable folder not determined.");

        var fileName = string.IsNullOrWhiteSpace(preferredFileName)
            ? Path.GetFileName(sourceZipPath)
            : preferredFileName;
        return StageServerPendingZip(sourceZipPath, fileName);
    }

    private static string ExtractServerModVersionFromZip(string zipPath, string? serverModName = null)
    {
        string? tempExtractRoot = null;
        try
        {
            tempExtractRoot = ExtractZipToTempFolder(zipPath);

            var dllCandidates = Directory.GetFiles(tempExtractRoot, "*.dll", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}BepInEx{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                               && !path.Contains($"/BepInEx/", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (dllCandidates.Count == 0)
                return "0.0.0";

            if (!string.IsNullOrWhiteSpace(serverModName))
            {
                var exactServerDll = dllCandidates
                    .FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), serverModName, StringComparison.OrdinalIgnoreCase));

                if (exactServerDll != null)
                {
                    var exactVersion = TryReadDllVersion(exactServerDll);
                    return IsVersionPlaceholder(exactVersion) ? "0.0.0" : exactVersion!;
                }

                var serverFolderDll = dllCandidates
                    .FirstOrDefault(path => PathContainsArchiveSegment(path.Replace('\\', '/'), serverModName));
                if (serverFolderDll != null)
                {
                    var folderVersion = TryReadDllVersion(serverFolderDll);
                    return IsVersionPlaceholder(folderVersion) ? "0.0.0" : folderVersion!;
                }

                return "0.0.0";
            }

            var preferredDll = dllCandidates.FirstOrDefault();

            var dllVersion = TryReadDllVersion(preferredDll!);
            if (!IsVersionPlaceholder(dllVersion))
                return dllVersion!;
        }
        catch
        {
            // Fall through to default version if ZIP metadata cannot be parsed.
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempExtractRoot) && Directory.Exists(tempExtractRoot))
                Directory.Delete(tempExtractRoot, true);
        }

        return "0.0.0";
    }

    private static string? TryReadDllVersion(string dllPath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(dllPath);
            if (!IsVersionPlaceholder(info.FileVersion))
                return info.FileVersion;
            if (!IsVersionPlaceholder(info.ProductVersion))
                return info.ProductVersion;
        }
        catch
        {
            // Continue with assembly metadata fallback.
        }

        try
        {
            var assemblyName = System.Reflection.AssemblyName.GetAssemblyName(dllPath);
            var version = assemblyName.Version?.ToString();
            if (!IsVersionPlaceholder(version))
                return version;
        }
        catch
        {
            // Ignore invalid/non-.NET assembly metadata.
        }

        return null;
    }

    private static string? TryExtractPluginVersionFromZip(string zipPath, string? clientModName, out string? selectedEntryPath, out string? selectedDllName)
    {
        selectedEntryPath = null;
        selectedDllName = null;
        string? tempExtractRoot = null;

        try
        {
            tempExtractRoot = ExtractZipToTempFolder(zipPath);
            var allDllEntries = Directory.GetFiles(tempExtractRoot, "*.dll", SearchOption.AllDirectories).ToList();

            if (allDllEntries.Count == 0)
                return null;

            if (string.IsNullOrWhiteSpace(clientModName))
                return null;

            var pluginRootPath = Path.Combine(tempExtractRoot, "BepInEx", "plugins");
            var pluginDllEntries = allDllEntries
                .Where(path => path.StartsWith(pluginRootPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            string? targetEntry = null;

            // Primary rule: version comes from /BepInEx/plugins/**/<ModName>.dll.
            if (!string.IsNullOrWhiteSpace(clientModName) && pluginDllEntries.Count > 0)
            {
                targetEntry = pluginDllEntries
                    .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), clientModName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path.Count(ch => ch == '/' || ch == '\\'))
                    .FirstOrDefault();

                // For folder mods where dll name differs, prefer dlls inside /BepInEx/plugins/<ModName>/...
                targetEntry ??= pluginDllEntries
                    .Where(path => path.Contains($"{Path.DirectorySeparatorChar}BepInEx{Path.DirectorySeparatorChar}plugins{Path.DirectorySeparatorChar}{clientModName}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                                   || path.Replace('\\', '/').Contains($"/BepInEx/plugins/{clientModName}/", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path.Count(ch => ch == '/' || ch == '\\'))
                    .FirstOrDefault();
            }

            // Secondary: exact mod DLL name anywhere in archive.
            if (targetEntry == null && !string.IsNullOrWhiteSpace(clientModName))
            {
                targetEntry = allDllEntries
                    .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), clientModName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path.Count(ch => ch == '/' || ch == '\\'))
                    .FirstOrDefault();
            }


            if (targetEntry == null)
                return null;

            selectedEntryPath = targetEntry;
            selectedDllName = Path.GetFileName(targetEntry);
            return TryReadDllVersion(targetEntry);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempExtractRoot) && Directory.Exists(tempExtractRoot))
                Directory.Delete(tempExtractRoot, true);
        }
    }

    private static string? TryExtractPluginVersionFromZip(string zipPath, string? clientModName)
    {
        return TryExtractPluginVersionFromZip(zipPath, clientModName, out _, out _);
    }

    private static ServerModInfo BuildServerChangeFromDb(ServerModViewModel selected, string pendingState, string oldVersion)
    {
        return new ServerModInfo
        {
            Name = selected.Name,
            Version = oldVersion,
            NewVersion = selected.DbVersion,
            FileName = selected.FileName,
            PendingChangeState = pendingState
        };
    }

    private void CompleteQueueServerChanges(int queued, int skipped, string actionText, string noEligibleText)
    {
        if (queued == 0)
        {
            MessageBox.Show(noEligibleText, "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SaveServerPendingChangesToDatabase();
        RefreshServerModsListView();
        RefreshPendingChanges_Internal();
        MessageBox.Show($"Queued {queued} server mod(s) for {actionText}." + (skipped > 0 ? $" Skipped: {skipped}." : string.Empty), "Success", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void AddServerMod_Click(object sender, RoutedEventArgs e)
    {
        AddMod_Click(sender, e);
    }

    private void AddServerModToDb_Click(object sender, RoutedEventArgs e)
    {
        var selectedMods = ServerModsListView.SelectedItems.Cast<ServerModViewModel>().ToList();
        if (selectedMods.Count == 0)
        {
            MessageBox.Show("Please select one or more server mods to add to the database.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var queued = 0;
            var skipped = 0;

            foreach (var selected in selectedMods)
            {
                if (HasDb(selected) || !HasLocal(selected))
                {
                    skipped++;
                    continue;
                }

                if (!TryQueueServerChangeFromLocal(selected, ServerStateAddDb, selected.DbVersion == "-" ? string.Empty : selected.DbVersion))
                {
                    skipped++;
                    continue;
                }

                queued++;
            }

            CompleteQueueServerChanges(queued, skipped, "database addition", "No selected server mods were eligible to add to database (already in DB or not installed locally).");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to queue server mod addition: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateServerMod_Click(object sender, RoutedEventArgs e)
    {
        var selectedMods = ServerModsListView.SelectedItems.Cast<ServerModViewModel>().ToList();
        if (selectedMods.Count == 0)
        {
            MessageBox.Show("Please select one or more server mods to update.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var queued = 0;
            var skipped = 0;

            foreach (var selected in selectedMods)
            {
                if (!HasLocal(selected) && !HasDb(selected))
                {
                    skipped++;
                    continue;
                }

                var oldVersion = selected.DbVersion == "-" ? selected.LocalVersion : selected.DbVersion;
                if (!TryQueueServerChangeFromZip(selected, ServerStateUpdateBoth, oldVersion, out var canceled))
                {
                    if (canceled)
                        return;

                    skipped++;
                    continue;
                }

                queued++;
            }

            CompleteQueueServerChanges(queued, skipped, "update (database + local)", "No selected server mods were eligible to update.");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to queue server mod update: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveServerMod_Click(object sender, RoutedEventArgs e)
    {
        var selectedMods = ServerModsListView.SelectedItems.Cast<ServerModViewModel>().ToList();
        if (selectedMods.Count == 0)
        {
            MessageBox.Show("Please select one or more server mods to remove.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"Queue {selectedMods.Count} selected server mod(s) for removal from database and local install?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            var queued = 0;
            var skipped = 0;

            foreach (var selected in selectedMods)
            {
                if (!HasLocal(selected) && !HasDb(selected))
                {
                    skipped++;
                    continue;
                }

                _pendingServerChanges[selected.Name] = new ServerModInfo
                {
                    Name = selected.Name,
                    Version = HasDb(selected) ? selected.DbVersion : selected.LocalVersion,
                    FileName = selected.FileName,
                    PendingChangeState = ServerStateDeleteBoth
                };
                queued++;
            }

            CompleteQueueServerChanges(queued, skipped, "removal (database + local)", "No selected server mods were eligible to remove.");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to queue server mod removal: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateServerModToDb_Click(object sender, RoutedEventArgs e)
    {
        var selectedMods = ServerModsListView.SelectedItems.Cast<ServerModViewModel>().ToList();
        if (selectedMods.Count == 0)
        {
            MessageBox.Show("Please select one or more server mods to update in database.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var queued = 0;
            var skipped = 0;

            foreach (var selected in selectedMods)
            {
                if (!HasLocal(selected) || !HasDb(selected))
                {
                    skipped++;
                    continue;
                }

                if (!TryQueueServerChangeFromLocal(selected, ServerStateUpdateDb, selected.DbVersion))
                {
                    skipped++;
                    continue;
                }

                queued++;
            }

            CompleteQueueServerChanges(queued, skipped, "database update", "No selected server mods were eligible to update in database.");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to queue server mod DB update: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveServerModFromDb_Click(object sender, RoutedEventArgs e)
    {
        var selectedMods = ServerModsListView.SelectedItems.Cast<ServerModViewModel>().ToList();
        if (selectedMods.Count == 0)
        {
            MessageBox.Show("Please select one or more server mods to remove from database.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"Queue {selectedMods.Count} selected server mod(s) for removal from the database?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            var queued = 0;
            var skipped = 0;
            foreach (var selected in selectedMods)
            {
                if (!HasDb(selected))
                {
                    skipped++;
                    continue;
                }

                var pending = new ServerModInfo
                {
                    Name = selected.Name,
                    Version = selected.DbVersion,
                    FileName = selected.FileName,
                    PendingChangeState = ServerStateDeleteDb
                };
                _pendingServerChanges[selected.Name] = pending;
                queued++;
            }

            CompleteQueueServerChanges(queued, skipped, "database removal", "No selected server mods were found in database.");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to queue server mod removal: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddServerModToLocal_Click(object sender, RoutedEventArgs e)
    {
        var selectedMods = ServerModsListView.SelectedItems.Cast<ServerModViewModel>().ToList();
        if (selectedMods.Count == 0)
        {
            MessageBox.Show("Please select one or more server mods to add locally.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var queued = 0;
            var skipped = 0;

            foreach (var selected in selectedMods)
            {
                if (!HasDb(selected) || HasLocal(selected))
                {
                    skipped++;
                    continue;
                }

                _pendingServerChanges[selected.Name] = BuildServerChangeFromDb(selected, ServerStateAddLocal, string.Empty);
                queued++;
            }

            CompleteQueueServerChanges(queued, skipped, "local addition", "No selected server mods were eligible to add locally (must exist in DB and not be installed locally).");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to queue local add: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateServerModLocal_Click(object sender, RoutedEventArgs e)
    {
        var selectedMods = ServerModsListView.SelectedItems.Cast<ServerModViewModel>().ToList();
        if (selectedMods.Count == 0)
        {
            MessageBox.Show("Please select one or more server mods to update locally.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var queued = 0;
            var skipped = 0;

            foreach (var selected in selectedMods)
            {
                if (!HasDb(selected) || !HasLocal(selected))
                {
                    skipped++;
                    continue;
                }

                if (string.Equals(selected.LocalVersion, selected.DbVersion, StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                _pendingServerChanges[selected.Name] = BuildServerChangeFromDb(selected, ServerStateUpdateLocal, selected.LocalVersion);
                queued++;
            }

            CompleteQueueServerChanges(queued, skipped, "local update", "No selected server mods were eligible to update locally.");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to queue local update: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveServerModLocal_Click(object sender, RoutedEventArgs e)
    {
        var selectedMods = ServerModsListView.SelectedItems.Cast<ServerModViewModel>().ToList();
        if (selectedMods.Count == 0)
        {
            MessageBox.Show("Please select one or more server mods to remove locally.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"Queue {selectedMods.Count} selected server mod(s) for local removal?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            var queued = 0;
            var skipped = 0;

            foreach (var selected in selectedMods)
            {
                if (!HasLocal(selected))
                {
                    skipped++;
                    continue;
                }

                _pendingServerChanges[selected.Name] = new ServerModInfo
                {
                    Name = selected.Name,
                    Version = selected.LocalVersion,
                    FileName = selected.FileName,
                    PendingChangeState = ServerStateDeleteLocal
                };
                queued++;
            }

            CompleteQueueServerChanges(queued, skipped, "local removal", "No selected server mods were installed locally.");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to queue local removal: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ──────────────────── Server Pending Changes persistence ────────────────────

    private void LoadServerPendingChangesFromDatabase()
    {
        if (string.IsNullOrWhiteSpace(_databasePath)) return;
        try
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();
            EnsureSptCoffeeSchema(connection);

            _pendingServerChanges.Clear();
            _serverConfigConflictDecisions.Clear();

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT mod_name, change_type, old_version, new_version, file_name
FROM server_pending_changes
ORDER BY id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var mod = new ServerModInfo
                {
                    Name = reader.GetString(0),
                    PendingChangeState = reader.GetString(1),
                    Version = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    NewVersion = reader.IsDBNull(3) || string.IsNullOrEmpty(reader.GetString(3)) ? null : reader.GetString(3),
                    FileName = reader.IsDBNull(4) ? string.Empty : reader.GetString(4)
                };
                _pendingServerChanges[mod.Name] = mod;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load server pending changes: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveServerPendingChangesToDatabase()
    {
        if (string.IsNullOrWhiteSpace(_databasePath)) return;
        try
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();
            EnsureSptCoffeeSchema(connection);

            using var tx = connection.BeginTransaction();

            using (var clearCmd = connection.CreateCommand())
            {
                clearCmd.Transaction = tx;
                clearCmd.CommandText = "DELETE FROM server_pending_changes;";
                clearCmd.ExecuteNonQuery();
            }

            foreach (var (name, mod) in _pendingServerChanges)
            {
                using var insertCmd = connection.CreateCommand();
                insertCmd.Transaction = tx;
                insertCmd.CommandText = @"
INSERT INTO server_pending_changes(mod_name, change_type, old_version, new_version, file_name, created_utc)
VALUES($modName, $changeType, $oldVersion, $newVersion, $fileName, $createdUtc);";
                insertCmd.Parameters.AddWithValue("$modName", name);
                insertCmd.Parameters.AddWithValue("$changeType", mod.PendingChangeState);
                insertCmd.Parameters.AddWithValue("$oldVersion", mod.Version ?? string.Empty);
                insertCmd.Parameters.AddWithValue("$newVersion", mod.NewVersion ?? string.Empty);
                insertCmd.Parameters.AddWithValue("$fileName", mod.FileName ?? string.Empty);
                insertCmd.Parameters.AddWithValue("$createdUtc", DateTime.UtcNow.ToString("O"));
                insertCmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save server pending changes: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public class ServerConfig
{
    public int Port { get; set; } = 25569;
    public string SptServerFolder { get; set; } = @"C:\SPT";
    public string AdditionalModsPath { get; set; } = "";
    public string HeadlessFolder { get; set; } = "";
    public string LocalHeadlessPlayerId { get; set; } = "";
}

public class BootstrapConfig
{
    public int Port { get; set; } = 25569;
    public string DatabaseFileName { get; set; } = "MainDatabase\\SPTCoffee.db";
    public bool RestartServersIfUpdateStartedOnline { get; set; }
}

public class InstalledPluginViewModel
{
    public string Name { get; set; } = string.Empty;
    public string FileVersion { get; set; } = string.Empty;
    public string DbVersion { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public System.Windows.Media.Brush StatusBrush { get; set; } = System.Windows.Media.Brushes.White;
    public string StatusFontWeight { get; set; } = "Normal";
}

public class ServerModViewModel
{
    public string Name { get; set; } = string.Empty;
    public string DbVersion { get; set; } = string.Empty;
    public string LocalVersion { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public System.Windows.Media.Brush StatusBrush { get; set; } = System.Windows.Media.Brushes.White;
    public string StatusFontWeight { get; set; } = "Normal";
    /// <summary>Pending state key (legacy: add/delete/update, new: *_both/*_db/*_local).</summary>
    public string PendingChangeState { get; set; } = string.Empty;
    public string PendingStateKind => PendingChangeState.StartsWith("add", StringComparison.OrdinalIgnoreCase) ? "add"
        : PendingChangeState.StartsWith("delete", StringComparison.OrdinalIgnoreCase) ? "delete"
        : PendingChangeState.StartsWith("update", StringComparison.OrdinalIgnoreCase) ? "update"
        : string.Empty;
    public string PendingStateLabel => PendingChangeState.ToLowerInvariant() switch
    {
        "add" => "Waiting for addition",
        "update" => "Waiting for update",
        "delete" => "Waiting for deletion",
        "add_both" => "Waiting: add DB + local",
        "update_both" => "Waiting: update DB + local",
        "delete_both" => "Waiting: remove DB + local",
        "add_db" => "Waiting: add to DB",
        "update_db" => "Waiting: update DB",
        "delete_db" => "Waiting: remove from DB",
        "add_local" => "Waiting: add to local",
        "update_local" => "Waiting: update local",
        "delete_local" => "Waiting: remove from local",
        _ => string.Empty
    };
}

public class PendingChangeEntry
{
    public string Name { get; set; } = string.Empty;
    /// <summary>"Client" or "Server"</summary>
    public string ModType { get; set; } = "Client";
    /// <summary>Pending state key (legacy add/delete/update, plus server *_both/*_db/*_local).</summary>
    public string PendingChangeState { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? NewVersion { get; set; }
    public bool IsServerMod => string.Equals(ModType, "Server", StringComparison.OrdinalIgnoreCase);
    public string PendingStateKind => PendingChangeState.StartsWith("add", StringComparison.OrdinalIgnoreCase) ? "add"
        : PendingChangeState.StartsWith("delete", StringComparison.OrdinalIgnoreCase) ? "delete"
        : PendingChangeState.StartsWith("update", StringComparison.OrdinalIgnoreCase) ? "update"
        : string.Empty;
    public string PendingStateLabel => PendingChangeState.ToLowerInvariant() switch
    {
        "add" => "Addition",
        "update" => "Update",
        "delete" => "Deletion",
        "add_both" => "Add DB + local",
        "update_both" => "Update DB + local",
        "delete_both" => "Remove DB + local",
        "add_db" => "Add to DB",
        "update_db" => "Update DB",
        "delete_db" => "Remove DB",
        "add_local" => "Add to local",
        "update_local" => "Update local",
        "delete_local" => "Remove local",
        _ => string.Empty
    };
}

