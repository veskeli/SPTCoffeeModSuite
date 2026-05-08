using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Interop;
using SPTCoffee.Contracts.Models;
using SPTCoffeeModManager.Models;
using SPTCoffeeModManager.Services;

namespace SPTCoffeeModManager;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow
{
    private static readonly HttpClient SharedHttpClient = new();
    private static readonly HttpClient DownloadHttpClient = new() { Timeout = TimeSpan.FromMinutes(120) };

    private readonly string? _modsFolder;
    private readonly string? _pluginsConfigFolder;
    private readonly string? _clientPath;
    private string _basePath = "";

    // IP/Port of your server console
    private string _serverIp = "127.0.0.1";
    private int _serverPort = 25569;
    private string _sptServerAddress = "http://127.0.0.1:6969";
    private string? _secret = "";

    // store exe directory and config path
    private readonly string? _configPath;
    private readonly ModProfileRepository _modProfileRepository = null!;
    private readonly ModProfileFileService _modProfileFileService = null!;
    private ModProfileStore _modProfileStore = new();
    private bool _isUpdatingProfileSelection;

    // SignalR connection is now managed by Services.SignalRService

    // Excluded mod names and folders (fetched from server as well)
    private List<string> _excludedMods = new List<string>
    {
        "spt-common", "spt-core", "spt-custom", "spt-debugging",
        "spt-reflection", "spt-singleplayer", "Fika.Headless"
    };
    private List<string> _excludedModFolders = new List<string>
    {
        "spt"
    };
    private List<string> _excludedConfigs = new List<string>
    {
        "BepInEx.cfg", "com.bepis.bepinex.configurationmanager.cfg",
        "com.fika.headless.cfg"
    };

    public MainWindow()
    {
        try
        {
            var exeDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule!.FileName)!;
            _modsFolder = Path.Combine(exeDir, "BepInEx", "plugins");
            _pluginsConfigFolder = Path.Combine(exeDir, "BepInEx", "config");
            _clientPath = Path.Combine(exeDir, "SPT", "SPT.Launcher.exe");

            // store exe directory and config path
            _configPath = Path.Combine(exeDir, "coffee_manager_server_config.json");

            _basePath = exeDir;

            _modProfileRepository = new ModProfileRepository(_basePath);
            _modProfileFileService = new ModProfileFileService(_basePath, _excludedMods, _excludedModFolders, _excludedConfigs);
        }
        catch
        {
            if (!ConfirmContinueWithoutSptRoot())
            {
                Close();
                return;
            }

            _modProfileRepository = new ModProfileRepository(AppDomain.CurrentDomain.BaseDirectory);
            _modProfileFileService = new ModProfileFileService(AppDomain.CurrentDomain.BaseDirectory, _excludedMods, _excludedModFolders, _excludedConfigs);
        }

        InitializeComponent();
        SetActiveTabButton(HomeTabButton);

