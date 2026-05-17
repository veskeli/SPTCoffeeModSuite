using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Windows;
using Microsoft.Data.Sqlite;
using SPTCoffee.Contracts.Models;
using SPTServerManager.Models;
using MessageBox = System.Windows.MessageBox;

namespace SPTServerManager;

public partial class MainWindow
{
    private const string ChangeHistoryScopeClient = "db_storage";
    private const string ChangeHistoryScopeServerDatabase = "db";
    private const string ChangeHistoryScopeServerLocal = "local";
    private const string ChangeHistoryScopeServerBoth = "db_local";
    private const string TempTrashFolderName = "Trash";

    private static string GetTempTrashFolder(string rootFolder) => Path.Combine(GetTempFolder(rootFolder), TempTrashFolderName);

    private static void MigrateChangeHistorySchema(SqliteConnection connection)
    {
        var newColumns = new[]
        {
            ("scope_kind", "TEXT NOT NULL DEFAULT ''"),
            ("action_kind", "TEXT NOT NULL DEFAULT ''"),
            ("old_version", "TEXT NOT NULL DEFAULT ''"),
            ("new_version", "TEXT NOT NULL DEFAULT ''"),
            ("old_revision", "INTEGER NOT NULL DEFAULT 0"),
            ("new_revision", "INTEGER NOT NULL DEFAULT 0"),
            ("old_file_name", "TEXT NOT NULL DEFAULT ''"),
            ("new_file_name", "TEXT NOT NULL DEFAULT ''"),
            ("before_json", "TEXT NOT NULL DEFAULT ''"),
            ("after_json", "TEXT NOT NULL DEFAULT ''"),
            ("snapshot_path", "TEXT NOT NULL DEFAULT ''"),
            ("reverted_utc", "TEXT"),
            ("notes", "TEXT NOT NULL DEFAULT ''")
        };

        foreach (var (col, def) in newColumns)
        {
            try
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE change_history ADD COLUMN {col} {def};";
                alter.ExecuteNonQuery();
            }
            catch
            {
                // Column already exists — safe to ignore.
            }
        }
    }

    private void RefreshChangeHistoryView()
    {
        try
        {
            ChangeHistoryListView.ItemsSource = LoadChangeHistoryEntriesFromDatabase();
            UpdateSettingsTempUsageDisplay();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to refresh change history: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private List<ChangeHistoryEntry> LoadChangeHistoryEntriesFromDatabase()
    {
        if (string.IsNullOrWhiteSpace(_databasePath))
            return new List<ChangeHistoryEntry>();

        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        EnsureSptCoffeeSchema(connection);
        MigrateChangeHistorySchema(connection);

        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, item_name, mod_type, scope_kind, action_kind,
       old_version, new_version, old_revision, new_revision,
       old_file_name, new_file_name, before_json, after_json,
       snapshot_path, created_utc, reverted_utc, notes
FROM change_history
ORDER BY id DESC;";

        var result = new List<ChangeHistoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var entry = new ChangeHistoryEntry
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                ModType = reader.GetString(2),
                ScopeKind = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                ActionKind = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                OldVersion = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                NewVersion = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                OldRevision = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                NewRevision = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                OldFileName = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                NewFileName = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                BeforeJson = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
                AfterJson = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
                SnapshotPath = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
                CreatedUtc = reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
                RevertedUtc = reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
                Notes = reader.IsDBNull(16) ? string.Empty : reader.GetString(16)
            };

            result.Add(entry);
        }

        DecorateChangeHistoryEntries(result);

        return result;
    }

    private static string BuildHistoryIdentityKey(ChangeHistoryEntry entry)
    {
        return $"{entry.ModType.ToLowerInvariant()}::{entry.Name.ToLowerInvariant()}";
    }

    private void DecorateChangeHistoryEntries(List<ChangeHistoryEntry> entries)
    {
        var maxUnrevertedByIdentity = entries
            .Where(x => string.IsNullOrWhiteSpace(x.RevertedUtc))
            .GroupBy(BuildHistoryIdentityKey)
            .ToDictionary(g => g.Key, g => g.Max(x => x.Id), StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var identity = BuildHistoryIdentityKey(entry);
            var hasNewerUnreverted = maxUnrevertedByIdentity.TryGetValue(identity, out var maxId) && maxId > entry.Id;
            DecorateChangeHistoryEntry(entry, hasNewerUnreverted);
        }
    }

    private void DecorateChangeHistoryEntry(ChangeHistoryEntry entry, bool hasNewerActiveChange = false)
    {
        entry.HasNewerActiveChange = hasNewerActiveChange;
        entry.TempArtifactSizeBytes = string.IsNullOrWhiteSpace(entry.SnapshotPath) ? 0 : GetPathSize(entry.SnapshotPath);
        entry.CanRevert = CanRevertHistoryEntry(entry, out var revertStatus);
        entry.RevertStatus = revertStatus;
    }

    private static string SerializeHistoryPayload<T>(T? value)
    {
        return value == null ? string.Empty : JsonSerializer.Serialize(value);
    }

    private static T? DeserializeHistoryPayload<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return default;
        }
    }

    private void InsertChangeHistoryEntry(SqliteConnection connection, ChangeHistoryEntry entry)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO change_history(
    item_name, mod_type, scope_kind, action_kind,
    old_version, new_version, old_revision, new_revision,
    old_file_name, new_file_name,
    before_json, after_json, snapshot_path, created_utc, reverted_utc, notes)
