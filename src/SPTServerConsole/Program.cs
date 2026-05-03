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

        return new RuntimeSettings
        {
            SptServerFolder = string.IsNullOrWhiteSpace(sptServerFolder) ? @"C:\SPT" : sptServerFolder,
            AdditionalModsPath = additionalModsPath ?? string.Empty
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

public class RuntimeSettings
{
    public string SptServerFolder { get; set; } = @"C:\SPT";
    public string AdditionalModsPath { get; set; } = string.Empty;
}