        // Apply Windows 11 rounded corners if available
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _ = WindowCornerHelper.TrySetWindowCornerPreference(hwnd, WindowCornerHelper.DwmWindowCorner.Round);
        };

        // Wire up event handlers
        if (HomeTabContent != null)
        {
            HomeTabContent.RefreshButtonRef.Click += CheckServerButton_Click;
            HomeTabContent.CheckUpdatesButtonRef.Click += CheckUpdates_Click;
            HomeTabContent.LaunchOrUpdateButtonRef.Click += LaunchOrUpdate_Click;
            HomeTabContent.KillHeadlessButtonRef.Click += KillServer_Click;
            HomeTabContent.NewProfileButtonRef.Click += NewProfileButton_Click;
            HomeTabContent.ManageProfileButtonRef.Click += ManageProfileButton_Click;
            HomeTabContent.ModProfileComboBoxRef.SelectionChanged += ModProfileComboBox_SelectionChanged;
        }
        if (ModsTabContent != null)
        {
            ModsTabContent.RefreshModsButtonRef.Click += CheckServerButton_Click;
            ModsTabContent.CheckForModsButtonRef.Click += CheckServerButton_Click;
        }
        // Settings tab no longer exposes a Configure button; logic moved into the tab

        // Load saved server config if present
        LoadConfig();
        InitializeModProfiles();

        Loaded += async (_, _) =>
        {
            if (!File.Exists(_clientPath))
            {
                if (!ConfirmContinueWithoutSptRoot())
                {
                    Close();
                    return;
                }
            }

            // Kick off SPT version check immediately so local errors/version show without waiting on server calls.
            _ = CheckSptVersion();

            await RefreshPluginConfigs();
            await RefreshMods();

            // if secret is set, validate it on server and update admin status
            if (!string.IsNullOrWhiteSpace(_secret))
            {
                CheckIfSecretIsValid(_secret);
            }

            // Fetch launcher settings
            await FetchLauncherSettings();
            _modProfileFileService.UpdateExclusions(_excludedMods, _excludedModFolders, _excludedConfigs);
            RefreshProfileModCounts();

            // Smart profile initialization: check for mismatched mods on first run
            await HandleFirstRunProfileInitialization();

            UpdateHomeProfileSelector();

            // Initialize server status check timer
            InitializeServerCheckTimer();

            // Check server status on load
            await CheckServerStatus();

            // Start server notifier (may set status to Offline if unreachable)
            await InitializeSignalR();
        };
    }

    private void InitializeModProfiles()
    {
        _modProfileStore = _modProfileRepository.EnsureInitialized();
        RefreshProfileModCounts();
        UpdateHomeProfileSelector();
    }

    private async Task HandleFirstRunProfileInitialization()
    {
        if (_modProfileStore.Profiles.Count > 1 || !string.Equals(_modProfileStore.Profiles[0].Name, "Default", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var serverMods = await GetServerModsAsync();
        var localMods = GetLocalMods();

        if (localMods.Count == 0)
        {
            return;
        }

        var serverModNames = new HashSet<string>(serverMods.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        var mismatchedMods = localMods.Where(m => !serverModNames.Contains(m.Name)).ToList();

        if (mismatchedMods.Count >= 2)
        {
            var result = MessageBox.Show(
                $"No profiles found but mods don't match server. Do you want to make a profile from these mods?\n\n" +
                $"Local mods not on server: {mismatchedMods.Count}\n\n" +
                $"Press yes if you currently have mods that you play locally or on another server.",
                "Create Custom Profile?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (result == MessageBoxResult.Yes)
            {
                var customProfile = new ModProfile
                {
                    Name = "Local",
                    IsServerProfile = false,
                    IsActive = false,
                    IsProtected = false
                };

                var storeForSwitch = _modProfileRepository.LoadStore();
                storeForSwitch.Profiles.Add(customProfile);
                _modProfileRepository.SaveStore(storeForSwitch);

                try
                {
                    _modProfileFileService.ActivateProfile(storeForSwitch, customProfile.Name);
                    _modProfileRepository.SaveStore(storeForSwitch);
                    _modProfileStore = storeForSwitch;
                    RefreshProfileModCounts();
                    UpdateHomeProfileSelector();

                    MessageBox.Show(
                        "Custom 'Local' profile created and activated.\n\nYour current mods are now stored in the 'Local' profile.\n\n" +
                        "You can use the 'Default' profile for server-matched mods.",
                        "Profile Created",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to create custom profile: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }

    private void RefreshProfileModCounts()
    {
        foreach (var profile in _modProfileStore.Profiles)
        {
            profile.ModCount = _modProfileFileService.CountMods(profile);
        }
    }

    private void UpdateHomeProfileSelector()
    {
        if (HomeTabContent == null)
        {
            return;
        }

        _isUpdatingProfileSelection = true;
        HomeTabContent.SetProfiles(_modProfileStore.Profiles, _modProfileStore.ActiveProfileName);
        _isUpdatingProfileSelection = false;
    }

    private void NewProfileButton_Click(object sender, RoutedEventArgs e)
    {
        OpenProfileManager(createNewOnOpen: true);
    }

    private void ManageProfileButton_Click(object sender, RoutedEventArgs e)
    {
        OpenProfileManager(createNewOnOpen: false);
    }

    private void OpenProfileManager(bool createNewOnOpen)
    {
        var profileWindow = new ModProfileManagerWindow(_modProfileRepository, _modProfileFileService, createNewOnOpen)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        profileWindow.ShowDialog();

        _modProfileStore = _modProfileRepository.EnsureInitialized();
        RefreshProfileModCounts();
        UpdateHomeProfileSelector();
    }

    private async void ModProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingProfileSelection)
        {
            return;
        }

        var selectedProfileName = HomeTabContent.GetSelectedProfileName();
        if (string.IsNullOrWhiteSpace(selectedProfileName) ||
            string.Equals(selectedProfileName, _modProfileStore.ActiveProfileName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"Switch active profile to '{selectedProfileName}'?\n\nThis will move active mods and configs into profile storage.",
            "Switch Profile",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            UpdateHomeProfileSelector();
            return;
        }

        HomeTabContent.ModProfileComboBoxRef.IsEnabled = false;
        HomeTabContent.SetStatusMessage("Switching profile...");

        try
        {
            await Task.Run(() => _modProfileFileService.ActivateProfile(_modProfileStore, selectedProfileName));
            _modProfileRepository.SaveStore(_modProfileStore);
            RefreshProfileModCounts();
            UpdateHomeProfileSelector();
            await RefreshPluginConfigs();
            await RefreshMods();
            HomeTabContent.SetStatusMessage($"Active profile: {selectedProfileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to switch profile: {ex.Message}", "Profile Error", MessageBoxButton.OK, MessageBoxImage.Error);
            _modProfileStore = _modProfileRepository.EnsureInitialized();
            RefreshProfileModCounts();
            UpdateHomeProfileSelector();
        }
        finally
        {
            HomeTabContent.ModProfileComboBoxRef.IsEnabled = true;
        }
    }

    private static bool ConfirmContinueWithoutSptRoot()
    {
        var result = MessageBox.Show(
            "SPT not found. Make sure you run this inside the SPT root folder.\n\n" +
            "You can continue if you know what you are doing. Are you sure you want to continue?",
            "SPT Not Found",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
    }

    private async Task InitializeSignalR()
    {
        // Subscribe to the shared SignalR service events and start the connection.
        var svc = Services.SignalRService.Instance;

        // Unsubscribe first to avoid duplicate handlers if called multiple times
        svc.ServerRestarting -= OnServerRestarting;
        svc.SptServerOffline -= OnSptServerOffline;
        svc.SptServerOnline -= OnSptServerOnline;
        svc.SptServerRestarting -= OnSptServerRestarting;
        svc.SptServerUpdating -= OnSptServerUpdating;
        svc.HeadlessOffline -= OnHeadlessOffline;
        svc.HeadlessOnline -= OnHeadlessOnline;
        svc.HeadlessRestarted -= OnHeadlessRestarted;
        svc.Connected -= OnConnected;
        svc.ConnectionFailed -= OnConnectionFailed;

        svc.ServerRestarting += OnServerRestarting;
        svc.SptServerOffline += OnSptServerOffline;
        svc.SptServerOnline += OnSptServerOnline;
        svc.SptServerRestarting += OnSptServerRestarting;
        svc.SptServerUpdating += OnSptServerUpdating;
        svc.HeadlessOffline += OnHeadlessOffline;
        svc.HeadlessOnline += OnHeadlessOnline;
        svc.HeadlessRestarted += OnHeadlessRestarted;
        svc.Connected += OnConnected;
        svc.ConnectionFailed += OnConnectionFailed;

        // Start the shared connection
        await svc.StartAsync(BaseUrl);
    }

    // Event bridges that marshal into the UI thread
    private void OnServerRestarting(string message) => Dispatcher.Invoke(() => { Console.WriteLine("[SERVER NOTICE] " + message); HomeTabContent.SetServerStatus("Restarting...", System.Windows.Media.Brushes.Orange); });
    private void OnSptServerOffline(string message) => Dispatcher.Invoke(() => { Console.WriteLine("[SPT NOTICE] " + message); HomeTabContent.SetSptServerStatus("Offline", System.Windows.Media.Brushes.Red); });
    private void OnSptServerOnline(string message) => Dispatcher.Invoke(() => { Console.WriteLine("[SPT NOTICE] " + message); HomeTabContent.SetSptServerStatus("Online", System.Windows.Media.Brushes.LightGreen); });
    private void OnSptServerRestarting(string message) => Dispatcher.Invoke(() => { Console.WriteLine("[SPT NOTICE] " + message); HomeTabContent.SetSptServerStatus("Restarting...", System.Windows.Media.Brushes.Orange); });
    private void OnSptServerUpdating(string message) => Dispatcher.Invoke(() => { Console.WriteLine("[SPT NOTICE] " + message); HomeTabContent.SetSptServerStatus("Updating...", System.Windows.Media.Brushes.Aqua); });
    private void OnHeadlessOffline(string message) => Dispatcher.Invoke(() => { Console.WriteLine("[HEADLESS NOTICE] " + message); HomeTabContent.SetHeadlessStatus("Offline", System.Windows.Media.Brushes.Red); });
    private void OnHeadlessOnline(string message) => Dispatcher.Invoke(() => { Console.WriteLine("[HEADLESS NOTICE] " + message); HomeTabContent.SetHeadlessStatus("Online", System.Windows.Media.Brushes.LightGreen); });
    private void OnHeadlessRestarted(string message) => Dispatcher.Invoke(() => { Console.WriteLine("[SERVER NOTICE] " + message); HomeTabContent.SetHeadlessStatus("Restarting...", System.Windows.Media.Brushes.Orange); });
    private void OnConnected(string msg) => Dispatcher.Invoke(() => Console.WriteLine(msg));
    private void OnConnectionFailed(Exception ex) => Dispatcher.Invoke(() =>
    {
        Debug.WriteLine($"SignalR connection failed: {ex.Message}");
        HomeTabContent.SetServerStatus("Offline", System.Windows.Media.Brushes.Red);
        HomeTabContent.SetSptServerStatus("Offline", System.Windows.Media.Brushes.Red);
        HomeTabContent.SetHeadlessStatus("Offline", System.Windows.Media.Brushes.Red);
    });

    private string BaseUrl => $"http://{_serverIp}:{_serverPort}";

    private async Task<List<ModInfo>> GetServerModsAsync()
    {
        try
        {
            var response = await SharedHttpClient.GetAsync($"{BaseUrl}/api/plugins");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var dbMods = JsonSerializer.Deserialize<List<ModInfo>>(json);
                return dbMods ?? new List<ModInfo>();
            }

            return new List<ModInfo>();
        }
        catch
        {
            return new List<ModInfo>();
        }
    }

    private List<ModInfo> GetLocalMods()
    {
        var mods = new List<ModInfo>();

        if (!Directory.Exists(_modsFolder))
            return mods;

        // Excluded mod names and folders (same as server)
        var excludedMods = new HashSet<string>(_excludedMods, StringComparer.OrdinalIgnoreCase);
        var excludedModFolders = new HashSet<string>(_excludedModFolders, StringComparer.OrdinalIgnoreCase);

        var processedMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Folder-based mods
        foreach (var modFolder in Directory.GetDirectories(_modsFolder))
        {
            var folderName = Path.GetFileName(modFolder);
            if (excludedModFolders.Contains(folderName))
                continue;

            var dllFiles = Directory.GetFiles(modFolder, "*.dll", SearchOption.AllDirectories);
            if (dllFiles.Length == 0)
                continue;

            // Don't process excluded or already processed mods (Some mods may contain e.g. Fika combability DLLs)
            //if (excludedMods.Contains(folderName) || processedMods.Contains(folderName))
                //continue;

            var version = FileVersionInfo.GetVersionInfo(dllFiles[0]).FileVersion ?? "0";

            mods.Add(new ModInfo
            {
                Name = folderName,
                Version = version,
                FileName = folderName + ".zip",
                IsFolderMod = true
            });

            processedMods.Add(folderName);
        }

        // Single DLL mods
        foreach (var dll in Directory.GetFiles(_modsFolder, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var modName = Path.GetFileNameWithoutExtension(dll);

            if (excludedMods.Contains(modName) || processedMods.Contains(modName))
                continue;

            var version = FileVersionInfo.GetVersionInfo(dll).FileVersion ?? "0";

            mods.Add(new ModInfo
            {
                Name = modName,
                Version = version,
                FileName = modName + ".zip",
                IsFolderMod = false
            });

            processedMods.Add(modName);
        }

        return mods;
    }

    private async Task CheckSptVersion()
    {
        try
        {
            if (_modsFolder == null)
            {
                HomeTabContent.SetCurrentSptVersion("SPT folder not found", System.Windows.Media.Brushes.Red);
                return;
            }

            // --- Local check first (fast, no network needed) ---
            var sptFolder = Path.Combine(_modsFolder, "spt");
            if (!Directory.Exists(sptFolder))
            {
                HomeTabContent.SetCurrentSptVersion("SPT folder missing", System.Windows.Media.Brushes.Red);
                Debug.WriteLine("SPT folder not found at: " + sptFolder);
                return;
            }

            var sptCoreDll = Path.Combine(sptFolder, "spt-core.dll");
            if (!File.Exists(sptCoreDll))
            {
                HomeTabContent.SetCurrentSptVersion("spt-core.dll missing", System.Windows.Media.Brushes.Red);
                Debug.WriteLine("spt-core.dll not found at: " + sptCoreDll);
                return;
            }

            FileVersionInfo versionInfo;
            try
            {
                versionInfo = FileVersionInfo.GetVersionInfo(sptCoreDll);
            }
            catch (Exception ex)
            {
                HomeTabContent.SetCurrentSptVersion("Could not read version", System.Windows.Media.Brushes.Red);
                Debug.WriteLine($"Failed to read version info from {sptCoreDll}: {ex.Message}");
                return;
            }

            var localVersionText = versionInfo.FileVersion ?? "0.0.0.0";
            if (string.IsNullOrEmpty(localVersionText) || localVersionText == "0.0.0.0")
            {
                HomeTabContent.SetCurrentSptVersion("Version info empty", System.Windows.Media.Brushes.Red);
                Debug.WriteLine("Version info is empty for: " + sptCoreDll);
                return;
            }

            // Local version found — show local version immediately in gray until server confirms state
            HomeTabContent.SetCurrentSptVersion(localVersionText, System.Windows.Media.Brushes.Gray);

            // --- Server comparison (only attempted if server is already reachable) ---
            try
            {
                var response = await SharedHttpClient.GetAsync($"{BaseUrl}/api/spt/version");
                if (!response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"Server version check returned non-success status: {response.StatusCode}");
                    // Server failed, show local version in gray
                    HomeTabContent.SetCurrentSptVersion(localVersionText, System.Windows.Media.Brushes.Gray);
                    return;
                }

                string verText;
                try
                {
                    var responseText = await response.Content.ReadAsStringAsync();
                    verText = JsonSerializer.Deserialize<string>(responseText) ?? responseText.Trim().Trim('"');
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to parse server version response: {ex.Message}");
                    // Server response failed, show local version in gray
                    HomeTabContent.SetCurrentSptVersion(localVersionText, System.Windows.Media.Brushes.Gray);
                    return;
                }

                if (!Version.TryParse(verText, out var serverVersion) ||
                    !Version.TryParse(localVersionText, out var localVersion))
                {
                    Debug.WriteLine($"Failed to parse versions: server='{verText}', local='{localVersionText}'");
                    // Version parse failed, show local version in gray
                    HomeTabContent.SetCurrentSptVersion(localVersionText, System.Windows.Media.Brushes.Gray);
                    return;
                }

                if (serverVersion > localVersion)
                {
                    HomeTabContent.SetCurrentSptVersion($"{localVersionText} (Outdated)", System.Windows.Media.Brushes.Red);
                    HomeTabContent.SetLaunchOrUpdateButtonContent("Update SPT");

                    var updateSpt = MessageBox.Show("A new SPT version is required. Would you like to update?",
                        "Update Required", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (updateSpt == MessageBoxResult.Yes)
                        SptNeedsUpdate();
                }
                else
                {
                    HomeTabContent.SetCurrentSptVersion(localVersionText, System.Windows.Media.Brushes.LightGreen);
                }
            }
            catch (Exception ex)
            {
                // Server unreachable — show local version in gray
                Debug.WriteLine($"Could not reach server for SPT version check: {ex.Message}");
                HomeTabContent.SetCurrentSptVersion(localVersionText, System.Windows.Media.Brushes.Gray);
            }
        }
        catch (Exception e)
        {
            HomeTabContent.SetCurrentSptVersion("Check failed", System.Windows.Media.Brushes.Red);
            Debug.WriteLine($"Unexpected error in CheckSptVersion: {e.Message}");
        }
    }

    private void SptNeedsUpdate()
    {
        // Set play button to update
        HomeTabContent.SetLaunchOrUpdateButtonContent("Update SPT");

        // Don't open multiple updater windows
        foreach (Window w in Application.Current.Windows)
        {
            if (w is SptUpdater)
                return;
        }

        var updateWindow = new SptUpdater(BaseUrl, _basePath)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        // Hide the main window while updater is visible and show it again when updater closes
        this.Hide();
        updateWindow.Closed += async (_, _) =>
        {
            this.Show();
            // Recheck SPT version after update
            await CheckSptVersion();
            // Check mods again in case SPT update included mod updates and it will update the button state
            await RefreshMods();
        };

        updateWindow.Show();
    }

    private async Task CheckServerStatus()
    {
        try
        {
            var response = await SharedHttpClient.GetAsync($"{BaseUrl}/api/status/spt-server");
            // If response is successful, server is online
            if (response.IsSuccessStatusCode)
            {
                HomeTabContent.SetServerStatus("Online", System.Windows.Media.Brushes.LightGreen);

                // Response returns true and false based on SPT server status so we can use it to update that
                var content = await response.Content.ReadAsStringAsync();
                if (bool.TryParse(content.Trim(), out var isServerRunning))
                {
                    HomeTabContent.SetSptServerStatus(isServerRunning ? "Online" : "Offline", isServerRunning ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.Red);
                }
            }
            else
            {
                HomeTabContent.SetServerStatus("Offline", System.Windows.Media.Brushes.Red);
                HomeTabContent.SetSptServerStatus("Offline", System.Windows.Media.Brushes.Red);
            }

            // Headless server status
            await CheckHeadlessServerStatus();
        }
        catch
        {
            HomeTabContent.SetServerStatus("Offline", System.Windows.Media.Brushes.Red);
            HomeTabContent.SetSptServerStatus("Offline", System.Windows.Media.Brushes.Red);
        }
    }

    private async Task CheckHeadlessServerStatus()
    {
        try
        {
            var responseHeadless = await SharedHttpClient.GetAsync($"{BaseUrl}/api/status/headless");
            if (responseHeadless.IsSuccessStatusCode)
            {
                var content = await responseHeadless.Content.ReadAsStringAsync();
                if (bool.TryParse(content.Trim(), out var isHeadlessRunning))
                {
                    HomeTabContent.SetHeadlessStatus(isHeadlessRunning ? "Online" : "Offline", isHeadlessRunning ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.Red);
                }
            }
            else
            {
                HomeTabContent.SetHeadlessStatus("Offline", System.Windows.Media.Brushes.Red);
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"Error checking headless server status: {e.Message}");
            HomeTabContent.SetHeadlessStatus("Offline", System.Windows.Media.Brushes.Red);
        }
    }

    // Public wrappers so SettingsTab can trigger these actions
    public Task RefreshModsPublic() => RefreshMods();
    public Task CheckServerStatusPublic() => CheckServerStatus();

    private async Task RefreshMods()
    {
        HomeTabContent.SetLaunchOrUpdateButtonEnabled(false);

        var serverMods = await GetServerModsAsync();
        var localMods = GetLocalMods();

        if (serverMods.Count == 0)
        {
            return;
        }

        var statusList = CompareMods(serverMods, localMods);
        HomeTabContent.SetModListItemsSource(statusList);

        var upToDate = ModsMatch(serverMods, localMods);
        HomeTabContent.SetLaunchOrUpdateButtonContent(upToDate ? "Launch" : "Update");
        HomeTabContent.SetLaunchOrUpdateButtonEnabled(true);
    }

    private List<ModStatusEntry> CompareMods(List<ModInfo> serverMods, List<ModInfo> localMods)
    {
        var statusList = new List<ModStatusEntry>();

        // Create dictionaries for quick lookup
        var localDict = localMods.ToDictionary(m => m.Name, m => m, StringComparer.OrdinalIgnoreCase);
        var serverDict = serverMods.ToDictionary(m => m.Name, m => m, StringComparer.OrdinalIgnoreCase);

        // Check server mods against local mods
        foreach (var serverMod in serverMods)
        {
            localDict.TryGetValue(serverMod.Name, out var localMod);

            string status;
            if (localMod == null)
                status = "Not installed";
            else
                status = localMod.Version == serverMod.Version ? "Up to date" : "Update";

            statusList.Add(new ModStatusEntry
            {
                Name = serverMod.Name,
                LocalVersion = localMod?.Version ?? "-",
                ServerVersion = serverMod.Version,
                Status = status,
                IsFolderMod = serverMod.IsFolderMod
            });
        }

        // Check for local mods not on server
        foreach (var localMod in localMods)
        {
            if (!serverDict.ContainsKey(localMod.Name))
            {
                statusList.Add(new ModStatusEntry
                {
                    Name = localMod.Name,
                    LocalVersion = localMod.Version,
                    ServerVersion = "-",
                    Status = "Removed",
                    IsFolderMod = localMod.IsFolderMod
                });
            }
        }

        return statusList;
    }

    private bool ModsMatch(List<ModInfo> serverMods, List<ModInfo> localMods)
    {
        // Check if local and server mod counts match
        if (localMods.Count != serverMods.Count)
            return false;

        // Check if all server mods are present locally with matching versions
        foreach (var serverMod in serverMods)
        {
            var localMod = localMods.FirstOrDefault(m => m.Name == serverMod.Name);
            if (localMod == null || localMod.Version != serverMod.Version)
                return false;
        }
        return true;
    }

    private async Task<List<ConfigInfo>> GetServerConfigsAsync()
    {
        try
        {
            var response = await SharedHttpClient.GetAsync($"{BaseUrl}/api/config-files");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var dbConfigs = JsonSerializer.Deserialize<List<ConfigInfo>>(json);
                return dbConfigs ?? new List<ConfigInfo>();
            }

            return new List<ConfigInfo>();
        }
        catch
        {
            return new List<ConfigInfo>();
        }
    }

    private List<ConfigInfo> GetLocalConfigs()
    {
        var configs = new List<ConfigInfo>();

        if (!Directory.Exists(_pluginsConfigFolder))
            return configs;

        var excludedConfigs = new HashSet<string>(_excludedConfigs, StringComparer.OrdinalIgnoreCase);

        var configFiles = Directory.GetFiles(_pluginsConfigFolder, "*.cfg", SearchOption.TopDirectoryOnly);
        foreach (var configFile in configFiles)
        {
            var fileName = Path.GetFileName(configFile);
            if (excludedConfigs.Contains(fileName))
                continue;

            var lastModified = File.GetLastWriteTimeUtc(configFile);

            configs.Add(new ConfigInfo
            {
                FileName = fileName,
                LastModified = lastModified
            });
        }

        return configs;
    }

    private async Task RefreshPluginConfigs()
    {
        HomeTabContent.SetSyncStatus("Checking...", System.Windows.Media.Brushes.Gray);

        var serverConfigs = await GetServerConfigsAsync();
        var localConfigs = GetLocalConfigs();

        if(serverConfigs.Count == 0)
        {
            HomeTabContent.SetSyncStatus("Failed to get server configs", System.Windows.Media.Brushes.DarkOrange);
            return;
        }
        if(localConfigs.Count == 0)
        {
            HomeTabContent.SetSyncStatus("No local configs", System.Windows.Media.Brushes.MediumSlateBlue);
            return;
        }

        if(serverConfigs.Count != localConfigs.Count)
        {
            HomeTabContent.SetSyncStatus("Configs out of sync", System.Windows.Media.Brushes.CadetBlue);
            return;
        }

        // TODO: Check enforced configs last modified dates

        HomeTabContent.SetSyncStatus("Configs synced", System.Windows.Media.Brushes.LightGreen);
    }

    private async void LaunchOrUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // If SPT needs update, open updater window
            if (HomeTabContent.GetLaunchOrUpdateButtonContent() == "Update SPT")
            {
                SptNeedsUpdate();
                return;
            }

            // Sync configs
            HomeTabContent.SetStatusMessage("Syncing config files...");
             var serverConfigs = await GetServerConfigsAsync();
             var localConfigs = GetLocalConfigs();
            HomeTabContent.SetSyncStatus("Syncing configs...", System.Windows.Media.Brushes.DodgerBlue);

            // Check if configs length differ (some local configs removed or this is first launch) sync all missing configs
            if (localConfigs.Count != serverConfigs.Count)
            {
                foreach (var serverConfig in serverConfigs)
                {
                    var localConfig = localConfigs.FirstOrDefault(c =>
                        string.Equals(c.FileName, serverConfig.FileName, StringComparison.OrdinalIgnoreCase));

                    if (localConfig == null)
                    {
                        try
                        {
                            var url = $"{BaseUrl}/api/config-files/{Path.GetFileNameWithoutExtension(serverConfig.FileName)}";
                            var data = await SharedHttpClient.GetByteArrayAsync(url);

                            var destPath = Path.Combine(_pluginsConfigFolder!, serverConfig.FileName);
                            await File.WriteAllBytesAsync(destPath, data);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Failed to download config {serverConfig.FileName}: {ex.Message}");
                            MessageBox.Show($"Failed to download config {serverConfig.FileName}: {ex.Message}",
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                    }
                    // Remember to check if enforced and found
                    else if (serverConfig.IsEnforced && localConfig.LastModified != serverConfig.LastModified)
                    {
                        try
                        {
                            var url = $"{BaseUrl}/api/config-files/{Path.GetFileNameWithoutExtension(serverConfig.FileName)}";
                            var data = await SharedHttpClient.GetByteArrayAsync(url);

                            var destPath = Path.Combine(_pluginsConfigFolder!, serverConfig.FileName);
                            await File.WriteAllBytesAsync(destPath, data);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Failed to download config {serverConfig.FileName}: {ex.Message}");
                            MessageBox.Show($"Failed to download config {serverConfig.FileName}: {ex.Message}",
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                    }
                }
            }
            // Download enforced configs if missing or outdated
            else
            {
                foreach (var serverConfig in serverConfigs.Where(c => c.IsEnforced))
                {
                    var localConfig = localConfigs.FirstOrDefault(c =>
                        string.Equals(c.FileName, serverConfig.FileName, StringComparison.OrdinalIgnoreCase));

                    if (localConfig == null || localConfig.LastModified != serverConfig.LastModified)
                    {
                        try
                        {
                            var url = $"{BaseUrl}/api/config-files/{Path.GetFileNameWithoutExtension(serverConfig.FileName)}";
                            var data = await SharedHttpClient.GetByteArrayAsync(url);

                            var destPath = Path.Combine(_pluginsConfigFolder!, serverConfig.FileName);
                            await File.WriteAllBytesAsync(destPath, data);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Failed to download config {serverConfig.FileName}: {ex.Message}");
                            MessageBox.Show($"Failed to download config {serverConfig.FileName}: {ex.Message}",
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                    }
                }
            }
            HomeTabContent.SetSyncStatus("Configs synced", System.Windows.Media.Brushes.LightGreen);

            if (HomeTabContent.GetLaunchOrUpdateButtonContent() == "Update")
            {
                HomeTabContent.SetLaunchOrUpdateButtonEnabled(false);
                HomeTabContent.SetStatusMessage("Updating mods...");
                var modsToUpdate = ((List<ModStatusEntry>)HomeTabContent.GetModListItemsSource()!)
                    .Where(m => m.Status == "Update" || m.Status == "Not installed")
                    .ToList();

                // Download and update mods
                var allUpdated = await DownloadAndUpdateMods(modsToUpdate);
                HomeTabContent.SetStatusMessage(allUpdated ? "All mods updated" : "Some mods failed");

                // Remove mods marked as "Removed"
                var modsToRemove = ((List<ModStatusEntry>)HomeTabContent.GetModListItemsSource()!)
                     .Where(m => m.Status == "Removed")
                     .ToList();
                 await RemoveMods(modsToRemove);

                // Small delay before finishing to let user see status and windows catch up
                await Task.Delay(100);
                HomeTabContent.SetStatusMessage("Update complete.");
                await Task.Delay(200);

                HomeTabContent.SetLaunchOrUpdateButtonContent("Launch");
                HomeTabContent.SetLaunchOrUpdateButtonEnabled(true);

                await RefreshMods();
            }
            else
            {
                HomeTabContent.SetStatusMessage("Launching the game...");
                LaunchTheGame();
            }

            await Task.Delay(2500);
            HomeTabContent.SetStatusMessage("");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error during launch/update: {ex.Message}");
            MessageBox.Show($"Error during launch/update: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RemoveMods(List<ModStatusEntry> modsToRemove)
    {
        try
        {
            // Show progress: removing mods
            HomeTabContent.SetStatusMessage("Removing old mods...");
            await Task.Run(() =>
            {
                foreach (var mod in modsToRemove)
                {
                    if (mod.IsFolderMod)
                    {
                        var modFolder = Path.Combine(_modsFolder!, mod.Name);
                        if (Directory.Exists(modFolder))
                            Directory.Delete(modFolder, true);
                    }
                    else
                    {
                        var dllFile = Path.Combine(_modsFolder!, mod.Name + ".dll");
                        if (File.Exists(dllFile))
                            File.Delete(dllFile);
                    }
                }
            });
            // Update mod list UI
            HomeTabContent.RefreshModList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to remove mods: {ex.Message}");
            MessageBox.Show($"Failed to remove some mods: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task<bool> DownloadAndUpdateMods(List<ModStatusEntry> mods)
    {
        var success = true;

        foreach (var mod in mods)
        {
            try
            {
                if (!Directory.Exists(_modsFolder))
                    return false;

                // Initial UI update
                mod.Status = "Preparing...";
                HomeTabContent.RefreshModList();
                HomeTabContent.SetStatusMessage($"Updating mod: {mod.Name}");
                await Task.Delay(50);

                // Get mod info from server
                var serverMods = await GetServerModsAsync();
                var modInfo = serverMods.FirstOrDefault(m => m.Name == mod.Name);
                if (modInfo == null)
                {
                    Debug.WriteLine($"Mod info not found on server: {mod.Name}");
                    continue;
                }

                var url = $"{BaseUrl}/api/plugins/{modInfo.Name}/zip";

                // --- Streamed download with progress ---
                var tempZip = Path.Combine(Path.GetTempPath(), modInfo.FileName);
                if (File.Exists(tempZip)) File.Delete(tempZip);

                // If download states are not supported, fallback to simple download
                mod.Status = "Downloading...";
                using (var response = await DownloadHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    var canReport = totalBytes > 0;

                    await using var stream = await response.Content.ReadAsStreamAsync();
                    await using var fileStream = File.Create(tempZip);

                    var buffer = new byte[81920];
                    long totalRead = 0;
                    int read;
                    double lastPercent = 0;

                    while ((read = await stream.ReadAsync(buffer)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read));
                        totalRead += read;

                        if (canReport)
                        {
                            var percent = (double)totalRead / totalBytes * 100;
                            if (percent - lastPercent >= 1) // only update every 1%
                            {
                                mod.Status = $"Downloading... {percent:F0}%";
                                HomeTabContent.SetStatusMessage($"Updating mod: {mod.Name} - {percent:F0}%");
                                HomeTabContent.RefreshModList();
                                lastPercent = percent;
                            }
                        }
                    }
                }

                // --- Extracting ---
                mod.Status = "Extracting...";
                HomeTabContent.RefreshModList();
                HomeTabContent.SetStatusMessage($"Updating mod: {mod.Name}");
                await Task.Delay(50);

                // Extract to temp folder
                var extractPath = Path.Combine(Path.GetTempPath(), $"{mod.Name}_extract_{Guid.NewGuid():N}");
                if (Directory.Exists(extractPath))
                    Directory.Delete(extractPath, true);

                ZipFile.ExtractToDirectory(tempZip, extractPath);
                File.Delete(tempZip);

                // Ensure Plugins folder exists
                Directory.CreateDirectory(_modsFolder);

                if (modInfo.IsFolderMod)
                {
                    // Handle nested mod folders correctly
                    var srcFolder = extractPath;
                    var subDirs = Directory.GetDirectories(extractPath);

                    if (subDirs.Length == 1 &&
                        File.Exists(Path.Combine(subDirs[0], $"{mod.Name}.dll")))
                    {
                        srcFolder = subDirs[0];
                    }

                    var destFolder = Path.Combine(_modsFolder, mod.Name);
                    if (Directory.Exists(destFolder))
                        Directory.Delete(destFolder, true);

                    mod.Status = "Installing...";
                    HomeTabContent.RefreshModList();
                    await Task.Delay(50);

                    CopyDirectory(srcFolder, destFolder);
                }
                else
                {
                    var dllFile = Directory.GetFiles(extractPath, "*.dll", SearchOption.AllDirectories).FirstOrDefault();
                    if (dllFile != null)
                    {
                        mod.Status = "Installing...";
                        HomeTabContent.RefreshModList();
                        await Task.Delay(50);

                        var destFile = Path.Combine(_modsFolder, Path.GetFileName(dllFile));
                        if (File.Exists(destFile))
                            File.Delete(destFile);
                        File.Copy(dllFile, destFile, overwrite: true);
                    }
                }

                // Cleanup
                if (Directory.Exists(extractPath))
                    Directory.Delete(extractPath, true);

                // Done
                mod.Status = "Up to date";
                HomeTabContent.RefreshModList();
                await Task.Delay(50);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to update {mod.Name}: {ex.Message}");
                success = false;

                mod.Status = "Failed";
                HomeTabContent.RefreshModList();
                HomeTabContent.SetStatusMessage($"Failed to update mod: {mod.Name}");

                MessageBox.Show($"Failed to update mod {mod.Name}: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        HomeTabContent.SetStatusMessage("Mod updates complete.");
        return success;
    }

    /// <summary>
    /// Recursively copies a directory and all contents.
    /// </summary>
    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destinationDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, destSubDir);
        }
    }

    private void LaunchTheGame()
    {
        if (!File.Exists(_clientPath))
        {
            MessageBox.Show("SPT.Launcher.exe not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = _clientPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(_clientPath)
        });
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        HomeTabContent.CheckUpdatesButtonRef.IsEnabled = false;
        HomeTabContent.CheckUpdatesButtonRef.Content = "Checking...";
        try
        {
            await RefreshMods();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error checking for updates: {ex.Message}");
        }
        await Task.Delay(1000); // 1 second cooldown
        HomeTabContent.CheckUpdatesButtonRef.Content = "Check for mod updates";
        HomeTabContent.CheckUpdatesButtonRef.IsEnabled = true;
    }

    private void CheckServerButton_Click(object sender, RoutedEventArgs e)
    {
         // Set status to checking
        HomeTabContent.SetServerStatus("Checking...", System.Windows.Media.Brushes.Gray);
         // Check server status
         _ = CheckServerStatus();
    }

    // new: load config from file
    private void LoadConfig()
    {
        try
        {
            if (string.IsNullOrEmpty(_configPath) || !File.Exists(_configPath))
                return;

            var json = File.ReadAllText(_configPath);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json);
            if (cfg != null)
            {
                if (!string.IsNullOrWhiteSpace(cfg.ServerIp))
                    _serverIp = cfg.ServerIp;
                if (cfg.ServerPort > 0)
                    _serverPort = cfg.ServerPort;
                if (!string.IsNullOrWhiteSpace(cfg.SptServerAddress))
                    _sptServerAddress = cfg.SptServerAddress;
                if (!string.IsNullOrWhiteSpace(cfg.Secret))
                    _secret = cfg.Secret;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load config: {ex.Message}");
        }
    }

    // new: save config to file
    private void SaveConfig()
    {
        try
        {
            if (string.IsNullOrEmpty(_configPath))
                return;

            var cfg = new AppConfig { ServerIp = _serverIp, ServerPort = _serverPort, SptServerAddress = _sptServerAddress, Secret = _secret};
            var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save config: {ex.Message}");
        }
    }

    private string? CheckIfSecretIsValid(string? secretKey)
    {
        if (!string.IsNullOrWhiteSpace(secretKey))
        {
            try
            {
                var url = $"{BaseUrl}/api/admin/validate?secret={secretKey}";
                try
                {
                    var response = SharedHttpClient.GetStringAsync(url).Result.Trim();
                    if (!string.IsNullOrEmpty(response))
                    {
                        try
                        {
                            // Expecting server to return an AdminConfig JSON; fall back to plain true/false
                            var admin = JsonSerializer.Deserialize<AdminConfig>(response);
                            if (admin != null && admin.IsEnabled)
                            {
                                Debug.WriteLine("Admin secret validated successfully.");
                                if (string.IsNullOrWhiteSpace(admin.Secret))
                                    admin.Secret = secretKey;
                                UpdateAdminStatus(admin);
                            }
                            else
                            {
                                MessageBox.Show("Admin secret is invalid on the server.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                                secretKey = ""; // clear invalid secret
                            }
                        }
                        catch
                        {
                            // Fallback: server returned "true" or "false"
                            if (string.Equals(response, "true", StringComparison.OrdinalIgnoreCase))
                            {
                                Debug.WriteLine("Admin secret validated successfully.");
                                UpdateAdminStatus(new AdminConfig { Secret = secretKey, IsEnabled = true });
                            }
                            else
                            {
                                MessageBox.Show("Admin secret is invalid on the server.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                                secretKey = ""; // clear invalid secret
                            }
                        }
                    }
                    else
                    {
                        MessageBox.Show("Admin secret is invalid on the server.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        secretKey = ""; // clear invalid secret
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to validate admin secret: {ex.Message}");
                    MessageBox.Show($"Failed to validate admin secret: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to validate admin secret: {ex.Message}");
                MessageBox.Show($"Failed to validate admin secret: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        return secretKey;
    }

    private void UpdateAdminStatus(AdminConfig config)
    {
        // Show the Admin panel if the admin entry is enabled (user validated as admin)
        try
        {
            // Show admin panel and text if enabled, hide if not
            HomeTabContent.UpdateAdminStatus(config);
        }
        catch
        {
            // Fallback: if referencing the panel fails for any reason, set the button only
        }
    }

    private void KillServer_Click(object sender, RoutedEventArgs e)
    {
        // Check if secret is set
        if (string.IsNullOrWhiteSpace(_secret))
        {
            MessageBox.Show("Admin secret is not set. Please configure the server settings first.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        // Check if the server is online
        if (!HomeTabContent.IsServerOnline())
        {
            MessageBox.Show("Server is not online. Cannot send shutdown command.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Prompt for confirmation
        var result = MessageBox.Show("Are you sure you want to send shutdown command to the headless server?", "Confirm Shutdown", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
            return;

        // Check if headless server is running /admin/headless/running with secret
        try
        {
            var urlCheck = $"{BaseUrl}/api/admin/headless/running?secret={_secret}";
            var responseCheck = SharedHttpClient.GetStringAsync(urlCheck).Result.Trim();
            if (!string.Equals(responseCheck, "true", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Headless server is not running. Cannot send shutdown command.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to check headless server status: {ex.Message}");
            MessageBox.Show($"Failed to check headless server status: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Send command to server /admin/headless/close with secret
        try
        {
            var url = $"{BaseUrl}/api/admin/headless/close?secret={_secret}";
            var response = SharedHttpClient.GetStringAsync(url).Result.Trim();

            if (string.Equals(response, "true", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Server shutdown command sent.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (string.Equals(response, "false", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Server cooldown active. Please wait before trying again.", "Info", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show($"Failed to send kill command. Server response: {response}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to send kill command: {ex.Message}");
            MessageBox.Show($"Failed to send kill command: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task FetchLauncherSettings()
    {
        var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher_settings.json");
        if (File.Exists(settingsPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(settingsPath);
                var settings = JsonSerializer.Deserialize<LauncherSettings>(json);
                if (settings != null)
                {
                    // Clear existing excluded lists
                    _excludedMods.Clear();
                    _excludedModFolders.Clear();
                    _excludedConfigs.Clear();

                    // Set as excluded lists
                    _excludedMods = settings.ExcludedMods;
                    _excludedModFolders = settings.ExcludedModFolders;
                    _excludedConfigs = settings.ExcludedConfigs;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load launcher settings: {ex.Message}");
            }
        }
    }

    private DispatcherTimer? _serverCheckTimer;

    private void InitializeServerCheckTimer()
    {
        _serverCheckTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        _serverCheckTimer.Tick += async (_, _) =>
        {
            // Check server status
            await CheckServerStatus();
        };
        _serverCheckTimer.Start();
    }

    /// <summary>
    /// Switches to the Home tab and updates button styling
    /// </summary>
    private void HomeTab_Click(object sender, RoutedEventArgs e)
    {
        // Show Home tab content
        HomeTabContent.Visibility = Visibility.Visible;
        ModsTabContent.Visibility = Visibility.Collapsed;
        SettingsTabContent.Visibility = Visibility.Collapsed;

        SetActiveTabButton(HomeTabButton);
    }

    /// <summary>
    /// Switches to the Mods tab and updates button styling
    /// </summary>
    private void ModsTab_Click(object sender, RoutedEventArgs e)
    {
        // Show Mods tab content
        HomeTabContent.Visibility = Visibility.Collapsed;
        ModsTabContent.Visibility = Visibility.Visible;
        SettingsTabContent.Visibility = Visibility.Collapsed;

        SetActiveTabButton(ModsTabButton);
    }

    /// <summary>
    /// Switches to the Settings tab and updates button styling
    /// </summary>
    private void SettingsTab_Click(object sender, RoutedEventArgs e)
    {
        // Show Settings tab content
        HomeTabContent.Visibility = Visibility.Collapsed;
        ModsTabContent.Visibility = Visibility.Collapsed;
        SettingsTabContent.Visibility = Visibility.Visible;

        SetActiveTabButton(SettingsTabButton);
    }

    private void SetActiveTabButton(Button activeButton)
    {
        HomeTabButton.Tag = null;
        ModsTabButton.Tag = null;
        SettingsTabButton.Tag = null;
        activeButton.Tag = "Active";
    }

    // Expose server config getters/setters so SettingsTab can call them
    public string GetServerIp() => _serverIp;
    public void SetServerIp(string ip) => _serverIp = string.IsNullOrWhiteSpace(ip) ? "127.0.0.1" : ip;

    public int GetServerPort() => _serverPort;
    public void SetServerPort(int port) => _serverPort = port > 0 ? port : 25569;

    public string GetSptServerAddress() => _sptServerAddress;
    public void SetSptServerAddress(string addr) => _sptServerAddress = string.IsNullOrWhiteSpace(addr) ? "http://127.0.0.1:6969" : addr;

    public string? GetSecret() => _secret;
    public void SetSecret(string? secret) => _secret = string.IsNullOrWhiteSpace(secret) ? "" : secret;

    // Public wrappers for config persistence and secret validation used by SettingsTab
    public void SaveConfigPublic() => SaveConfig();
    public string? ValidateSecretPublic(string? secret) => CheckIfSecretIsValid(secret);

}

// new: simple config DTO
public class AppConfig
{
    public string? ServerIp { get; set; }
    public int ServerPort { get; set; }
    public string? SptServerAddress { get; set; }
    public string? Secret { get; set; } // Admin secret for server control
}

public class ModStatusEntry
{
    public required string Name { get; set; }
    public required string LocalVersion { get; set; }
    public required string ServerVersion { get; set; }
    public string? Status { get; set; }
    public required bool IsFolderMod { get; set; }
}