VALUES(
    $itemName, $modType, $scopeKind, $actionKind,
    $oldVersion, $newVersion, $oldRevision, $newRevision,
    $oldFileName, $newFileName,
    $beforeJson, $afterJson, $snapshotPath, $createdUtc, $revertedUtc, $notes);";
        command.Parameters.AddWithValue("$itemName", entry.Name);
        command.Parameters.AddWithValue("$modType", entry.ModType);
        command.Parameters.AddWithValue("$scopeKind", entry.ScopeKind ?? string.Empty);
        command.Parameters.AddWithValue("$actionKind", entry.ActionKind ?? string.Empty);
        command.Parameters.AddWithValue("$oldVersion", entry.OldVersion ?? string.Empty);
        command.Parameters.AddWithValue("$newVersion", entry.NewVersion ?? string.Empty);
        command.Parameters.AddWithValue("$oldRevision", Math.Max(0, entry.OldRevision));
        command.Parameters.AddWithValue("$newRevision", Math.Max(0, entry.NewRevision));
        command.Parameters.AddWithValue("$oldFileName", entry.OldFileName ?? string.Empty);
        command.Parameters.AddWithValue("$newFileName", entry.NewFileName ?? string.Empty);
        command.Parameters.AddWithValue("$beforeJson", entry.BeforeJson ?? string.Empty);
        command.Parameters.AddWithValue("$afterJson", entry.AfterJson ?? string.Empty);
        command.Parameters.AddWithValue("$snapshotPath", entry.SnapshotPath ?? string.Empty);
        command.Parameters.AddWithValue("$createdUtc", string.IsNullOrWhiteSpace(entry.CreatedUtc) ? DateTime.UtcNow.ToString("O") : entry.CreatedUtc);
        command.Parameters.AddWithValue("$revertedUtc", string.IsNullOrWhiteSpace(entry.RevertedUtc) ? DBNull.Value : entry.RevertedUtc);
        command.Parameters.AddWithValue("$notes", entry.Notes ?? string.Empty);
        command.ExecuteNonQuery();
    }

    private void MarkChangeHistoryEntryReverted(SqliteConnection connection, long historyId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE change_history SET reverted_utc = $revertedUtc WHERE id = $id;";
        command.Parameters.AddWithValue("$id", historyId);
        command.Parameters.AddWithValue("$revertedUtc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private ChangeHistoryEntry? ApplyPendingClientChange(SqliteConnection connection, string modName, ModInfo mod, IReadOnlyDictionary<string, ModInfo> existingDbMods)
    {
        var state = (mod.PendingChangeState ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(state))
            return null;

        existingDbMods.TryGetValue(modName, out var rawExisting);
        var existingDb = rawExisting == null ? null : CloneModInfo(rawExisting);

        if (string.Equals(state, "add", StringComparison.OrdinalIgnoreCase))
        {
            var appliedAdd = CloneModInfo(mod);
            appliedAdd.Name = modName;
            appliedAdd.Version = NormalizeVersionForStorage(appliedAdd.Version, "0.0.0");
            appliedAdd.Revision = appliedAdd.NewRevision.HasValue && appliedAdd.NewRevision.Value > 0 ? appliedAdd.NewRevision.Value : 1;
            appliedAdd.NewVersion = null;
            appliedAdd.NewRevision = null;
            EnsureClientPendingFileInStorage(appliedAdd);
            UpsertModInDatabase(connection, appliedAdd);

            mod.Version = appliedAdd.Version;
            mod.Revision = appliedAdd.Revision;
            mod.NewVersion = null;
            mod.NewRevision = null;

            return CreateClientHistoryEntry(modName, "add", null, appliedAdd, null);
        }

        if (string.Equals(state, "delete", StringComparison.OrdinalIgnoreCase))
        {
            var snapshotPath = existingDb == null ? null : ArchiveExistingClientMod(existingDb);
            DeleteModFromDatabase(connection, modName);
            return existingDb == null ? null : CreateClientHistoryEntry(modName, "delete", existingDb, null, snapshotPath);
        }

        if (string.Equals(state, "update", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(mod.NewVersion))
        {
            var snapshotPath = existingDb == null ? null : ArchiveExistingClientMod(existingDb);
            EnsureClientPendingFileInStorage(mod, overwriteExisting: true);

            var oldVersion = NormalizeVersionForStorage(mod.Version, "0.0.0");
            var targetVersion = NormalizeVersionForStorage(mod.NewVersion, oldVersion);
            var existingRevision = existingDb == null
                ? Math.Max(0, mod.Revision)
                : Math.Max(0, existingDb.Revision);
            var effectiveNewRevision = mod.NewRevision.HasValue && mod.NewRevision.Value > 0 ? mod.NewRevision : (int?)null;
            var targetRevision = effectiveNewRevision ?? (string.Equals(oldVersion, targetVersion, StringComparison.OrdinalIgnoreCase)
                ? Math.Max(1, existingRevision + 1)
                : 1);

            var appliedUpdate = CloneModInfo(mod);
            appliedUpdate.Name = modName;
            appliedUpdate.Version = targetVersion;
            appliedUpdate.Revision = targetRevision;
            appliedUpdate.NewVersion = null;
            appliedUpdate.NewRevision = null;
            UpsertModInDatabase(connection, appliedUpdate);

            mod.Version = appliedUpdate.Version;
            mod.Revision = appliedUpdate.Revision;
            mod.NewVersion = null;
            mod.NewRevision = null;

            return CreateClientHistoryEntry(modName, "update", existingDb, appliedUpdate, snapshotPath);
        }

        return null;
    }

    private ChangeHistoryEntry? ApplyPendingServerChange(SqliteConnection connection, string modName, ServerModInfo mod)
    {
        var state = (mod.PendingChangeState ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(state))
            return null;

        var actionKind = GetPendingStateKind(state);
        if (string.IsNullOrWhiteSpace(actionKind))
            return null;

        var affectsDb = AffectsServerDatabase(state);
        var affectsLocal = AffectsServerLocal(state);
        var existingDb = TryLoadServerModFromDatabase(connection, modName);
        var localFolderPath = GetLocalServerModFolderPath(modName);
        var localExists = Directory.Exists(localFolderPath);
        var localVersion = localExists ? ExtractServerModVersion(localFolderPath) : string.Empty;
        string? snapshotPath = null;

        if (affectsDb && !string.Equals(actionKind, "add", StringComparison.OrdinalIgnoreCase) && existingDb != null && !localExists)
        {
            snapshotPath = ArchiveExistingStoredServerZip(existingDb.Name, existingDb.Version, existingDb.FileName);
        }

        switch (state)
        {
            case ServerStateAddBoth:
            case ServerStateUpdateBoth:
                UpsertServerModFromPending(connection, mod, modName);
                snapshotPath ??= UpsertLocalServerModFromPending(mod, modName);
                var updatedBothDb = TryLoadServerModFromDatabase(connection, modName);
                return CreateServerHistoryEntry(
                    modName,
                    ChangeHistoryScopeServerBoth,
                    actionKind,
                    existingDb,
                    updatedBothDb,
                    string.IsNullOrWhiteSpace(existingDb?.Version) ? localVersion : existingDb!.Version,
                    updatedBothDb?.Version ?? NormalizeVersionForStorage(mod.NewVersion, mod.Version),
                    snapshotPath,
                    localExists);

            case ServerStateDeleteBoth:
                DeleteServerModFromDatabase(connection, modName);
                snapshotPath ??= RemoveLocalServerMod(modName);
                return CreateServerHistoryEntry(
                    modName,
                    ChangeHistoryScopeServerBoth,
                    "delete",
                    existingDb,
                    null,
                    string.IsNullOrWhiteSpace(existingDb?.Version) ? localVersion : existingDb!.Version,
                    null,
                    snapshotPath,
                    localExists);

            case ServerStateAddDb:
            case ServerStateUpdateDb:
            case "add":
            case "update":
                UpsertServerModFromPending(connection, mod, modName);
                var updatedDb = TryLoadServerModFromDatabase(connection, modName);
                return CreateServerHistoryEntry(
                    modName,
                    ChangeHistoryScopeServerDatabase,
                    actionKind,
                    existingDb,
                    updatedDb,
                    existingDb?.Version,
                    updatedDb?.Version ?? NormalizeVersionForStorage(mod.NewVersion, mod.Version),
                    snapshotPath,
                    localExists);

            case ServerStateDeleteDb:
            case "delete":
                DeleteServerModFromDatabase(connection, modName);
                return CreateServerHistoryEntry(
                    modName,
                    ChangeHistoryScopeServerDatabase,
                    "delete",
                    existingDb,
                    null,
                    existingDb?.Version,
                    null,
                    snapshotPath,
                    localExists);

            case ServerStateAddLocal:
            case ServerStateUpdateLocal:
                snapshotPath ??= UpsertLocalServerModFromPending(mod, modName);
                return CreateServerHistoryEntry(
                    modName,
                    ChangeHistoryScopeServerLocal,
                    actionKind,
                    existingDb,
                    existingDb,
                    localVersion,
                    NormalizeVersionForStorage(mod.NewVersion, mod.Version),
                    snapshotPath,
                    localExists);

            case ServerStateDeleteLocal:
                snapshotPath ??= RemoveLocalServerMod(modName);
                return CreateServerHistoryEntry(
                    modName,
                    ChangeHistoryScopeServerLocal,
                    "delete",
                    existingDb,
                    existingDb,
                    localVersion,
                    null,
                    snapshotPath,
                    localExists);

            default:
                return null;
        }
    }

    private ChangeHistoryEntry CreateClientHistoryEntry(string modName, string actionKind, ModInfo? before, ModInfo? after, string? snapshotPath)
    {
        return new ChangeHistoryEntry
        {
            Name = modName,
            ModType = "Client",
            ScopeKind = ChangeHistoryScopeClient,
            ActionKind = actionKind,
            OldVersion = before?.Version ?? string.Empty,
            NewVersion = after?.Version ?? string.Empty,
            OldRevision = before?.Revision ?? 0,
            NewRevision = after?.Revision ?? 0,
            OldFileName = before?.FileName ?? string.Empty,
            NewFileName = after?.FileName ?? string.Empty,
            BeforeJson = SerializeHistoryPayload(before),
            AfterJson = SerializeHistoryPayload(after),
            SnapshotPath = snapshotPath ?? string.Empty,
            CreatedUtc = DateTime.UtcNow.ToString("O")
        };
    }

    private ChangeHistoryEntry CreateServerHistoryEntry(
        string modName,
        string scopeKind,
        string actionKind,
        ServerModInfo? before,
        ServerModInfo? after,
        string? oldVersion,
        string? newVersion,
        string? snapshotPath,
        bool localWasPresentBefore)
    {
        return new ChangeHistoryEntry
        {
            Name = modName,
            ModType = "Server",
            ScopeKind = scopeKind,
            ActionKind = actionKind,
            OldVersion = oldVersion ?? string.Empty,
            NewVersion = newVersion ?? string.Empty,
            OldRevision = before?.Revision ?? 0,
            NewRevision = after?.Revision ?? 0,
            OldFileName = before?.FileName ?? string.Empty,
            NewFileName = after?.FileName ?? string.Empty,
            BeforeJson = SerializeHistoryPayload(before),
            AfterJson = SerializeHistoryPayload(after),
            SnapshotPath = snapshotPath ?? string.Empty,
            CreatedUtc = DateTime.UtcNow.ToString("O")
            ,Notes = localWasPresentBefore ? "local_before=1" : "local_before=0"
        };
    }

    private static bool LocalWasPresentBefore(ChangeHistoryEntry entry)
    {
        return entry.Notes.Contains("local_before=1", StringComparison.OrdinalIgnoreCase);
    }

    private bool CanRevertHistoryEntry(ChangeHistoryEntry entry, out string status)
    {
        if (!string.IsNullOrWhiteSpace(entry.RevertedUtc))
        {
            status = "Already reverted";
            return false;
        }

        if (HistoryEntryRequiresSnapshot(entry))
        {
            if (string.IsNullOrWhiteSpace(entry.SnapshotPath) || !File.Exists(entry.SnapshotPath))
            {
                status = "Revert not possible (temp snapshot cleaned up)";
                return false;
            }
        }

        if (entry.HasNewerActiveChange)
        {
            status = "Warning: newer unreverted change exists";
            return false;
        }

        status = "Ready";
        return true;
    }

    private static bool HistoryEntryRequiresSnapshot(ChangeHistoryEntry entry)
    {
        if (string.Equals(entry.ModType, "Client", StringComparison.OrdinalIgnoreCase))
        {
            return !string.Equals(entry.ActionKind, "add", StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(entry.ActionKind, "update", StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(entry.ActionKind, "delete", StringComparison.OrdinalIgnoreCase)
               && entry.AffectsLocal;
    }

    private static string GetPendingStateKind(string state)
    {
        if (state.StartsWith("add", StringComparison.OrdinalIgnoreCase))
            return "add";
        if (state.StartsWith("update", StringComparison.OrdinalIgnoreCase))
            return "update";
        if (state.StartsWith("delete", StringComparison.OrdinalIgnoreCase))
            return "delete";
        return string.Empty;
    }

    private static bool AffectsServerDatabase(string state)
    {
        return string.Equals(state, ServerStateAddBoth, StringComparison.OrdinalIgnoreCase)
               || string.Equals(state, ServerStateUpdateBoth, StringComparison.OrdinalIgnoreCase)
               || string.Equals(state, ServerStateDeleteBoth, StringComparison.OrdinalIgnoreCase)
               || string.Equals(state, ServerStateAddDb, StringComparison.OrdinalIgnoreCase)
               || string.Equals(state, ServerStateUpdateDb, StringComparison.OrdinalIgnoreCase)
               || string.Equals(state, ServerStateDeleteDb, StringComparison.OrdinalIgnoreCase)
               || string.Equals(state, "add", StringComparison.OrdinalIgnoreCase)
               || string.Equals(state, "update", StringComparison.OrdinalIgnoreCase)
               || string.Equals(state, "delete", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AffectsServerLocal(string state)
    {
        return string.Equals(state, ServerStateAddBoth, StringComparison.OrdinalIgnoreCase)
               || string.Equals(state, ServerStateUpdateBoth, StringComparison.OrdinalIgnoreCase)
               || string.Equals(state, ServerStateDeleteBoth, StringComparison.OrdinalIgnoreCase)
               || string.Equals(state, ServerStateAddLocal, StringComparison.OrdinalIgnoreCase)
               || string.Equals(state, ServerStateUpdateLocal, StringComparison.OrdinalIgnoreCase)
               || string.Equals(state, ServerStateDeleteLocal, StringComparison.OrdinalIgnoreCase);
    }

    private ModInfo? TryLoadClientModFromDatabase(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT name, version, file_name, is_folder_mod, allow_on_headless, is_optional, optional_default_state, COALESCE(revision, 0)
FROM plugins WHERE name = $name COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("$name", name);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new ModInfo
        {
            Name = reader.GetString(0),
            Version = reader.GetString(1),
            FileName = reader.GetString(2),
            IsFolderMod = !reader.IsDBNull(3) && reader.GetInt32(3) == 1,
            AllowOnHeadless = !reader.IsDBNull(4) && reader.GetInt32(4) == 1,
            IsOptional = !reader.IsDBNull(5) && reader.GetInt32(5) == 1,
            OptionalDefaultState = !reader.IsDBNull(6) && reader.GetInt32(6) == 1,
            Revision = reader.IsDBNull(7) ? 0 : reader.GetInt32(7)
        };
    }

    private ServerModInfo? TryLoadServerModFromDatabase(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT name, version, file_name, COALESCE(revision, 0)
FROM server_plugins WHERE name = $name COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("$name", name);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new ServerModInfo
        {
            Name = reader.GetString(0),
            Version = reader.GetString(1),
            FileName = reader.GetString(2),
            Revision = reader.IsDBNull(3) ? 0 : reader.GetInt32(3)
        };
    }

    private string GetLocalServerModFolderPath(string modName)
    {
        return Path.Combine(Config.SptServerFolder, "SPT", "user", "mods", modName);
    }

    private string? ArchiveExistingStoredServerZip(string modName, string version, string fileName)
    {
        if (_exeFolder == null)
            throw new InvalidOperationException("Executable folder not determined.");

        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var sourcePath = Path.Combine(GetServerModZipFolder(_exeFolder), fileName);
        if (!File.Exists(sourcePath))
            return null;

        var archiveFolder = Path.Combine(
            GetOldServerModsFolder(_exeFolder),
            SanitizePathSegment(modName),
            SanitizePathSegment(NormalizeVersionForStorage(version, "unknown")));
        Directory.CreateDirectory(archiveFolder);

        var destinationPath = GetUniqueDestinationPath(archiveFolder, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, destinationPath, true);
        return destinationPath;
    }

    private string MoveFileToTempTrash(string sourcePath, string category)
    {
        if (_exeFolder == null)
            throw new InvalidOperationException("Executable folder not determined.");

        var trashFolder = Path.Combine(GetTempTrashFolder(_exeFolder), category);
        Directory.CreateDirectory(trashFolder);

        var destinationPath = GetUniqueDestinationPath(trashFolder, Path.GetFileName(sourcePath));
        File.Move(sourcePath, destinationPath);
        return destinationPath;
    }

    private string MoveDirectoryToTempTrash(string sourcePath, string category)
    {
        if (_exeFolder == null)
            throw new InvalidOperationException("Executable folder not determined.");

        var trashFolder = Path.Combine(GetTempTrashFolder(_exeFolder), category);
        Directory.CreateDirectory(trashFolder);

        var destinationPath = GetUniqueDestinationPath(trashFolder, Path.GetFileName(sourcePath));
        Directory.Move(sourcePath, destinationPath);
        return destinationPath;
    }

    private static string GetUniqueDestinationPath(string destinationFolder, string fileOrFolderName)
    {
        var destinationPath = Path.Combine(destinationFolder, fileOrFolderName);
        if (!File.Exists(destinationPath) && !Directory.Exists(destinationPath))
            return destinationPath;

        var stem = Path.GetFileNameWithoutExtension(fileOrFolderName);
        var extension = Path.GetExtension(fileOrFolderName);
        var suffix = 1;
        do
        {
            destinationPath = Path.Combine(destinationFolder, $"{stem}_{DateTime.UtcNow:yyyyMMddHHmmss}_{suffix}{extension}");
            suffix++;
        } while (File.Exists(destinationPath) || Directory.Exists(destinationPath));

        return destinationPath;
    }

    private static void CleanupEmptyDirectoryChain(string? startDirectory, string stopAtDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
            return;

        var current = new DirectoryInfo(startDirectory);
        var stopAt = Path.GetFullPath(stopAtDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (current != null)
        {
            var fullPath = current.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullPath, stopAt, StringComparison.OrdinalIgnoreCase))
                break;

            if (current.Exists && !current.EnumerateFileSystemInfos().Any())
            {
                var parent = current.Parent;
                current.Delete(false);
                current = parent;
                continue;
            }

            break;
        }
    }

    private void MoveStoredClientZipToTrash(string? fileName)
    {
        if (_exeFolder == null || string.IsNullOrWhiteSpace(fileName))
            return;

        var fullPath = Path.Combine(GetPluginZipFolder(_exeFolder), fileName);
        if (File.Exists(fullPath))
            MoveFileToTempTrash(fullPath, "ClientStorageRemovals");
    }

    private void MoveStoredServerZipToTrash(string? fileName)
    {
        if (_exeFolder == null || string.IsNullOrWhiteSpace(fileName))
            return;

        var fullPath = Path.Combine(GetServerModZipFolder(_exeFolder), fileName);
        if (File.Exists(fullPath))
            MoveFileToTempTrash(fullPath, "ServerStorageRemovals");
    }

    private void RestoreClientZipFromSnapshot(string snapshotPath, string fileName)
    {
        if (_exeFolder == null)
            throw new InvalidOperationException("Executable folder not determined.");

        var storageFolder = GetPluginZipFolder(_exeFolder);
        Directory.CreateDirectory(storageFolder);

        var destinationPath = Path.Combine(storageFolder, fileName);
        if (File.Exists(destinationPath))
            MoveFileToTempTrash(destinationPath, "ClientStorageRemovals");

        File.Copy(snapshotPath, destinationPath, true);
    }

    private void RestoreServerZipFromSnapshot(string snapshotPath, string fileName)
    {
        if (_exeFolder == null)
            throw new InvalidOperationException("Executable folder not determined.");

        var storageFolder = GetServerModZipFolder(_exeFolder);
        Directory.CreateDirectory(storageFolder);

        var destinationPath = Path.Combine(storageFolder, fileName);
        if (File.Exists(destinationPath))
            MoveFileToTempTrash(destinationPath, "ServerStorageRemovals");

        File.Copy(snapshotPath, destinationPath, true);
    }

    private void RestoreLocalServerModFromSnapshot(string snapshotPath, string modName)
    {
        if (_exeFolder == null)
            throw new InvalidOperationException("Executable folder not determined.");

        var targetFolder = GetLocalServerModFolderPath(modName);
        var targetParent = Path.GetDirectoryName(targetFolder);
        if (!string.IsNullOrWhiteSpace(targetParent))
            Directory.CreateDirectory(targetParent);

        if (Directory.Exists(targetFolder))
            MoveDirectoryToTempTrash(targetFolder, "ServerLocalRevertReplaced");

        var tempExtractRoot = Path.Combine(GetPendingServerFilesFolder(_exeFolder), "_history_restore");
        Directory.CreateDirectory(tempExtractRoot);
        var tempTargetFolder = Path.Combine(tempExtractRoot, modName + "_" + Guid.NewGuid().ToString("N"));

        try
        {
            ZipFile.ExtractToDirectory(snapshotPath, tempTargetFolder);
            var extractedSourceFolder = ResolveServerModSourceDirectory(tempTargetFolder, modName);
            Directory.Move(extractedSourceFolder, targetFolder);
        }
        finally
        {
            if (Directory.Exists(tempTargetFolder))
                Directory.Delete(tempTargetFolder, true);
        }
    }

    private bool TryRevertHistoryEntry(SqliteConnection connection, ChangeHistoryEntry entry, out string error)
    {
        error = string.Empty;

        if (entry.IsServerMod)
            return TryRevertServerHistoryEntry(connection, entry, out error);

        return TryRevertClientHistoryEntry(connection, entry, out error);
    }

    private bool TryRevertClientHistoryEntry(SqliteConnection connection, ChangeHistoryEntry entry, out string error)
    {
        error = string.Empty;
        var before = DeserializeHistoryPayload<ModInfo>(entry.BeforeJson);
        var after = DeserializeHistoryPayload<ModInfo>(entry.AfterJson);
        var current = TryLoadClientModFromDatabase(connection, entry.Name);

        switch ((entry.ActionKind ?? string.Empty).ToLowerInvariant())
        {
            case "add":
                if (current == null && string.IsNullOrWhiteSpace(after?.FileName))
                {
                    error = "Current added client mod could not be found.";
                    return false;
                }

                DeleteModFromDatabase(connection, entry.Name);
                MoveStoredClientZipToTrash(current?.FileName ?? after?.FileName ?? entry.NewFileName);
                return true;

            case "update":
            case "delete":
                if (before == null)
                {
                    error = "Previous client state is missing from history.";
                    return false;
                }

                if (HistoryEntryRequiresSnapshot(entry) && (string.IsNullOrWhiteSpace(entry.SnapshotPath) || !File.Exists(entry.SnapshotPath)))
                {
                    error = "Revert is not possible because the required temp snapshot was cleaned up.";
                    return false;
                }

                MoveStoredClientZipToTrash(current?.FileName ?? after?.FileName ?? entry.NewFileName);
                RestoreClientZipFromSnapshot(entry.SnapshotPath, string.IsNullOrWhiteSpace(before.FileName) ? (entry.Name + ".zip") : before.FileName);
                UpsertModInDatabase(connection, before);
                return true;

            default:
                error = $"Unsupported client history action: {entry.ActionKind}";
                return false;
        }
    }

    private bool TryRevertServerHistoryEntry(SqliteConnection connection, ChangeHistoryEntry entry, out string error)
    {
        error = string.Empty;
        var before = DeserializeHistoryPayload<ServerModInfo>(entry.BeforeJson);
        var after = DeserializeHistoryPayload<ServerModInfo>(entry.AfterJson);

        switch ((entry.ActionKind ?? string.Empty).ToLowerInvariant())
        {
            case "add":
                if (entry.AffectsDatabase)
                {
                    DeleteServerModFromDatabase(connection, entry.Name);
                    MoveStoredServerZipToTrash(after?.FileName ?? entry.NewFileName);
                }

                if (entry.AffectsLocal)
                {
                    var localFolder = GetLocalServerModFolderPath(entry.Name);
                    if (Directory.Exists(localFolder))
                        MoveDirectoryToTempTrash(localFolder, "ServerLocalRemovals");
                }

                return true;

            case "update":
                if (HistoryEntryRequiresSnapshot(entry) && (string.IsNullOrWhiteSpace(entry.SnapshotPath) || !File.Exists(entry.SnapshotPath)))
                {
                    error = "Revert is not possible because the required temp snapshot was cleaned up.";
                    return false;
                }

                if (entry.AffectsDatabase)
                {
                    if (before == null)
                    {
                        error = "Previous server database state is missing from history.";
                        return false;
                    }

                    MoveStoredServerZipToTrash(after?.FileName ?? entry.NewFileName);
                    if (!string.IsNullOrWhiteSpace(before.FileName) && !string.IsNullOrWhiteSpace(entry.SnapshotPath))
                        RestoreServerZipFromSnapshot(entry.SnapshotPath, before.FileName);
                    UpsertServerModInDatabase(connection, before);
                }

                if (entry.AffectsLocal)
                {
                    if (LocalWasPresentBefore(entry))
                    {
                        RestoreLocalServerModFromSnapshot(entry.SnapshotPath, entry.Name);
                    }
                    else
                    {
                        var localFolder = GetLocalServerModFolderPath(entry.Name);
                        if (Directory.Exists(localFolder))
                            MoveDirectoryToTempTrash(localFolder, "ServerLocalRemovals");
                    }
                }

                return true;

            case "delete":
                if (entry.AffectsDatabase && before != null)
                {
                    if (!string.IsNullOrWhiteSpace(before.FileName)
                        && !string.IsNullOrWhiteSpace(entry.SnapshotPath)
                        && File.Exists(entry.SnapshotPath)
                        && !File.Exists(Path.Combine(GetServerModZipFolder(_exeFolder!), before.FileName)))
                    {
                        RestoreServerZipFromSnapshot(entry.SnapshotPath, before.FileName);
                    }

                    UpsertServerModInDatabase(connection, before);
                }
                else if (entry.AffectsDatabase && before == null)
                {
                    error = "Previous server database state is missing from history.";
                    return false;
                }

                if (entry.AffectsLocal)
                {
                    if (!LocalWasPresentBefore(entry))
                    {
                        var localFolder = GetLocalServerModFolderPath(entry.Name);
                        if (Directory.Exists(localFolder))
                            MoveDirectoryToTempTrash(localFolder, "ServerLocalRemovals");
                        return true;
                    }

                    if (string.IsNullOrWhiteSpace(entry.SnapshotPath) || !File.Exists(entry.SnapshotPath))
                    {
                        error = "Revert is not possible because the required temp snapshot was cleaned up.";
                        return false;
                    }

                    RestoreLocalServerModFromSnapshot(entry.SnapshotPath, entry.Name);
                }

                return true;

            default:
                error = $"Unsupported server history action: {entry.ActionKind}";
                return false;
        }
    }

    private void RefreshChangeHistory_Click(object sender, RoutedEventArgs e)
    {
        RefreshChangeHistoryView();
    }

    private void RevertSelectedHistoryChange_Click(object sender, RoutedEventArgs e)
    {
        var selectedEntries = ChangeHistoryListView.SelectedItems.Cast<ChangeHistoryEntry>().ToList();
        if (selectedEntries.Count == 0)
        {
            MessageBox.Show("Please select one or more applied history entries to revert.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"Revert {selectedEntries.Count} selected history entr{(selectedEntries.Count == 1 ? "y" : "ies")}?", "Confirm Revert",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var entriesWithNewerChanges = selectedEntries
            .Where(x => x.HasNewerActiveChange && string.IsNullOrWhiteSpace(x.RevertedUtc))
            .OrderByDescending(x => x.Id)
            .ToList();
        var forceOlderRevert = false;
        var skipOlderRevert = false;
        if (entriesWithNewerChanges.Count > 0)
        {
            var previewLines = string.Join("\n", entriesWithNewerChanges.Take(5).Select(x => $"- {x.ModType}: {x.Name} ({x.ActionLabel} #{x.Id})"));
            if (entriesWithNewerChanges.Count > 5)
                previewLines += $"\n...and {entriesWithNewerChanges.Count - 5} more.";

            var choice = MessageBox.Show(
                "Some selected entries are older than newer unreverted changes for the same mod.\n" +
                "Reverting older entries first can produce unexpected state.\n\n" +
                "Yes = force revert all selected entries anyway\n" +
                "No = skip only these older entries\n" +
                "Cancel = abort\n\n" +
                previewLines,
                "Newer Change Warning",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (choice == MessageBoxResult.Cancel)
                return;

            forceOlderRevert = choice == MessageBoxResult.Yes;
            skipOlderRevert = choice == MessageBoxResult.No;
        }

        if (string.IsNullOrWhiteSpace(_databasePath))
        {
            MessageBox.Show("Database path not configured.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();
            EnsureSptCoffeeSchema(connection);
            MigratePluginsSchema(connection);
            MigratePendingChangesSchema(connection);
            MigrateChangeHistorySchema(connection);

            var reverted = 0;
            var failures = new List<string>();
            foreach (var entry in selectedEntries.OrderByDescending(x => x.Id))
            {
                var decoratedHasNewer = entry.HasNewerActiveChange;
                DecorateChangeHistoryEntry(entry, decoratedHasNewer);

                if (skipOlderRevert && entry.HasNewerActiveChange)
                {
                    failures.Add($"{entry.Name}: skipped because a newer unreverted change exists.");
                    continue;
                }

                if (!entry.CanRevert)
                {
                    if (forceOlderRevert && entry.HasNewerActiveChange)
                    {
                        // User explicitly allowed reverting older entries.
                    }
                    else
                    {
                    failures.Add($"{entry.Name}: {entry.RevertStatus}");
                    continue;
                    }
                }

                var pendingExists = entry.IsServerMod
                    ? _pendingServerChanges.ContainsKey(entry.Name)
                    : _pendingChanges.ContainsKey(entry.Name);
                if (pendingExists)
                {
                    failures.Add($"{entry.Name}: A pending change already exists. Revert the pending change first.");
                    continue;
                }

                if (!TryRevertHistoryEntry(connection, entry, out var error))
                {
                    failures.Add($"{entry.Name}: {error}");
                    continue;
                }

                MarkChangeHistoryEntryReverted(connection, entry.Id);
                reverted++;
            }

            RefreshModListView();
            RefreshServerModsListView();
            RefreshPendingChanges_Internal();
            RefreshChangeHistoryView();

            if (failures.Count > 0)
            {
                var details = string.Join("\n", failures.Take(5));
                if (failures.Count > 5)
                    details += $"\n...and {failures.Count - 5} more.";

                MessageBox.Show($"Reverted {reverted} history entr{(reverted == 1 ? "y" : "ies")}. Failed: {failures.Count}.\n\n{details}",
                    "Completed with errors", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show($"Reverted {reverted} history entr{(reverted == 1 ? "y" : "ies")}.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to revert history entries: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CleanupRevertTempArtifacts_Click(object sender, RoutedEventArgs e)
    {
        if (_exeFolder == null)
        {
            MessageBox.Show("Executable folder not determined.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var cleanupTargets = new[]
        {
            GetOldPluginsFolder(_exeFolder),
            GetOldServerModsFolder(_exeFolder),
            GetTempTrashFolder(_exeFolder)
        };

        var reclaimedBytes = cleanupTargets.Sum(GetPathSize);
        if (reclaimedBytes <= 0)
        {
            MessageBox.Show("No revert temp files were found to clean up.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(
                $"Clean up {FormatByteSize(reclaimedBytes)} of revert temp files?\n\nApplied history entries that depend on these temp snapshots will show 'revert not possible' afterwards.",
                "Confirm Cleanup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var target in cleanupTargets)
        {
            if (Directory.Exists(target))
                Directory.Delete(target, true);

            Directory.CreateDirectory(target);
        }

        RefreshChangeHistoryView();
        UpdateSettingsTempUsageDisplay();
        MessageBox.Show($"Cleaned up {FormatByteSize(reclaimedBytes)} of revert temp files. History has been refreshed.",
            "Cleanup Complete", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static long GetPathSize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return 0;

        if (File.Exists(path))
            return new FileInfo(path).Length;

        if (!Directory.Exists(path))
            return 0;

        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Select(file =>
            {
                try
                {
                    return new FileInfo(file).Length;
                }
                catch
                {
                    return 0L;
                }
            })
            .Sum();
    }

    private static string FormatByteSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = Math.Max(0, bytes);
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024d;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{size:0} {units[unitIndex]}" : $"{size:0.##} {units[unitIndex]}";
    }
}

public class ChangeHistoryEntry
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ModType { get; set; } = string.Empty;
    public string ScopeKind { get; set; } = string.Empty;
    public string ActionKind { get; set; } = string.Empty;
    public string OldVersion { get; set; } = string.Empty;
    public string NewVersion { get; set; } = string.Empty;
    public int OldRevision { get; set; }
    public int NewRevision { get; set; }
    public string OldFileName { get; set; } = string.Empty;
    public string NewFileName { get; set; } = string.Empty;
    public string BeforeJson { get; set; } = string.Empty;
    public string AfterJson { get; set; } = string.Empty;
    public string SnapshotPath { get; set; } = string.Empty;
    public string CreatedUtc { get; set; } = string.Empty;
    public string RevertedUtc { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public bool CanRevert { get; set; }
    public string RevertStatus { get; set; } = string.Empty;
    public bool HasNewerActiveChange { get; set; }
    public long TempArtifactSizeBytes { get; set; }
    public bool IsServerMod => string.Equals(ModType, "Server", StringComparison.OrdinalIgnoreCase);
    public bool AffectsDatabase => string.Equals(ScopeKind, "db", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(ScopeKind, "db_local", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(ScopeKind, "db_storage", StringComparison.OrdinalIgnoreCase);
    public bool AffectsLocal => string.Equals(ScopeKind, "local", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(ScopeKind, "db_local", StringComparison.OrdinalIgnoreCase);

    public string ScopeLabel => ScopeKind.ToLowerInvariant() switch
    {
        "db_storage" => "DB + Storage",
        "db_local" => "DB + Local",
        "db" => "Database",
        "local" => "Local",
        _ => string.IsNullOrWhiteSpace(ScopeKind) ? "-" : ScopeKind
    };

    public string ActionLabel => ActionKind.ToLowerInvariant() switch
    {
        "add" => "Add",
        "update" => "Update",
        "delete" => "Delete",
        _ => string.IsNullOrWhiteSpace(ActionKind) ? "-" : ActionKind
    };

    public string OldVersionDisplay => FormatVersionAndRevision(OldVersion, OldRevision);
    public string NewVersionDisplay => FormatVersionAndRevision(NewVersion, NewRevision);
    public string TempArtifactSizeDisplay => TempArtifactSizeBytes <= 0 ? "-" : FormatByteSizeStatic(TempArtifactSizeBytes);
    public string CreatedUtcDisplay => FormatUtc(CreatedUtc);
    public string RevertedUtcDisplay => string.IsNullOrWhiteSpace(RevertedUtc) ? "-" : FormatUtc(RevertedUtc);
    public string FileChangeDisplay => $"{FormatFileName(OldFileName)} -> {FormatFileName(NewFileName)}";
    public string TempGroupLabel => $"{ModType}: {Name} [{ResolveGroupVersion()}]";
    public string DetailsTooltip =>
        $"Name: {Name}\n" +
        $"Type: {ModType}\n" +
        $"Scope: {ScopeLabel}\n" +
        $"Action: {ActionLabel}\n" +
        $"From: {OldVersionDisplay}\n" +
        $"To: {NewVersionDisplay}\n" +
        $"Files: {FileChangeDisplay}\n" +
        $"Snapshot: {(string.IsNullOrWhiteSpace(SnapshotPath) ? "-" : SnapshotPath)}\n" +
        $"Notes: {(string.IsNullOrWhiteSpace(Notes) ? "-" : Notes)}";

    private static string FormatVersionAndRevision(string? version, int revision)
    {
        var normalizedVersion = string.IsNullOrWhiteSpace(version) ? "-" : version;
        return revision > 0 ? $"{normalizedVersion} (rev {revision})" : normalizedVersion;
    }

    private static string FormatUtc(string value)
    {
        return DateTime.TryParse(value, out var parsed) ? parsed.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss") : value;
    }

    private static string FormatFileName(string? fileName)
    {
        return string.IsNullOrWhiteSpace(fileName) ? "-" : fileName;
    }

    private string ResolveGroupVersion()
    {
        if (!string.IsNullOrWhiteSpace(OldVersion))
            return OldVersion;
        if (!string.IsNullOrWhiteSpace(NewVersion))
            return NewVersion;
        return "-";
    }

    private static string FormatByteSizeStatic(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = Math.Max(0, bytes);
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024d;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{size:0} {units[unitIndex]}" : $"{size:0.##} {units[unitIndex]}";
    }
}

