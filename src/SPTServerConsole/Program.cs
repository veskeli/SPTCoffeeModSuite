using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using SQLitePCL;
using SPTCoffee.Contracts.Models;

var configPath = "config.json";

Batteries_V2.Init();

// Add global handlers so unhandled exceptions don't close the console immediately
AppDomain.CurrentDomain.UnhandledException += (s, e) =>
{
    Console.WriteLine("Unhandled exception: " + ((e.ExceptionObject as Exception)?.ToString() ?? e.ExceptionObject?.ToString()));
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey();
};
TaskScheduler.UnobservedTaskException += (s, e) =>
{
    Console.WriteLine("Unobserved task exception: " + e.Exception);
    e.SetObserved();
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey();
};

if (!File.Exists(configPath))
{
    Console.WriteLine("config.json not found. Create it with ServerManager first.");
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey();
    return;
}

var headlessCloseLastCalled = new ConcurrentDictionary<string, DateTime>();
var headlessCloseCooldown = TimeSpan.FromSeconds(60); // adjust cooldown duration as needed

var configJson = File.ReadAllText(configPath);
var bootstrapConfig = JsonSerializer.Deserialize<BootstrapConfig>(configJson) ?? new BootstrapConfig();
var legacyConfig = JsonSerializer.Deserialize<ServerConfig>(configJson) ?? new ServerConfig();

var listenPort = bootstrapConfig.Port > 0 ? bootstrapConfig.Port : legacyConfig.Port;
var configuredDbPath = string.IsNullOrWhiteSpace(bootstrapConfig.DatabaseFileName) ? "MainDatabase\\SPTCoffee.db" : bootstrapConfig.DatabaseFileName;
var sptCoffeeDbPath = Path.IsPathRooted(configuredDbPath)
    ? configuredDbPath
    : Path.Combine(AppContext.BaseDirectory, configuredDbPath);

var rootPath = AppContext.BaseDirectory;
var pluginZipPath = Path.Combine(rootPath, "PluginZip");
var additionalModsPath = Path.Combine(rootPath, "AdditionalMods");
var mainDatabasePath = Path.Combine(rootPath, "MainDatabase");
var sptUpdatePath = Path.Combine(rootPath, "SptUpdate");
var configFilesPath = Path.Combine(rootPath, "ConfigFiles");

Directory.CreateDirectory(pluginZipPath);
Directory.CreateDirectory(additionalModsPath);
Directory.CreateDirectory(mainDatabasePath);
Directory.CreateDirectory(sptUpdatePath);
Directory.CreateDirectory(configFilesPath);

var dbDirectory = Path.GetDirectoryName(sptCoffeeDbPath);
if (!string.IsNullOrWhiteSpace(dbDirectory))
{
    Directory.CreateDirectory(dbDirectory);
}

SptCoffeeDb.EnsureSchema(sptCoffeeDbPath);
SptCoffeeDb.MigrateLegacyDataIfNeeded(sptCoffeeDbPath, legacyConfig.SptServerFolder, legacyConfig.AdditionalModsPath);

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();
var app = builder.Build();

// last spt version read time
DateTime lastSptVersionRead = DateTime.MinValue;
string cachedSptVersion = "";

// GET /PluginVersions.json -> legacy route, now served from mod database
app.MapGet("/PluginVersions.json", async context =>
{
    var mods = SptCoffeeDb.ReadPlugins(sptCoffeeDbPath);
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(JsonSerializer.Serialize(mods));
});

// GET /api/plugins -> return all plugins from database
app.MapGet("/api/plugins", async context =>
{
    var mods = SptCoffeeDb.ReadPlugins(sptCoffeeDbPath);
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(JsonSerializer.Serialize(mods));
});

// GET /api/plugins/{pluginName}/zip -> download plugin zip
app.MapGet("/api/plugins/{pluginName}/zip", async (string pluginName, HttpContext context) =>
{
    var modZipsPath = pluginZipPath;
    var mods = SptCoffeeDb.ReadPlugins(sptCoffeeDbPath);

    var mod = mods.FirstOrDefault(m =>
        string.Equals(m.Name, pluginName, StringComparison.OrdinalIgnoreCase));

    if (mod == null)
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync("Mod not found");
        return;
    }

    // Use FileName from database entry (this points to your .zip)
    var zipPath = Path.Combine(modZipsPath, mod.FileName);
    if (!File.Exists(zipPath))
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync($"Zip file not found: {mod.FileName}");
        return;
    }

    context.Response.ContentType = "application/zip";
    context.Response.Headers.ContentLength = new FileInfo(zipPath).Length;
    await context.Response.SendFileAsync(zipPath);
});

// GET /ConfigFiles.json -> legacy route, now served from database
app.MapGet("/ConfigFiles.json", async context =>
{
    var configs = SptCoffeeDb.ReadConfigs(sptCoffeeDbPath);
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(JsonSerializer.Serialize(configs));
});

// GET /api/config-files -> return all config files from database
app.MapGet("/api/config-files", async context =>
{
    var configs = SptCoffeeDb.ReadConfigs(sptCoffeeDbPath);
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(JsonSerializer.Serialize(configs));
});

// GET /api/config-files/{configName} -> download config file
app.MapGet("/api/config-files/{configName}", async (string configName, HttpContext context) =>
{
    var configsPath = configFilesPath;
    var configs = SptCoffeeDb.ReadConfigs(sptCoffeeDbPath);

    var foundConfig = configs.FirstOrDefault(c =>
        string.Equals(Path.GetFileNameWithoutExtension(c.FileName), configName, StringComparison.OrdinalIgnoreCase));

    if (foundConfig == null)
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync("Config file not found");
        return;
    }

    var fullConfigPath = Path.Combine(configsPath, foundConfig.FileName);
    if(!File.Exists(fullConfigPath))
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync("Config file not found on server");
        return;
    }

    // Return the config file
    context.Response.ContentType = "application/octet-stream";
    await context.Response.SendFileAsync(fullConfigPath);
});

app.MapGet("/api/spt/update", async context =>
{
    var sptUpdateZipPath = Path.Combine(sptUpdatePath, "spt_update.zip");

    if (!File.Exists(sptUpdateZipPath))
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync("SPT update zip not found");
        return;
    }

    var fileInfo = new FileInfo(sptUpdateZipPath);

    context.Response.ContentType = "application/zip";
    context.Response.Headers.ContentLength = fileInfo.Length;

    await context.Response.SendFileAsync(sptUpdateZipPath);
});

