namespace SPTServerManager.Models;

public class FileChangePreviewItem
{
    public bool IsIncluded { get; set; } = true;
    public string FileType { get; set; } = "Other";
    public string FileName { get; set; } = string.Empty;
    public string Location { get; set; } = ".";
    public bool ExistsInOld { get; set; }
    public string ExistsInOldText => ExistsInOld ? "Yes" : "No";
    public string SourceRelativePath { get; set; } = string.Empty;
}

