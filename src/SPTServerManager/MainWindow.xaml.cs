using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.SignalR.Client;
using System.Windows.Media;
using System.Windows.Threading;
using SQLitePCL;
using SPTCoffee.Contracts.Models;
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

    private bool _isHeadlessWaitingToBeStarted = false;
    private bool _isHeadlessAutoStartInProgress = false;
    private bool _prevHeadlessRunning = false;
    private bool _prevSptServerRunning = false;
    private bool _headlessRestartNotified = false;
    private CancellationTokenSource? _headlessAutoStartCts;

    private HubConnection? _hubConnection;

    private static string BaseUrl => "http://localhost:25569";

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

        // Load admin list
        RefreshAdminListView();

        // Load mod list
        RefreshModListView();

        // Load config list
        RefreshConfigListView();

        // Load installed plugins list
        RefreshInstalledPlugins_Click(this, new RoutedEventArgs());

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
                DatabaseFileName = GetDatabasePathForBootstrapSave()
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
);";
        command.ExecuteNonQuery();
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
    }

    private static string GetPluginZipFolder(string rootFolder) => Path.Combine(rootFolder, PluginZipFolderName);
    private static string GetAdditionalModsFolder(string rootFolder) => Path.Combine(rootFolder, AdditionalModsFolderName);
    private static string GetMainDatabaseFolder(string rootFolder) => Path.Combine(rootFolder, MainDatabaseFolderName);
    private static string GetSptUpdateFolder(string rootFolder) => Path.Combine(rootFolder, SptUpdateFolderName);
    private static string GetConfigFilesFolder(string rootFolder) => Path.Combine(rootFolder, ConfigFilesFolderName);

    private static void SaveManagerSettingsToDatabase(SqliteConnection connection, ServerConfig config)
    {
        UpsertSetting(connection, "spt_server_folder", config.SptServerFolder);
        UpsertSetting(connection, "additional_mods_path", config.AdditionalModsPath);
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
            ModListView.ItemsSource = LoadModsFromDatabase(connection);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to load mod list: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshModList_Click(object sender, RoutedEventArgs e) => RefreshModListView();

    private void AddMod_Click(object sender, RoutedEventArgs e)
    {
        var editWindow = new ModEditWindow();
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
            MigratePluginsSchema(connection);
            UpsertModInDatabase(connection, editWindow.Result!);
            RefreshModListView();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to save mod: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EditMod_Click(object sender, RoutedEventArgs e)
    {
        if (ModListView.SelectedItem is not ModInfo selected)
        {
            MessageBox.Show("Please select a mod to edit.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var editWindow = new ModEditWindow(selected);
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
            MigratePluginsSchema(connection);
            UpsertModInDatabase(connection, editWindow.Result!);
            RefreshModListView();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to save mod: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveMod_Click(object sender, RoutedEventArgs e)
    {
        if (ModListView.SelectedItem is not ModInfo selected)
        {
            MessageBox.Show("Please select a mod to remove.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"Remove mod \"{selected.Name}\" from the database?", "Confirm",
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
            DeleteModFromDatabase(connection, selected.Name);
            RefreshModListView();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to remove mod: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
        if (AdminListView.SelectedItem is not AdminConfig selectedAdmin)
        {
            MessageBox.Show("Please select an admin to edit.", "Info", MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
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
        // Get selected admin
        if (AdminListView.SelectedItem is not AdminConfig selectedAdmin)
        {
            MessageBox.Show("Please select an admin to remove.", "Info", MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Confirm removal
        var result = MessageBox.Show($"Are you sure you want to remove admin: {selectedAdmin.Note}?", "Confirm",
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

            var adminList = LoadAdminsFromDatabase(connection);
            adminList.RemoveAll(a => a.Secret == selectedAdmin.Secret);
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
        if (ConfigListView.SelectedItem is not ConfigInfo selected)
        {
            MessageBox.Show("Please select a config to edit.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

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
        if (ConfigListView.SelectedItem is not ConfigInfo selected)
        {
            MessageBox.Show("Please select a config to remove.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"Remove config \"{selected.FileName}\" from the database?", "Confirm",
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
            DeleteConfigFromDatabase(connection, selected.FileName);
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

        // Discover plugins from SPT server folder (used for both Server and Headless)
        var pluginsPath = Path.Combine(Config.SptServerFolder, "BepInEx", "plugins");
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
        if (InstalledPluginsListView.SelectedItem is not InstalledPluginViewModel selected)
        {
            MessageBox.Show("Please select a mod to allow on headless.", "Info", MessageBoxButton.OK,
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

            // Load the mod and update its AllowOnHeadless flag
            var mods = LoadModsFromDatabase(connection);
            var modToUpdate = mods.FirstOrDefault(m => string.Equals(m.Name, selected.Name, StringComparison.OrdinalIgnoreCase));

            if (modToUpdate == null)
            {
                // Mod not in database, create new entry
                modToUpdate = new ModInfo
                {
                    Name = selected.Name,
                    Version = selected.DbVersion,
                    FileName = selected.Name + ".zip",
                    IsFolderMod = false,
                    AllowOnHeadless = true
                };
            }
            else
            {
                modToUpdate.AllowOnHeadless = true;
            }

            UpsertModInDatabase(connection, modToUpdate);
            RefreshInstalledPlugins_Click(this, new RoutedEventArgs());
            MessageBox.Show($"Mod \"{selected.Name}\" is now allowed on headless.", "Success",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to update mod: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public class ServerConfig
{
    public int Port { get; set; } = 25569;
    public string SptServerFolder { get; set; } = @"C:\SPT";
    public string AdditionalModsPath { get; set; } = "";
}

public class BootstrapConfig
{
    public int Port { get; set; } = 25569;
    public string DatabaseFileName { get; set; } = "MainDatabase\\SPTCoffee.db";
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

