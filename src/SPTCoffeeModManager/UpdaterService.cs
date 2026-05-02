using System.Text.Json.Serialization;
using System.Windows;

namespace SPTCoffeeModManager;

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

public class UpdaterService
{
    private const string UpdateJsonUrl = "https://raw.githubusercontent.com/veskeli/SPTCoffeeModManager/main/latest.json";

    public static async Task CheckAndUpdateAsync(Action<string> updateStatus)
    {
        using var http = new HttpClient();
        updateStatus?.Invoke("Fetching update information...");
        var json = await http.GetStringAsync(UpdateJsonUrl);
        var updateInfo = JsonSerializer.Deserialize<UpdateInfo>(json);

        if(updateInfo == null)
        {
            updateStatus?.Invoke("Failed to parse update information.");
            return;
        }

        if (!await IsUpdateAvailableAsync())
        {
            updateStatus?.Invoke("No updates available.");
            return;
        }

        // Download update zip
        updateStatus?.Invoke($"Downloading update version {updateInfo.Version}...");
        var tempZip = Path.Combine(Path.GetTempPath(), "ModManagerUpdate.zip");
        await using (var stream = await http.GetStreamAsync(updateInfo.Url))
        await using (var file = File.Create(tempZip))
        {
            await stream.CopyToAsync(file);
        }

        // Launch updater and exit
        updateStatus?.Invoke("Launching updater...");
        var processModule = Process.GetCurrentProcess().MainModule;
        if (processModule != null)
        {
            var exePath = processModule.FileName;
            var baseDir = Path.GetDirectoryName(exePath)!;
            var updaterExe = Path.Combine(baseDir, "CoffeeUpdater.exe");

            if (!File.Exists(updaterExe))
            {
                MessageBox.Show("CoffeeUpdater.exe not found. Please make sure it's in the same folder as the mod manager.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = updaterExe,
                Arguments = $"\"{baseDir}\" \"{tempZip}\" \"{Path.GetFileName(exePath)}\"",
                UseShellExecute = false,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal
            };

            Process.Start(startInfo);
        }

        Environment.Exit(0);
    }

    public static async Task<bool> IsUpdateAvailableAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        Console.WriteLine("Checking for updates...");
        try
        {
            Console.WriteLine("Requesting update JSON...");
            var json = await http.GetStringAsync(UpdateJsonUrl);
            Console.WriteLine("Received JSON: " + json);

            var updateInfo = JsonSerializer.Deserialize<UpdateInfo>(json);
            Console.WriteLine("Deserialized update info.");

            if (string.IsNullOrWhiteSpace(updateInfo?.Version))
            {
                Console.WriteLine("Update info is missing the version field.");
                return false;
            }

            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
            var latestVersion = new Version(updateInfo.Version);

            Console.WriteLine($"Current: {currentVersion}, Latest: {latestVersion}");
            Console.WriteLine($"Is update available? {latestVersion > currentVersion}");
            return latestVersion > currentVersion;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Update check failed: {ex.Message}");
            return false;
        }
    }

    private class UpdateInfo
    {
        [JsonPropertyName("version")]
        public required string Version { get; init; }
        [JsonPropertyName("url")]
        public required string Url { get; init; }
    }
}