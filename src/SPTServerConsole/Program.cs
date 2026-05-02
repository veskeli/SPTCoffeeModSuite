using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

var configPath = "config.json";

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

var config = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(configPath))!;
var modZipPath = Path.Combine(AppContext.BaseDirectory, "temp", "ModZips");
Directory.CreateDirectory(modZipPath);

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();
var app = builder.Build();

// last spt version read time
DateTime lastSptVersionRead = DateTime.MinValue;
string cachedSptVersion = "";

// GET /PluginVersions.json -> return PluginVersions.json
app.MapGet("/PluginVersions.json", async context =>
{
    var jsonPath = Path.Combine(AppContext.BaseDirectory, "PluginVersions.json");
    if (File.Exists(jsonPath))
    {
        var json = await File.ReadAllTextAsync(jsonPath);
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(json);
    }
    else
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("[]");
    }
});

// GET /mods/{modName} -> download zip
app.MapGet("/mods/{modName}", async (string modName, HttpContext context) =>
{
    var baseDir = AppContext.BaseDirectory;
    var modZipsPath = Path.Combine(baseDir, "temp", "ModZips");
    var pluginVersionsPath = Path.Combine(baseDir, "PluginVersions.json");

    // Load PluginVersions.json to find the correct FileName
    if (!File.Exists(pluginVersionsPath))
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync("PluginVersions.json not found");
        return;
    }

    var json = await File.ReadAllTextAsync(pluginVersionsPath);
    var mods = JsonSerializer.Deserialize<List<ModInfo>>(json);

    if (mods == null)
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync("No mods found");
        return;
    }

    var mod = mods.FirstOrDefault(m =>
        string.Equals(m.Name, modName, StringComparison.OrdinalIgnoreCase));

    if (mod == null)
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync("Mod not found");
        return;
    }

    // Use FileName from PluginVersions.json (this points to your .zip)
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

// GET /ConfigFiles.json -> return ConfigFiles.json
app.MapGet("/ConfigFiles.json", async context =>
{
    var jsonPath = Path.Combine(AppContext.BaseDirectory, "ConfigFiles.json");
    if (File.Exists(jsonPath))
    {
        var json = await File.ReadAllTextAsync(jsonPath);
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(json);
    }
    else
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("[]");
    }
});

// GET /configs/{configName} -> download config file
app.MapGet("/configs/{configName}", async (string configName, HttpContext context) =>
{
    var baseDir = AppContext.BaseDirectory;
    var configsPath = Path.Combine(baseDir, "temp", "ConfigFiles");
    var configFilePath = Path.Combine(baseDir, "ConfigFiles.json");

    // Load ConfigFiles.json to find the correct config file
    if (!File.Exists(configFilePath))
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync("ConfigFiles.json not found");
        return;
    }

    var json = await File.ReadAllTextAsync(configFilePath);
    var configs = JsonSerializer.Deserialize<List<ConfigInfo>>(json);
    if (configs == null)
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync("No config files found");
        return;
    }

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

app.MapGet("/spt/update", async context =>
{
    var baseDir = AppContext.BaseDirectory;
    var sptUpdatePath = Path.Combine(baseDir, "temp", "spt_update.zip");

    if (!File.Exists(sptUpdatePath))
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync("SPT update zip not found");
        return;
    }

    var fileInfo = new FileInfo(sptUpdatePath);

    context.Response.ContentType = "application/zip";
    context.Response.Headers.ContentLength = fileInfo.Length;

    await context.Response.SendFileAsync(sptUpdatePath);
});