// GET /admin/validate?secret=... -> return boolean indicating whether secret matches any enabled admin
app.MapGet("/api/admin/validate", async (HttpContext context) =>
{
    var secret = context.Request.Query["secret"].ToString();
    context.Response.ContentType = "application/json";

    if (string.IsNullOrEmpty(secret))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("false");
        return;
    }

    var admins = SptCoffeeDb.ReadAdmins(sptCoffeeDbPath);

    var admin = admins?.FirstOrDefault(a => a.IsEnabled && a.Secret == secret);
    if (admin == null)
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsync("false");
        return;
    }

    // Return admin config without Note and Secret
    var safeAdmin = new
    {
        admin.IsEnabled,
        admin.AllowHeadlessClose
    };

    await context.Response.WriteAsync(JsonSerializer.Serialize(safeAdmin));
});

// POST /api/admin/headless/close?secret=... -> close headless client if secret matches an enabled admin with AllowHeadlessClose
app.MapMethods("/api/admin/headless/close", new[] { "GET", "POST" }, async (HttpContext context, IHubContext<ServerHub> hub) =>
{
    Console.WriteLine("On headless close command");

    var secret = context.Request.Query["secret"].ToString();
    context.Response.ContentType = "application/json";

    if (string.IsNullOrEmpty(secret))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("false");
        return;
    }

    var admins = SptCoffeeDb.ReadAdmins(sptCoffeeDbPath);

    var admin = admins?.FirstOrDefault(a => a.IsEnabled && a.AllowHeadlessClose && a.Secret == secret);
    if (admin == null)
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsync("false");
        return;
    }

    var now = DateTime.UtcNow;
    if (headlessCloseLastCalled.TryGetValue(secret, out var last) && (now - last) < headlessCloseCooldown)
    {
        context.Response.StatusCode = 429; // Too Many Requests
        await context.Response.WriteAsync("false");
        return;
    }

    headlessCloseLastCalled[secret] = now;

    Console.WriteLine($"Headless close requested by admin: {admin.Note}");

    var regex = new Regex(@"^EscapeFromTarkov$", RegexOptions.IgnoreCase);

    foreach (var p in Process.GetProcesses())
    {
        try
        {
            if (regex.IsMatch(p.ProcessName))
                p.Kill();
        }
        catch { /* ignore */ }
    }

    await hub.Clients.All.SendAsync("HeadlessRestarted", "Headless has been closed / restarting");

    await context.Response.WriteAsync("true");
});

// GET /api/status/headless -> return boolean indicating whether headless client is running
app.MapGet("/api/status/headless", async (HttpContext context) =>
{
    var regex = new Regex(@"^EscapeFromTarkov$", RegexOptions.IgnoreCase);

    var running = Process.GetProcesses().Any(p =>
    {
        try
        {
            return regex.IsMatch(p.ProcessName);
        }
        catch
        {
            return false;
        }
    });

    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(running ? "true" : "false");
});

// GET /api/admin/headless/running?secret=... -> return boolean indicating whether headless client is running
app.MapGet("/api/admin/headless/running", async (HttpContext context) =>
{
    var secret = context.Request.Query["secret"].ToString();
    context.Response.ContentType = "application/json";

    if (string.IsNullOrEmpty(secret))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("false");
        return;
    }

    var admins = SptCoffeeDb.ReadAdmins(sptCoffeeDbPath);

    var admin = admins?.FirstOrDefault(a => a.IsEnabled && a.AllowHeadlessClose && a.Secret == secret);
    if (admin == null)
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsync("false");
        return;
    }

    var regex = new Regex(@"^EscapeFromTarkov$", RegexOptions.IgnoreCase);
    var running = Process.GetProcesses().Any(p =>
    {
        try { return regex.IsMatch(p.ProcessName); }
        catch { return false; }
    });

    await context.Response.WriteAsync(running ? "true" : "false");
});

// GET /api/spt/version -> return SPT version string
app.MapGet("/api/spt/version", async context =>
{
    var now = DateTime.UtcNow;
    var cacheTimeout = TimeSpan.FromMinutes(60);
    bool isCacheValid = !string.IsNullOrEmpty(cachedSptVersion);
    var runtimeSettings = SptCoffeeDb.ReadRuntimeSettings(sptCoffeeDbPath);
    var modsFolder = Path.Combine(runtimeSettings.SptServerFolder, "BepInEx", "plugins");
    var sptCoreDll = Path.Combine(modsFolder, "spt", "spt-core.dll");

    string version;

    if (!isCacheValid || (now - lastSptVersionRead) > cacheTimeout)
    {
        try
        {
            if (File.Exists(sptCoreDll))
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(sptCoreDll);
                version = versionInfo.FileVersion ?? "unknown";

                // Cache version and timestamp
                cachedSptVersion = version;
                lastSptVersionRead = now;
            }
            else if (isCacheValid)
            {
                // Fallback to cached value if file missing
                version = cachedSptVersion;
            }
            else
            {
                // No file and no cache -> unknown
                version = "unknown";
                cachedSptVersion = version;
                lastSptVersionRead = now;
            }
        }
        catch
        {
            // On error, prefer cached value if available, otherwise return unknown
            version = isCacheValid ? cachedSptVersion : "unknown";
        }
    }
    else
    {
        version = cachedSptVersion;
    }

    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync($"\"{version}\"");
});

// Expose `LauncherConfig.json` so launchers can download launcher settings
app.MapGet("/LauncherConfig.json", async context =>
{
    var jsonPath = Path.Combine(AppContext.BaseDirectory, "LauncherConfig.json");
    if (File.Exists(jsonPath))
    {
        var json = await File.ReadAllTextAsync(jsonPath);
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(json);
    }
    else
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{}");
    }
});

app.MapGet("/api/launcher/settings", async context =>
{
    var jsonPath = Path.Combine(AppContext.BaseDirectory, "LauncherConfig.json");
    if (File.Exists(jsonPath))
    {
        var json = await File.ReadAllTextAsync(jsonPath);
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(json);
    }
    else
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{}");
    }
});

// Check if SPT Server is running on this machine
app.MapGet("/api/status/spt-server", async context =>
{
    var regex = new Regex(@"^SPT\.Server$", RegexOptions.IgnoreCase);
    var running = Process.GetProcesses().Any(p =>
    {
        try
        {
            return regex.IsMatch(p.ProcessName);
        }
        catch
        {
            return false;
        }
    });

    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(running ? "true" : "false");
});

// GET /api/spt/players -> read the current SPT.Server logs and return active players plus recent connect/disconnect events
app.MapGet("/api/spt/players", async context =>
{
    var runtimeSettings = SptCoffeeDb.ReadRuntimeSettings(sptCoffeeDbPath);
    var snapshot = SptPlayerPresenceReader.BuildSnapshot(runtimeSettings.SptServerFolder, runtimeSettings.HeadlessFolder, runtimeSettings.LocalHeadlessPlayerId);

    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(JsonSerializer.Serialize(snapshot));
});

