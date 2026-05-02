using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.AspNetCore.SignalR.Client;
using System.Windows.Media;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;

namespace SPTServerManager;

public partial class MainWindow : Window
{
    private ServerConfig Config { get; set; } = new();

    private readonly string? _configPath;
    private readonly string? _adminConfigPath;
    private readonly string? _launcherSettingsPath;
    private readonly string? _exeFolder;

    private readonly DispatcherTimer _launcherButtonCooldownTimer = new();
    private readonly DispatcherTimer _sptServerButtonCooldownTimer = new();
    private readonly DispatcherTimer _headlessManagerButtonCooldownTimer = new();

    private bool _isHeadlessWaitingToBeStarted = false;
    private bool _prevHeadlessRunning = false;
    private bool _prevSptServerRunning = false;
    private bool _headlessRestartNotified = false;

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
        LoadConfig();

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
            .WithUrl($"{BaseUrl}/hub")
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
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                Config = JsonSerializer.Deserialize<ServerConfig>(json) ?? new ServerConfig();
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
            var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            if (_configPath != null)
            {
                File.WriteAllText(_configPath, json);
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
            var oldZipFolder = Path.Combine(exeFolder, "temp", "ModZips");
            if (Directory.Exists(oldZipFolder))
            {
                Directory.Delete(oldZipFolder, true);
            }
            // Delete old PluginVersions.json if exists
            var oldJsonPath = Path.Combine(exeFolder, "PluginVersions.json");
            if (File.Exists(oldJsonPath))
            {
                File.Delete(oldJsonPath);
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

                foreach (var path in validPaths)
                {
                    var r = ProcessModFolder(path, exeFolder);
                    created += r.created;
                    updated += r.updated;
                }

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

    private (int created, int updated) ProcessModFolder(string modsPath, string exeFolder)
    {
        var excludedMods = new HashSet<string>(_excludedMods, StringComparer.OrdinalIgnoreCase);
        var excludedModFolders = new HashSet<string>(_excludedModFolders, StringComparer.OrdinalIgnoreCase);

        string zipFolder = Path.Combine(exeFolder, "temp", "ModZips");
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

        // Write version JSON
        string jsonPath = Path.Combine(exeFolder, "PluginVersions.json");

        // Merge with existing `PluginVersions.json` if present
        List<ModInfo> existingList = new();
        if (File.Exists(jsonPath))
        {
            try
            {
                var existingJson = File.ReadAllText(jsonPath);
                existingList = JsonSerializer.Deserialize<List<ModInfo>>(existingJson) ?? new List<ModInfo>();
            }
            catch
            {
                existingList = new List<ModInfo>();
            }
        }

        // Merge by Name (case-insensitive): update existing entries or add new ones
        var existingDict = existingList.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var mod in pluginList)
        {
            if (existingDict.TryGetValue(mod.Name, out var existing))
            {
                existing.Version = mod.Version;
                existing.FileName = mod.FileName;
                existing.IsFolderMod = mod.IsFolderMod;
            }
            else
            {
                existingList.Add(mod);
            }
        }

        // Persist merged list
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(existingList, new JsonSerializerOptions { WriteIndented = true }));

        return (createdCount, updatedCount);
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
            var zipFolder = Path.Combine(_exeFolder, "temp", "ModZips");
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

                foreach (var path in validPaths)
                {
                    createdTotal += ProcessConfigFolder(path, _exeFolder);
                }

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

    private int ProcessConfigFolder(string configsPath, string exeFolder)
    {
        string configFolder = Path.Combine(exeFolder, "temp", "ConfigFiles");
        Directory.CreateDirectory(configFolder);

        var excludedConfigs = new HashSet<string>(_excludedConfigs, StringComparer.OrdinalIgnoreCase);
        var configList = new List<ConfigInfo>();

        int created = 0;

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
            created++;
        }

        string jsonPath = Path.Combine(exeFolder, "ConfigFiles.json");

        // Merge with existing `ConfigFiles.json` if present, preserving existing IsEnforced where applicable
        List<ConfigInfo> existingList = new();
        if (File.Exists(jsonPath))
        {
            try
            {
                var existingJson = File.ReadAllText(jsonPath);
                existingList = JsonSerializer.Deserialize<List<ConfigInfo>>(existingJson) ?? new List<ConfigInfo>();
            }
            catch
            {
                existingList = new List<ConfigInfo>();
            }
        }

        var existingDict = existingList.ToDictionary(c => c.FileName, StringComparer.OrdinalIgnoreCase);
        foreach (var cfg in configList)
        {
            if (existingDict.TryGetValue(cfg.FileName, out var existing))
            {
                // Update last-modified but preserve IsEnforced flag from existing entry
                existing.LastModified = cfg.LastModified;
            }
            else
            {
                existingList.Add(cfg);
            }
        }

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(existingList, new JsonSerializerOptions { WriteIndented = true }));