// GET /admin/validate?secret=... -> return boolean indicating whether secret matches any enabled admin
app.MapGet("/admin/validate", async (HttpContext context) =>
{
    var secret = context.Request.Query["secret"].ToString();
    context.Response.ContentType = "application/json";

    if (string.IsNullOrEmpty(secret))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("false");
        return;
    }

    var adminsPath = Path.Combine(AppContext.BaseDirectory, "admins.json");
    if (!File.Exists(adminsPath))
    {
        await context.Response.WriteAsync("false");
        return;
    }

    var json = await File.ReadAllTextAsync(adminsPath);
    List<AdminConfig>? admins;
    try
    {
        admins = JsonSerializer.Deserialize<List<AdminConfig>>(json);
    }
    catch
    {
        await context.Response.WriteAsync("false");
        return;
    }

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

// POST /admin/headless/close?secret=... -> close headless client if secret matches an enabled admin with AllowHeadlessClose
app.MapMethods("/admin/headless/close", new[] { "GET", "POST" }, async (HttpContext context, IHubContext<ServerHub> hub) =>
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

    var adminsPath = Path.Combine(AppContext.BaseDirectory, "admins.json");
    if (!File.Exists(adminsPath))
    {
        await context.Response.WriteAsync("false");
        return;
    }

    var json = await File.ReadAllTextAsync(adminsPath);
    List<AdminConfig>? admins;
    try
    {
        admins = JsonSerializer.Deserialize<List<AdminConfig>>(json);
    }
    catch
    {
        await context.Response.WriteAsync("false");
        return;
    }

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

// GET /headless/running -> return boolean indicating whether headless client is running
app.MapGet("/headless/running", async (HttpContext context) =>
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

// GET /admin/headless/running?secret=... -> return boolean indicating whether headless client is running
app.MapGet("/admin/headless/running", async (HttpContext context) =>
{
    var secret = context.Request.Query["secret"].ToString();
    context.Response.ContentType = "application/json";

    if (string.IsNullOrEmpty(secret))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("false");
        return;
    }

    var adminsPath = Path.Combine(AppContext.BaseDirectory, "admins.json");
    if (!File.Exists(adminsPath))
    {
        await context.Response.WriteAsync("false");
        return;
    }

    var json = await File.ReadAllTextAsync(adminsPath);
    List<AdminConfig>? admins;
    try
    {
        admins = JsonSerializer.Deserialize<List<AdminConfig>>(json);
    }
    catch
    {
        await context.Response.WriteAsync("false");
        return;
    }

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

// GET /spt/version -> return SPT version string
app.MapGet("/spt/version", async context =>
{
    var now = DateTime.UtcNow;
    var cacheTimeout = TimeSpan.FromMinutes(60);
    bool isCacheValid = !string.IsNullOrEmpty(cachedSptVersion);
    var modsFolder = Path.Combine(config.SptServerFolder, "BepInEx", "plugins");
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

// Check if SPT Server is running on this machine
app.MapGet("/sptserver/running", async context =>
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
app.MapHub<ServerHub>("/hub");

// Example endpoint to notify all clients that server is restarting
app.MapPost("/notify/restart", async (IHubContext<ServerHub> hub) =>
{
    await hub.Clients.All.SendAsync("ServerRestarting", "Server will restart soon");
    return Results.Ok("Notification sent");
});

// Notify SPT Server offline on shutdown
app.MapPost("/notify/spt/offline", async (IHubContext<ServerHub> hub) =>
{
    await hub.Clients.All.SendAsync("SptServerOffline", "SPT Server is going offline");
    return Results.Ok("Notification sent");
});

// Notify SPT Server online on startup
app.MapPost("/notify/spt/online", async (IHubContext<ServerHub> hub) =>
{
    await hub.Clients.All.SendAsync("SptServerOnline", "SPT Server is online");
    return Results.Ok("Notification sent");
});

// Notify SPT Server Restart
app.MapPost("/notify/spt/restart", async (IHubContext<ServerHub> hub) =>
{
    await hub.Clients.All.SendAsync("SptServerRestarting", "SPT Server is restarting");
    return Results.Ok("Notification sent");
});

// Notify SPT Server updating
app.MapPost("/notify/spt/updating", async (IHubContext<ServerHub> hub) =>
{
    await hub.Clients.All.SendAsync("SptServerUpdating", "SPT Server is updating");
    return Results.Ok("Notification sent");
});

// Notify Headless Closed
app.MapPost("/notify/headless/offline", async (IHubContext<ServerHub> hub) =>
{
    await hub.Clients.All.SendAsync("HeadlessOffline", "Headless client is offline");
    return Results.Ok("Notification sent");
});

// Notify Headless Online
app.MapPost("/notify/headless/online", async (IHubContext<ServerHub> hub) =>
{
    await hub.Clients.All.SendAsync("HeadlessOnline", "Headless client is online");
    return Results.Ok("Notification sent");
});

// Notify Headless Restart
app.MapPost("/notify/headless/restart", async (IHubContext<ServerHub> hub) =>
{
    await hub.Clients.All.SendAsync("HeadlessRestarted", "Headless has been closed / restarting");
    return Results.Ok("Notification sent");
});

// Wrap the app startup/run to catch and display any exceptions so the console remains open
try
{
    app.Run($"http://0.0.0.0:{config.Port}");
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

public record ConfigInfo
{
    public string FileName { get; set; } = "";
    public DateTime LastModified { get; set; }
    public bool IsEnforced { get; set; } = false; // If true, launcher will get this config file from server on launch
}

public record ModInfo
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public required string FileName { get; set; }       // Name of the zip file
    public required bool IsFolderMod { get; set; }     // true if folder-based mod
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

public class ServerHub : Hub { }