// SignalR Hub registration
app.MapHub<ServerHub>("/api/hub");

// Example endpoint to notify all clients that server is restarting
app.MapPost("/api/events/restart", async (IHubContext<ServerHub> hub) =>
{
    await hub.Clients.All.SendAsync("ServerRestarting", "Server will restart soon");
    return Results.Ok("Notification sent");
});

// Notify SPT Server offline on shutdown
app.MapPost("/api/events/spt/offline", async (IHubContext<ServerHub> hub) =>
{
    await hub.Clients.All.SendAsync("SptServerOffline", "SPT Server is going offline");
    return Results.Ok("Notification sent");
});

// Notify SPT Server online on startup
app.MapPost("/api/events/spt/online", async (IHubContext<ServerHub> hub) =>
{
    await hub.Clients.All.SendAsync("SptServerOnline", "SPT Server is online");
    return Results.Ok("Notification sent");
});

// Notify SPT Server Restart
app.MapPost("/api/events/spt/restart", async (IHubContext<ServerHub> hub) =>
{
    await hub.Clients.All.SendAsync("SptServerRestarting", "SPT Server is restarting");
    return Results.Ok("Notification sent");
});

// Notify SPT Server updating
app.MapPost("/api/events/spt/updating", async (IHubContext<ServerHub> hub) =>
{
    await hub.Clients.All.SendAsync("SptServerUpdating", "SPT Server is updating");
    return Results.Ok("Notification sent");
});

// Notify Headless Closed
app.MapPost("/api/events/headless/offline", async (IHubContext<ServerHub> hub) =>
{
    await hub.Clients.All.SendAsync("HeadlessOffline", "Headless client is offline");
    return Results.Ok("Notification sent");
});

// Notify Headless Online
app.MapPost("/api/events/headless/online", async (IHubContext<ServerHub> hub) =>
{
    await hub.Clients.All.SendAsync("HeadlessOnline", "Headless client is online");
    return Results.Ok("Notification sent");
});

// Notify Headless Restart
app.MapPost("/api/events/headless/restart", async (IHubContext<ServerHub> hub) =>
{
    await hub.Clients.All.SendAsync("HeadlessRestarted", "Headless has been closed / restarting");
    return Results.Ok("Notification sent");
});