        return created;
    }


    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow
        {
            LauncherPort = Config.Port.ToString(),
            ServerFolderPath = Config.SptServerFolder,
            AdditionalModsPath = Config.AdditionalModsPath
        };

        if (settingsWindow.ShowDialog() == true)
        {
            if (int.TryParse(settingsWindow.LauncherPort, out var port))
            {
                Config.Port = port;
            }
            Config.SptServerFolder = settingsWindow.ServerFolderPath;
            Config.AdditionalModsPath = settingsWindow.AdditionalModsPath;

            SaveConfig(Config);
        }
    }

    private readonly DispatcherTimer _statusTimer = new();

    private async void StatusTimer_Tick(object? sender, EventArgs e)
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
                        var url = $"http://localhost:{Config.Port}/notify/spt/{endpoint}";
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
                        var url = $"http://localhost:{Config.Port}/notify/headless/{endpoint}";
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

    private async void LauncherButtonCooldownTimer_Tick(object? sender, EventArgs e)
    {
        StartLauncherServerButton.IsEnabled = true;
        _launcherButtonCooldownTimer.Stop();
    }

    private async void SptServerButtonCooldownTimer_Tick(object? sender, EventArgs e)
    {
        StartSptServerButton.IsEnabled = true;
        _sptServerButtonCooldownTimer.Stop();
    }

    private async void HeadlessManagerButtonCooldownTimer_Tick(object? sender, EventArgs e)
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
                    var url = $"{BaseUrl}/notify/spt/offline";
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
                var url = $"{BaseUrl}/notify/headless/offline";
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
            SetHeadlessManagerButtonAsStarting();
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

    // Headless safety timer as the spt server needs to be running before starting headless manager
    private readonly DispatcherTimer _headlessSafetyTimer = new DispatcherTimer();

    private void StartHeadlessWithDelay_Click(object sender, RoutedEventArgs e)
    {
        _headlessSafetyTimer.Interval = TimeSpan.FromSeconds(20);
        _headlessSafetyTimer.Tick += HeadlessSafetyTimer_Tick;
        _headlessSafetyTimer.Start();

        _isHeadlessWaitingToBeStarted = true;
    }

    private void HeadlessSafetyTimer_Tick(object? sender, EventArgs e)
    {
        _headlessSafetyTimer.Stop();
        StartSptHeadlessManager_Click(this, new RoutedEventArgs());
        _isHeadlessWaitingToBeStarted = false;
    }

    private void StopAllServers_Click(object sender, RoutedEventArgs e)
    {
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
        // Check if admin config file exists
        if (!File.Exists(_adminConfigPath))
        {
            return;
        }

        // Read admin config file
        try
        {
            var json = File.ReadAllText(_adminConfigPath);
            var adminList = JsonSerializer.Deserialize<List<AdminConfig>>(json) ?? new List<AdminConfig>();
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

            // Load existing admins
            try
            {
                List<AdminConfig> adminList;
                if (File.Exists(_adminConfigPath))
                {
                    var json = File.ReadAllText(_adminConfigPath);
                    adminList = JsonSerializer.Deserialize<List<AdminConfig>>(json) ?? new List<AdminConfig>();
                }
                else
                {
                    adminList = new List<AdminConfig>();
                }

                // Add new admin
                adminList.Add(newAdmin);

                // Save updated admin list
                var updatedJson = JsonSerializer.Serialize(adminList, new JsonSerializerOptions { WriteIndented = true });
                if (_adminConfigPath != null) File.WriteAllText(_adminConfigPath, updatedJson);
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

            // Save updated admin list
            try
            {
                var adminList = AdminListView.ItemsSource as List<AdminConfig> ?? new List<AdminConfig>();
                var json = JsonSerializer.Serialize(adminList, new JsonSerializerOptions { WriteIndented = true });
                if (_adminConfigPath != null) File.WriteAllText(_adminConfigPath, json);
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

        // Load existing admins
        try
        {
            if (File.Exists(_adminConfigPath))
            {
                var json = File.ReadAllText(_adminConfigPath);
                var adminList = JsonSerializer.Deserialize<List<AdminConfig>>(json) ?? new List<AdminConfig>();

                // Remove selected admin
                adminList.RemoveAll(a => a.Secret == selectedAdmin.Secret);

                // Save updated admin list or delete file if empty
                if (adminList.Count == 0)
                {
                    File.Delete(_adminConfigPath);
                }
                else
                {
                    var updatedJson = JsonSerializer.Serialize(adminList, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_adminConfigPath, updatedJson);
                }
            }
        }
        catch (System.Exception ex)
        {
            MessageBox.Show("Failed to load admin configuration: " + ex.Message, "Error", MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }
    }
}

public class ServerConfig
{
    public int Port { get; set; } = 25569;
    public string SptServerFolder { get; set; } = @"C:\SPT";
    public string AdditionalModsPath { get; set; } = "";
}

public class ConfigInfo
{
    public string FileName { get; set; } = "";
    public DateTime LastModified { get; set; }
    public bool IsEnforced { get; set; } = false; // If true, launcher will get this config file from server on launch
}

public class ModInfo
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string FileName { get; set; } = "";
    public bool IsFolderMod { get; set; } = false;
}

// Admin config so launcher can authenticate admin commands. Array of these in JSON so multiple admins can be set up.
public class AdminConfig
{
    public string Note { get; set; } = ""; // For easy edit, e.g. "My own pc"
    public string Secret { get; set; } = ""; // Like password
    public bool IsEnabled { get; set; } = true; // So can be disabled without deleting
    public bool AllowHeadlessClose { get; set; } = false; // Whether this admin can close headless client
}

// Additional Launcher settings
public class LauncherSettings
{
    // Excluded mods
    public List<string> ExcludedMods { get; set; } = new List<string>();
    // Excluded mods folders
    public List<string> ExcludedModFolders { get; set; } = new List<string>();
    // Excluded config files
    public List<string> ExcludedConfigs { get; set; } = new List<string>();
}
