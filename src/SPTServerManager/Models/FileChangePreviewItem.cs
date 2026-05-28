namespace SPTServerManager.Models;

public class FileChangePreviewItem
{
    public const string ChoiceKeepIncoming = "Keep New";
    public const string ChoiceKeepExisting = "Keep Old";
    public const string ChoiceKeepNone = "Skip";

    public bool IsIncluded { get; set; } = true;
    public string FileType { get; set; } = "Other";
    public string FileName { get; set; } = string.Empty;
    public string DisplayName => IsBundleGroup && BundleItemCount > 0
        ? $"{FileName} ({BundleItemCount} {(BundleItemCount == 1 ? "item" : "items")})"
        : FileName;
    public string FileVersion { get; set; } = string.Empty;
    public string Location { get; set; } = ".";
    public bool ExistsInOld { get; set; }
    public string SourceKind { get; set; } = "New";
    public string SelectionChoice { get; set; } = ChoiceKeepIncoming;
    public string TargetRelativePath { get; set; } = string.Empty;
    public string SourceRelativePath { get; set; } = string.Empty;
    public string? OldSourcePath { get; set; }
    public bool IsBundleGroup { get; set; }
    public int BundleItemCount { get; set; }
    public bool IsFromOldSource => string.Equals(SourceKind, "Old", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(SourceKind, "Both", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> SelectionOptions => SourceKind.ToLowerInvariant() switch
    {
        "new" => new[] { ChoiceKeepIncoming, ChoiceKeepNone },
        "old" => new[] { ChoiceKeepExisting, ChoiceKeepNone },
        "both" => new[] { ChoiceKeepIncoming, ChoiceKeepExisting, ChoiceKeepNone },
        _ => new[] { ChoiceKeepIncoming, ChoiceKeepNone }
    };

    public bool ShouldKeepIncoming => string.Equals(SelectionChoice, ChoiceKeepIncoming, StringComparison.OrdinalIgnoreCase);
    public bool ShouldKeepExisting => string.Equals(SelectionChoice, ChoiceKeepExisting, StringComparison.OrdinalIgnoreCase);
    public bool ShouldKeepAny => ShouldKeepIncoming || ShouldKeepExisting;
}