// Wrap the app startup/run to catch and display any exceptions so the console remains open
try
{
    app.Run($"http://0.0.0.0:{listenPort}");
}
catch (Exception ex)
{
    Console.WriteLine("Error while running web server: " + ex);
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey();
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


public class ServerHub : Hub { }

public static class SptCoffeeDb
{
    public static void EnsureSchema(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS plugins (
    name TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
    version TEXT NOT NULL,
    file_name TEXT NOT NULL,
    is_folder_mod INTEGER NOT NULL,
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

    public static List<ModInfo> ReadPlugins(string dbPath)
    {
        var mods = new List<ModInfo>();
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, version, file_name, is_folder_mod FROM plugins ORDER BY name COLLATE NOCASE;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            mods.Add(new ModInfo
            {
                Name = reader.GetString(0),
                Version = reader.GetString(1),
                FileName = reader.GetString(2),
                IsFolderMod = reader.GetInt32(3) == 1
            });
        }

        return mods;
    }

    public static List<ConfigInfo> ReadConfigs(string dbPath)
    {
        var configs = new List<ConfigInfo>();
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_name, last_modified_utc, is_enforced FROM configs ORDER BY file_name COLLATE NOCASE;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var parsedDate = DateTime.TryParse(reader.GetString(1), out var dt) ? dt : DateTime.MinValue;
            configs.Add(new ConfigInfo
            {
                FileName = reader.GetString(0),
                LastModified = parsedDate,
                IsEnforced = reader.GetInt32(2) == 1
            });
        }

        return configs;
    }

    public static List<AdminConfig> ReadAdmins(string dbPath)
    {
        var admins = new List<AdminConfig>();
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

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

    public static void MigrateLegacyDataIfNeeded(string dbPath, string legacySptServerFolder, string legacyAdditionalModsPath)
    {
        var plugins = ReadPlugins(dbPath);
        if (plugins.Count == 0)
        {
            MigrateLegacyModCatalogDbIfNeeded(dbPath);
            MigrateLegacyPluginVersionsIfNeeded(dbPath);
        }

        var configs = ReadConfigs(dbPath);
        if (configs.Count == 0)
        {
            MigrateLegacyConfigFilesIfNeeded(dbPath);
        }

        var admins = ReadAdmins(dbPath);
        if (admins.Count == 0)
        {
            MigrateLegacyAdminsIfNeeded(dbPath);
        }

        MigrateLegacySettingsIfNeeded(dbPath, legacySptServerFolder, legacyAdditionalModsPath);
    }

    public static RuntimeSettings ReadRuntimeSettings(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        var sptServerFolder = GetSetting(connection, "spt_server_folder");
        var additionalModsPath = GetSetting(connection, "additional_mods_path");
        var headlessFolder = GetSetting(connection, "headless_folder");
        var localHeadlessPlayerId = GetSetting(connection, "local_headless_player_id");

        return new RuntimeSettings
        {
            SptServerFolder = string.IsNullOrWhiteSpace(sptServerFolder) ? @"C:\SPT" : sptServerFolder,
            AdditionalModsPath = additionalModsPath ?? string.Empty,
            HeadlessFolder = headlessFolder ?? string.Empty,
            LocalHeadlessPlayerId = localHeadlessPlayerId ?? string.Empty
        };
    }

    private static void MigrateLegacyPluginVersionsIfNeeded(string dbPath)
    {

        var legacyJsonPath = Path.Combine(AppContext.BaseDirectory, "PluginVersions.json");
        if (!File.Exists(legacyJsonPath))
        {
            return;
        }

        List<ModInfo>? legacyMods;
        try
        {
            legacyMods = JsonSerializer.Deserialize<List<ModInfo>>(File.ReadAllText(legacyJsonPath));
        }
        catch
        {
            return;
        }

        if (legacyMods == null || legacyMods.Count == 0)
        {
            return;
        }

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var tx = connection.BeginTransaction();

        foreach (var mod in legacyMods)
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = @"
INSERT INTO plugins(name, version, file_name, is_folder_mod, updated_utc)
VALUES($name, $version, $fileName, $isFolderMod, $updatedUtc)
ON CONFLICT(name) DO UPDATE SET
    version = excluded.version,
    file_name = excluded.file_name,
    is_folder_mod = excluded.is_folder_mod,
    updated_utc = excluded.updated_utc;";
            command.Parameters.AddWithValue("$name", mod.Name);
            command.Parameters.AddWithValue("$version", mod.Version);
            command.Parameters.AddWithValue("$fileName", mod.FileName);
            command.Parameters.AddWithValue("$isFolderMod", mod.IsFolderMod ? 1 : 0);
            command.Parameters.AddWithValue("$updatedUtc", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void MigrateLegacyModCatalogDbIfNeeded(string dbPath)
    {
        var legacyDbPath = Path.Combine(AppContext.BaseDirectory, "ModCatalog.db");
        if (!File.Exists(legacyDbPath))
        {
            return;
        }

        var legacyMods = new List<ModInfo>();
        using (var legacyConnection = new SqliteConnection($"Data Source={legacyDbPath}"))
        {
            legacyConnection.Open();
            using var readCommand = legacyConnection.CreateCommand();
            readCommand.CommandText = "SELECT name, version, file_name, is_folder_mod FROM mods ORDER BY name COLLATE NOCASE;";

            using var reader = readCommand.ExecuteReader();
            while (reader.Read())
            {
                legacyMods.Add(new ModInfo
                {
                    Name = reader.GetString(0),
                    Version = reader.GetString(1),
                    FileName = reader.GetString(2),
                    IsFolderMod = reader.GetInt32(3) == 1
                });
            }
        }

        if (legacyMods.Count == 0)
        {
            return;
        }

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var tx = connection.BeginTransaction();

        foreach (var mod in legacyMods)
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = @"
INSERT INTO plugins(name, version, file_name, is_folder_mod, updated_utc)
VALUES($name, $version, $fileName, $isFolderMod, $updatedUtc)
ON CONFLICT(name) DO UPDATE SET
    version = excluded.version,
    file_name = excluded.file_name,
    is_folder_mod = excluded.is_folder_mod,
    updated_utc = excluded.updated_utc;";
            command.Parameters.AddWithValue("$name", mod.Name);
            command.Parameters.AddWithValue("$version", mod.Version);
            command.Parameters.AddWithValue("$fileName", mod.FileName);
            command.Parameters.AddWithValue("$isFolderMod", mod.IsFolderMod ? 1 : 0);
            command.Parameters.AddWithValue("$updatedUtc", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void MigrateLegacyConfigFilesIfNeeded(string dbPath)
    {
        var legacyJsonPath = Path.Combine(AppContext.BaseDirectory, "ConfigFiles.json");
        if (!File.Exists(legacyJsonPath))
        {
            return;
        }

        List<ConfigInfo>? legacyConfigs;
        try
        {
            legacyConfigs = JsonSerializer.Deserialize<List<ConfigInfo>>(File.ReadAllText(legacyJsonPath));
        }
        catch
        {
            return;
        }

        if (legacyConfigs == null || legacyConfigs.Count == 0)
        {
            return;
        }

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var tx = connection.BeginTransaction();

        foreach (var config in legacyConfigs)
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
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

        tx.Commit();
    }

    private static void MigrateLegacySettingsIfNeeded(string dbPath, string legacySptServerFolder, string legacyAdditionalModsPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        if (!string.IsNullOrWhiteSpace(GetSetting(connection, "spt_server_folder")))
        {
            return;
        }

        UpsertSetting(connection, "spt_server_folder", string.IsNullOrWhiteSpace(legacySptServerFolder) ? @"C:\SPT" : legacySptServerFolder);
        UpsertSetting(connection, "additional_mods_path", legacyAdditionalModsPath ?? string.Empty);
    }

    private static void MigrateLegacyAdminsIfNeeded(string dbPath)
    {
        var legacyJsonPath = Path.Combine(AppContext.BaseDirectory, "admins.json");
        if (!File.Exists(legacyJsonPath))
        {
            return;
        }

        List<AdminConfig>? legacyAdmins;
        try
        {
            legacyAdmins = JsonSerializer.Deserialize<List<AdminConfig>>(File.ReadAllText(legacyJsonPath));
        }
        catch
        {
            return;
        }

        if (legacyAdmins == null || legacyAdmins.Count == 0)
        {
            return;
        }

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var tx = connection.BeginTransaction();

        foreach (var admin in legacyAdmins)
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = @"
INSERT INTO admins(note, secret, is_enabled, allow_headless_close, updated_utc)
VALUES($note, $secret, $isEnabled, $allowHeadlessClose, $updatedUtc)
ON CONFLICT(secret) DO UPDATE SET
    note = excluded.note,
    is_enabled = excluded.is_enabled,
    allow_headless_close = excluded.allow_headless_close,
    updated_utc = excluded.updated_utc;";
            command.Parameters.AddWithValue("$note", admin.Note ?? string.Empty);
            command.Parameters.AddWithValue("$secret", admin.Secret ?? string.Empty);
            command.Parameters.AddWithValue("$isEnabled", admin.IsEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$allowHeadlessClose", admin.AllowHeadlessClose ? 1 : 0);
            command.Parameters.AddWithValue("$updatedUtc", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        tx.Commit();
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
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$updatedUtc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }
}

public static class SptPlayerPresenceReader
{
    private static readonly Regex PlayerEventRegex = new(@"\[(?:WS|ws)\]\s*Player:\s*(?<name>.+?)\s*\((?<id>[^)]+)\)\s*(?<stamp>\d+)\s*has\s+(?<state>connected|disconnected)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RequestPathRegex = new(@"\[(?:Client|WebSocket)\s+Request\]\s*(?<path>/\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NotifierWebSocketRequestRegex = new(@"^/notifierServer/getwebsocket/(?<id>[a-f0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LocalStartRegex = new(@"\[Client Request\]\s*/client/match/local/start", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HeadlessStartRegex = new(@"\[Client Request\]\s*/fika/raid/headless/start", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HeadlessPlayerRegex = new(@"CoopHandler\]\s*AddClientToBotEnemies:\s*(?<name>.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HeadlessWaitingForHostRegex = new(@"HeadlessGameController\]\s*Starting task to wait for host to start the raid\.?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HeadlessAllPlayersLoadedRegex = new(@"HeadlessGameController\]\s*All players are loaded, continuing\.\.\.", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HeadlessWebSocketConnectedRegex = new(@"Connected to HeadlessWebSocket", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HeadlessLocationRegex = new(@"HeadlessGame\]\s*Location:\s*(?<location>.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HeadlessSessionEndRegex = new(@"Fika Server Session Statistics|NetManagerUtils\]\s*Destroyed FikaServer", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly object PresenceSync = new();

    private const string StateConnected = "Connected";
    private const string StateDisconnected = "Disconnected";
    private const string StateDisconnectedOrJoining = "Disconnected or Joining";
    private const string StateDisconnectedAfterLocalRaid = "Disconnected after Local Raid";
    private const string StateDisconnectedAfterHeadlessRaid = "Disconnected after Headless Raid";
    private const string StateInSoloRaid = "In Solo Raid";
    private const string StateInHeadlessRaid = "In Headless Raid";
    private const string StateStartingRaid = "Starting Raid";
    private const string StateWaitingForRaid = "Waiting for Raid";
    private const string StateHostingRaid = "Hosting Raid";
    private const string HeadlessStatusRestarting = "Restarting for New Raid";
    private const string UnknownHeadlessLocation = "Unknown";

    private static string _cachedLogPath = string.Empty;
    private static long _cachedOffset;
    private static string _pendingLineFragment = string.Empty;
    private static readonly Dictionary<string, PlayerPresenceInfo> CachedPlayers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<PlayerPresenceEventInfo> CachedRecentEvents = [];
    private const int MaxRecentEvents = 45;

    private static int _pendingSoloRaidStarts;
    private static bool _headlessStartAwaitingLocalStart;
    private static int _pendingHeadlessHostStarts;
    private static bool _headlessRaidLoading;
    private static bool _headlessReadyWaitingForRaid;
    private static string _lastRequestPath = string.Empty;
    private static readonly HashSet<string> PendingHeadlessJoinPlayerKeys = new(StringComparer.OrdinalIgnoreCase);
    private static string _configuredLocalHeadlessPlayerId = string.Empty;

    private static string _cachedHeadlessLogPath = string.Empty;
    private static long _cachedHeadlessOffset;
    private static string _pendingHeadlessFragment = string.Empty;
    private static readonly HashSet<string> ParsedHeadlessRaidPlayers = new(StringComparer.OrdinalIgnoreCase);
    private static string _headlessLocation = UnknownHeadlessLocation;

    public static PlayerPresenceSnapshot BuildSnapshot(string serverFolder, string headlessFolder = "", string localHeadlessPlayerId = "")
    {
        var now = DateTime.UtcNow;
        var isServerRunning = IsSptServerRunning();

        lock (PresenceSync)
        {
            _configuredLocalHeadlessPlayerId = localHeadlessPlayerId?.Trim() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(serverFolder) || !Directory.Exists(serverFolder))
        {
            return new PlayerPresenceSnapshot
            {
                LastUpdatedUtc = now,
                IsServerRunning = isServerRunning,
                Error = string.IsNullOrWhiteSpace(serverFolder)
                    ? "SPT server folder is not configured."
                    : $"SPT server folder not found: {serverFolder}"
            };
        }

        var logFile = FindLatestLogFile(serverFolder);
        if (logFile == null)
        {
            return new PlayerPresenceSnapshot
            {
                LastUpdatedUtc = now,
                IsServerRunning = isServerRunning,
                Error = "No SPT.Server log file with player presence lines was found."
            };
        }

        if (!isServerRunning)
        {
            ClearConnectedPlayers();

            List<PlayerPresenceInfo> disconnectedPlayers;
            lock (PresenceSync)
            {
                disconnectedPlayers = CachedPlayers.Values
                    .OrderBy(GetStateSortRank)
                    .ThenBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return new PlayerPresenceSnapshot
            {
                LogFilePath = logFile,
                LogLastWriteUtc = File.GetLastWriteTimeUtc(logFile),
                LastUpdatedUtc = now,
                IsServerRunning = false,
                StatusMessage = "SPT.Server is stopped. All players are disconnected.",
                ActiveCount = 0,
                ActivePlayers = disconnectedPlayers,
                RecentEvents = [],
                CurrentRaidType = "None",
                HeadlessStatus = StateDisconnected,
                HeadlessLocation = UnknownHeadlessLocation,
                HeadlessRaidPlayers = []
            };
        }

        TailAndParseLatestLog(logFile);

        if (!string.IsNullOrWhiteSpace(headlessFolder))
        {
            TailAndParseHeadlessLog(headlessFolder);
        }
        else
        {
            lock (PresenceSync)
            {
                ResetHeadlessCache(markPlayersDisconnected: true);
            }
        }

        List<PlayerPresenceInfo> trackedPlayers;
        List<PlayerPresenceEventInfo> recentEvents;
        string raidType;
        string headlessStatus;
        string headlessLocation;
        List<string> headlessPlayers;
        int activeCount;
        string statusMessage;

        lock (PresenceSync)
        {
            trackedPlayers = CachedPlayers.Values
                .OrderBy(GetStateSortRank)
                .ThenBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            recentEvents = CachedRecentEvents.ToList();
            raidType = GetCurrentRaidType();
            headlessStatus = GetHeadlessStatus();
            headlessLocation = GetHeadlessLocation(headlessStatus);
            headlessPlayers = GetHeadlessRaidPlayerNames();
            activeCount = trackedPlayers.Count(player => IsActivePlayerState(player.State));
            statusMessage = BuildStatusMessage(activeCount, raidType, headlessStatus);
        }

        return new PlayerPresenceSnapshot
        {
            LogFilePath = logFile,
            LogLastWriteUtc = File.GetLastWriteTimeUtc(logFile),
            LastUpdatedUtc = now,
            IsServerRunning = true,
            StatusMessage = statusMessage,
            ActiveCount = activeCount,
            ActivePlayers = trackedPlayers,
            RecentEvents = recentEvents,
            CurrentRaidType = raidType,
            HeadlessStatus = headlessStatus,
            HeadlessLocation = headlessLocation,
            HeadlessRaidPlayers = headlessPlayers
        };
    }

    private static string? FindLatestLogFile(string serverFolder)
    {
        var candidateDirectories = new[]
        {
            Path.Combine(serverFolder, "SPT", "user", "logs", "spt"),
            Path.Combine(serverFolder, "SPT", "user", "logs"),
            Path.Combine(serverFolder, "SPT", "Logs"),
            Path.Combine(serverFolder, "SPT", "logs"),
            Path.Combine(serverFolder, "user", "logs"),
            Path.Combine(serverFolder, "user", "Logs"),
            Path.Combine(serverFolder, "logs"),
            Path.Combine(serverFolder, "Logs")
        };

        var files = new List<string>();

        foreach (var directory in candidateDirectories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            files.AddRange(Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly));
            files.AddRange(Directory.EnumerateFiles(directory, "*.txt", SearchOption.TopDirectoryOnly));
        }

        return files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static bool IsSptServerRunning()
    {
        var regex = new Regex(@"^SPT\.Server$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        return Process.GetProcesses().Any(p =>
        {
            try
            {
                return regex.IsMatch(p.ProcessName);
            }
            catch
            {
                return false;
            }
        });
    }

    private static bool IsHeadlessManagerRunning()
    {
        var regex = new Regex(@"^FikaHeadlessManager$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        return Process.GetProcesses().Any(p =>
        {
            try
            {
                return regex.IsMatch(p.ProcessName);
            }
            catch
            {
                return false;
            }
        });
    }

    private static void TailAndParseLatestLog(string logFile)
    {
        lock (PresenceSync)
        {
            if (!string.Equals(_cachedLogPath, logFile, StringComparison.OrdinalIgnoreCase))
            {
                ResetCache(logFile);
            }

            using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length < _cachedOffset)
            {
                // File rotated/truncated.
                ResetCache(logFile);
            }

            stream.Seek(_cachedOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var chunk = reader.ReadToEnd();
            _cachedOffset = stream.Length;

            if (chunk.Length == 0)
            {
                return;
            }

            var pendingAndChunk = _pendingLineFragment + chunk;
            var splitLines = pendingAndChunk.Split('\n');
            var processCount = splitLines.Length;

            if (!pendingAndChunk.EndsWith("\n", StringComparison.Ordinal))
            {
                _pendingLineFragment = splitLines[^1];
                processCount--;
            }
            else
            {
                _pendingLineFragment = string.Empty;
            }

            for (var i = 0; i < processCount; i++)
            {
                ProcessLogLine(splitLines[i].TrimEnd('\r'));
            }
        }
    }

    private static void TailAndParseHeadlessLog(string headlessFolder)
    {
        var headlessLogPath = Path.Combine(headlessFolder, "BepInEx", "LogOutput.log");
        if (!File.Exists(headlessLogPath))
        {
            lock (PresenceSync)
            {
                if (!string.IsNullOrEmpty(_cachedHeadlessLogPath) || ParsedHeadlessRaidPlayers.Count > 0)
                {
                    ResetHeadlessCache(markPlayersDisconnected: true);
                }
            }

            return;
        }

        lock (PresenceSync)
        {
            if (!string.Equals(_cachedHeadlessLogPath, headlessLogPath, StringComparison.OrdinalIgnoreCase))
            {
                ResetHeadlessCache(markPlayersDisconnected: false);
                _cachedHeadlessLogPath = headlessLogPath;
            }

            try
            {
                using var stream = new FileStream(headlessLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                if (stream.Length < _cachedHeadlessOffset)
                {
                    ResetHeadlessCache(markPlayersDisconnected: true);
                    _cachedHeadlessLogPath = headlessLogPath;
                }

                stream.Seek(_cachedHeadlessOffset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                var chunk = reader.ReadToEnd();
                _cachedHeadlessOffset = stream.Length;

                if (chunk.Length == 0)
                    return;

                var pendingAndChunk = _pendingHeadlessFragment + chunk;
                var splitLines = pendingAndChunk.Split('\n');
                var processCount = splitLines.Length;

                if (!pendingAndChunk.EndsWith("\n", StringComparison.Ordinal))
                {
                    _pendingHeadlessFragment = splitLines[^1];
                    processCount--;
                }
                else
                {
                    _pendingHeadlessFragment = string.Empty;
                }

                for (var i = 0; i < processCount; i++)
                {
                    ProcessHeadlessLogLine(splitLines[i].TrimEnd('\r'));
                }
            }
            catch
            {
                // Ignore file access errors
            }
        }
    }

    private static void ProcessLogLine(string line)
    {
        var requestPathMatch = RequestPathRegex.Match(line);
        if (requestPathMatch.Success)
        {
            var requestPath = requestPathMatch.Groups["path"].Value.Trim();
            _lastRequestPath = requestPath;

            var notifierMatch = NotifierWebSocketRequestRegex.Match(requestPath);
            if (notifierMatch.Success)
            {
                var accountId = notifierMatch.Groups["id"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(accountId))
                {
                    var (notifierPlayerKey, notifierPlayer) = GetOrCreateTrackedPlayer(accountId, string.Empty);
                    notifierPlayer.AccountId = accountId;
                    notifierPlayer.State = StateConnected;
                    PendingHeadlessJoinPlayerKeys.Remove(notifierPlayerKey);
                }
            }
        }

        if (HeadlessStartRegex.IsMatch(line))
        {
            _headlessStartAwaitingLocalStart = true;
            _headlessRaidLoading = true;
            _headlessReadyWaitingForRaid = false;
            _headlessLocation = UnknownHeadlessLocation;
            PendingHeadlessJoinPlayerKeys.Clear();
            ParsedHeadlessRaidPlayers.Clear();
            MarkHeadlessRaidPlayersDisconnected();
            SeedPendingHeadlessJoinCandidates();
            return;
        }

        if (LocalStartRegex.IsMatch(line))
        {
            if (_headlessStartAwaitingLocalStart)
            {
                _pendingHeadlessHostStarts++;
                _headlessStartAwaitingLocalStart = false;
            }
            else
            {
                _pendingSoloRaidStarts++;
            }
            return;
        }

        if (!TryParsePlayerEvent(line, out var playerEvent))
        {
            return;
        }

        var (playerKey, player) = GetOrCreateTrackedPlayer(playerEvent.AccountId, playerEvent.Name);
        player.Name = playerEvent.Name;
        player.AccountId = playerEvent.AccountId;
        player.LastSeenStamp = playerEvent.Stamp;
        var previousState = player.State;

        if (playerEvent.IsConnected)
        {
            player.State = StateConnected;
            PendingHeadlessJoinPlayerKeys.Remove(playerKey);
        }
        else
        {
            if (string.Equals(previousState, StateInSoloRaid, StringComparison.OrdinalIgnoreCase))
            {
                player.State = StateDisconnectedAfterLocalRaid;
                PendingHeadlessJoinPlayerKeys.Remove(playerKey);
            }
            else if (string.Equals(previousState, StateInHeadlessRaid, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(previousState, StateHostingRaid, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(previousState, StateStartingRaid, StringComparison.OrdinalIgnoreCase))
            {
                player.State = StateDisconnectedAfterHeadlessRaid;
                PendingHeadlessJoinPlayerKeys.Remove(playerKey);
                ParsedHeadlessRaidPlayers.Remove(player.Name);
            }
            else if (IsLocalHeadlessProfile(player))
            {
                if (_pendingHeadlessHostStarts > 0)
                {
                    _pendingHeadlessHostStarts--;
                }

                player.State = (_headlessRaidLoading || ParsedHeadlessRaidPlayers.Count > 0 || string.Equals(player.State, StateHostingRaid, StringComparison.OrdinalIgnoreCase))
                    ? StateStartingRaid
                    : StateDisconnected;
            }
            else if (string.Equals(_lastRequestPath, "/singleplayer/settings/getRaidTime", StringComparison.OrdinalIgnoreCase))
            {
                player.State = StateDisconnectedOrJoining;
                _lastRequestPath = string.Empty;
                if (_headlessRaidLoading)
                {
                    PendingHeadlessJoinPlayerKeys.Add(playerKey);
                }
            }
            else if (_pendingSoloRaidStarts > 0)
            {
                player.State = StateInSoloRaid;
                _pendingSoloRaidStarts--;
            }
            else
            {
                player.State = StateDisconnected;
                PendingHeadlessJoinPlayerKeys.Remove(playerKey);
            }
        }

        CachedRecentEvents.Add(playerEvent);
        if (CachedRecentEvents.Count > MaxRecentEvents)
        {
            CachedRecentEvents.RemoveAt(0);
        }
    }

    private static void ProcessHeadlessLogLine(string line)
    {
        if (HeadlessSessionEndRegex.IsMatch(line))
        {
            ResetHeadlessCache(markPlayersDisconnected: true);
            return;
        }

        if (HeadlessWebSocketConnectedRegex.IsMatch(line))
        {
            _headlessReadyWaitingForRaid = true;
            return;
        }

        var locationMatch = HeadlessLocationRegex.Match(line);
        if (locationMatch.Success)
        {
            var location = locationMatch.Groups["location"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(location))
            {
                _headlessLocation = location;
            }

            return;
        }

        if (HeadlessWaitingForHostRegex.IsMatch(line))
        {
            _headlessRaidLoading = true;
            _headlessReadyWaitingForRaid = false;
        }

        var match = HeadlessPlayerRegex.Match(line);
        if (match.Success)
        {
            var name = match.Groups["name"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                ParsedHeadlessRaidPlayers.Add(name);
            }

            return;
        }

        if (HeadlessAllPlayersLoadedRegex.IsMatch(line))
        {
            FinalizeHeadlessRaidLoad();
        }
    }

    private static void ResetCache(string logFile)
    {
        _cachedLogPath = logFile;
        _cachedOffset = 0;
        _pendingLineFragment = string.Empty;
        CachedRecentEvents.Clear();
        _pendingSoloRaidStarts = 0;
        _headlessStartAwaitingLocalStart = false;
        _pendingHeadlessHostStarts = 0;
        _lastRequestPath = string.Empty;
    }

    private static void ResetHeadlessCache(bool markPlayersDisconnected)
    {
        if (markPlayersDisconnected)
        {
            MarkHeadlessRaidPlayersDisconnected();
        }

        _cachedHeadlessLogPath = string.Empty;
        _cachedHeadlessOffset = 0;
        _pendingHeadlessFragment = string.Empty;
        ParsedHeadlessRaidPlayers.Clear();
        PendingHeadlessJoinPlayerKeys.Clear();
        _headlessRaidLoading = false;
        _headlessReadyWaitingForRaid = false;
        _headlessLocation = UnknownHeadlessLocation;
        _headlessStartAwaitingLocalStart = false;
        _pendingHeadlessHostStarts = 0;
    }

    private static void ClearConnectedPlayers()
    {
        lock (PresenceSync)
        {
            CachedRecentEvents.Clear();
            _pendingSoloRaidStarts = 0;
            ResetHeadlessCache(markPlayersDisconnected: true);
            _lastRequestPath = string.Empty;

            foreach (var player in CachedPlayers.Values)
            {
                player.State = StateDisconnected;
            }
        }
    }

    private static void FinalizeHeadlessRaidLoad()
    {
        _headlessRaidLoading = false;
        _headlessReadyWaitingForRaid = false;

        foreach (var entry in CachedPlayers.ToList())
        {
            var player = entry.Value;
            if (IsLocalHeadlessProfile(player))
            {
                player.State = StateHostingRaid;
                continue;
            }

            if (ParsedHeadlessRaidPlayers.Contains(player.Name))
            {
                player.State = StateInHeadlessRaid;
                PendingHeadlessJoinPlayerKeys.Remove(entry.Key);
                continue;
            }

            if (PendingHeadlessJoinPlayerKeys.Contains(entry.Key))
            {
                player.State = StateDisconnected;
            }
        }

        foreach (var name in ParsedHeadlessRaidPlayers)
        {
            var (_, player) = GetOrCreateTrackedPlayer(string.Empty, name);
            player.Name = name;
            player.State = StateInHeadlessRaid;
        }

        if (TryGetHeadlessProfile(out _, out var headlessProfile))
        {
            headlessProfile.State = StateHostingRaid;
        }

        PendingHeadlessJoinPlayerKeys.Clear();
    }

    private static (string PlayerKey, PlayerPresenceInfo Player) GetOrCreateTrackedPlayer(string accountId, string name)
    {
        var trimmedName = name.Trim();

        if (!string.IsNullOrWhiteSpace(accountId) && CachedPlayers.TryGetValue(accountId, out var byAccountId))
        {
            return (accountId, byAccountId);
        }

        var nameOnlyKey = GetNameOnlyKey(trimmedName);
        if (!string.IsNullOrWhiteSpace(trimmedName) && CachedPlayers.TryGetValue(nameOnlyKey, out var placeholderPlayer))
        {
            if (!string.IsNullOrWhiteSpace(accountId))
            {
                CachedPlayers.Remove(nameOnlyKey);
                placeholderPlayer.AccountId = accountId;
                CachedPlayers[accountId] = placeholderPlayer;
                return (accountId, placeholderPlayer);
            }

            return (nameOnlyKey, placeholderPlayer);
        }

        if (!string.IsNullOrWhiteSpace(trimmedName))
        {
            var byName = CachedPlayers.FirstOrDefault(kvp => string.Equals(kvp.Value.Name, trimmedName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(byName.Key))
            {
                if (!string.IsNullOrWhiteSpace(accountId) && !string.Equals(byName.Key, accountId, StringComparison.OrdinalIgnoreCase))
                {
                    CachedPlayers.Remove(byName.Key);
                    byName.Value.AccountId = accountId;
                    CachedPlayers[accountId] = byName.Value;
                    return (accountId, byName.Value);
                }

                return (byName.Key, byName.Value);
            }
        }

        var newPlayer = new PlayerPresenceInfo
        {
            Name = trimmedName,
            AccountId = accountId,
            State = StateDisconnected
        };

        var newKey = !string.IsNullOrWhiteSpace(accountId) ? accountId : nameOnlyKey;
        CachedPlayers[newKey] = newPlayer;
        return (newKey, newPlayer);
    }

    private static string GetNameOnlyKey(string name) => $"name:{name}";

    private static bool IsActivePlayerState(string state) =>
        !string.Equals(state, StateDisconnected, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(state, StateDisconnectedAfterLocalRaid, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(state, StateDisconnectedAfterHeadlessRaid, StringComparison.OrdinalIgnoreCase);

    private static bool IsHeadlessPlayerState(string state) =>
        string.Equals(state, StateStartingRaid, StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, StateHostingRaid, StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, StateInHeadlessRaid, StringComparison.OrdinalIgnoreCase);

    private static int GetStateSortRank(PlayerPresenceInfo player) => player.State switch
    {
        StateConnected => 0,
        StateDisconnectedOrJoining => 1,
        StateInSoloRaid => 2,
        StateDisconnectedAfterLocalRaid => 3,
        StateStartingRaid => 4,
        StateHostingRaid => 5,
        StateInHeadlessRaid => 6,
        StateDisconnectedAfterHeadlessRaid => 7,
        _ => 8
    };

    private static string GetCurrentRaidType()
    {
        var hasHeadless = _headlessRaidLoading
            || ParsedHeadlessRaidPlayers.Count > 0
            || CachedPlayers.Values.Any(player => IsHeadlessPlayerState(player.State));
        var hasSolo = _pendingSoloRaidStarts > 0
            || CachedPlayers.Values.Any(player => !IsLocalHeadlessProfile(player) && string.Equals(player.State, StateInSoloRaid, StringComparison.OrdinalIgnoreCase));

        if (hasHeadless && hasSolo)
        {
            return "Mixed";
        }

        if (hasHeadless)
        {
            return "Headless";
        }

        if (hasSolo)
        {
            return "Solo";
        }

        return "None";
    }

    private static List<string> GetHeadlessRaidPlayerNames()
    {
        return CachedPlayers.Values
            .Where(player => !IsLocalHeadlessProfile(player) && string.Equals(player.State, StateInHeadlessRaid, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(player.Name))
            .Select(player => player.Name)
            .Concat(ParsedHeadlessRaidPlayers)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetHeadlessStatus()
    {
        if (TryGetHeadlessProfile(out _, out var headlessProfile))
        {
            if (string.Equals(headlessProfile.State, StateDisconnectedAfterHeadlessRaid, StringComparison.OrdinalIgnoreCase))
            {
                return IsHeadlessManagerRunning() ? HeadlessStatusRestarting : StateDisconnected;
            }

            return headlessProfile.State;
        }

        if (_headlessRaidLoading)
        {
            return StateStartingRaid;
        }

        if (ParsedHeadlessRaidPlayers.Count > 0)
        {
            return StateHostingRaid;
        }

        if (_headlessReadyWaitingForRaid)
        {
            return StateWaitingForRaid;
        }

        if (IsHeadlessManagerRunning())
        {
            return HeadlessStatusRestarting;
        }

        return StateDisconnected;
    }

    private static string GetHeadlessLocation(string headlessStatus)
    {
        if (string.Equals(headlessStatus, StateDisconnected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(headlessStatus, HeadlessStatusRestarting, StringComparison.OrdinalIgnoreCase))
        {
            return UnknownHeadlessLocation;
        }

        return string.IsNullOrWhiteSpace(_headlessLocation) ? UnknownHeadlessLocation : _headlessLocation;
    }

    private static string BuildStatusMessage(int activeCount, string raidType, string headlessStatus)
    {
        var baseMessage = $"Tracking {activeCount} player(s).";

        if (raidType == "Mixed")
        {
            return $"{baseMessage} Solo and headless raids are active.";
        }

        if (raidType == "Solo")
        {
            return $"{baseMessage} Solo raids are active.";
        }

        if (raidType == "Headless")
        {
            return $"{baseMessage} Headless status: {headlessStatus}.";
        }

        return baseMessage;
    }

    private static bool IsLocalHeadlessProfile(PlayerPresenceInfo player)
    {
        if (player == null)
        {
            return false;
        }

        var configuredId = _configuredLocalHeadlessPlayerId;
        if (!string.IsNullOrWhiteSpace(configuredId))
        {
            return string.Equals(player.AccountId, configuredId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(player.Name, configuredId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(player.Name, $"headless_{configuredId}", StringComparison.OrdinalIgnoreCase);
        }

        return player.Name.StartsWith("headless_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetHeadlessProfile(out string playerKey, out PlayerPresenceInfo player)
    {
        foreach (var entry in CachedPlayers)
        {
            if (IsLocalHeadlessProfile(entry.Value))
            {
                playerKey = entry.Key;
                player = entry.Value;
                return true;
            }
        }

        playerKey = string.Empty;
        player = null!;
        return false;
    }

    private static void MarkHeadlessRaidPlayersDisconnected()
    {
        foreach (var player in CachedPlayers.Values)
        {
            if (IsHeadlessPlayerState(player.State))
            {
                player.State = StateDisconnectedAfterHeadlessRaid;
            }
        }

        foreach (var key in PendingHeadlessJoinPlayerKeys.ToList())
        {
            if (CachedPlayers.TryGetValue(key, out var pendingPlayer)
                && string.Equals(pendingPlayer.State, StateDisconnectedOrJoining, StringComparison.OrdinalIgnoreCase))
            {
                pendingPlayer.State = StateDisconnectedAfterHeadlessRaid;
            }
        }
    }

    private static void SeedPendingHeadlessJoinCandidates()
    {
        foreach (var entry in CachedPlayers)
        {
            var player = entry.Value;
            if (IsLocalHeadlessProfile(player))
            {
                continue;
            }

            if (string.Equals(player.State, StateDisconnectedOrJoining, StringComparison.OrdinalIgnoreCase))
            {
                PendingHeadlessJoinPlayerKeys.Add(entry.Key);
            }
        }
    }

    private static bool TryParsePlayerEvent(string line, out PlayerPresenceEventInfo playerEvent)
    {
        var match = PlayerEventRegex.Match(line);
        if (!match.Success)
        {
            playerEvent = null!;
            return false;
        }

        playerEvent = new PlayerPresenceEventInfo
        {
            Name = match.Groups["name"].Value.Trim(),
            AccountId = match.Groups["id"].Value.Trim(),
            Stamp = match.Groups["stamp"].Value.Trim(),
            IsConnected = string.Equals(match.Groups["state"].Value, "connected", StringComparison.OrdinalIgnoreCase)
        };

        return true;
    }
}

public class RuntimeSettings
{
    public string SptServerFolder { get; set; } = @"C:\SPT";
    public string AdditionalModsPath { get; set; } = string.Empty;
    public string HeadlessFolder { get; set; } = string.Empty;
    public string LocalHeadlessPlayerId { get; set; } = string.Empty;
}

