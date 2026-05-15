namespace SPTServerManager.Models;

public class OldPluginBackupEntry
{
    public string ModName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTime ArchivedUtc { get; set; }
}

