using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace FlixyPix.Core;

public sealed record FolderSyncReport(DateTimeOffset At, FolderSyncResult Result, IReadOnlyList<FolderChange> Changes)
{
    public bool Applied { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<LibraryFolder> Unreachable { get; init; } = [];
}
public sealed partial class CatalogRepository
{
    /// <summary>Size and date a folder check first saw for files whose catalog entry knows no file date (entries taken over
    /// from EazyFlixPix 4). They are kept beside the media: writing them into each entry cost a large library a quarter of an
    /// hour and a new search index.</summary>
    private void InitializeFolderBaselineSchema(SqliteTransaction transaction)
    {
        using var create = Command("CREATE TABLE IF NOT EXISTS folder_baselines(path_key TEXT PRIMARY KEY, size INTEGER NOT NULL, modified INTEGER NOT NULL) WITHOUT ROWID", transaction);
        create.ExecuteNonQuery();
    }

    private static Dictionary<string, (long Size, DateTimeOffset Modified)> FolderBaselinesUnder(string folder, SqliteConnection source)
    {
        var prefix = PathKey(folder) + System.IO.Path.DirectorySeparatorChar;
        using var command = source.CreateCommand();
        command.CommandText = "SELECT path_key,size,modified FROM folder_baselines WHERE path_key >= $prefix AND path_key < $end";
        command.Parameters.AddWithValue("$prefix", prefix); command.Parameters.AddWithValue("$end", prefix[..^1] + (char)(System.IO.Path.DirectorySeparatorChar + 1));
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, (long, DateTimeOffset)>();
        while (reader.Read()) result[reader.GetString(0)] = (reader.GetInt64(1), new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero));
        return result;
    }

    /// <summary>Notes new first-seen states and drops those whose entry has a file date of its own by now, in one transaction.</summary>
    private void SaveFolderBaselines(IReadOnlyCollection<(string Key, long Size, DateTimeOffset Modified)> noted, IReadOnlyCollection<string> stale)
    {
        if (noted.Count == 0 && stale.Count == 0) return;
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            using var save = Command("INSERT OR REPLACE INTO folder_baselines(path_key,size,modified) VALUES($key,$size,$modified)", transaction, ("$key", ""), ("$size", 0L), ("$modified", 0L));
            foreach (var (key, size, modified) in noted)
            {
                save.Parameters["$key"].Value = key; save.Parameters["$size"].Value = size; save.Parameters["$modified"].Value = modified.UtcTicks;
                save.ExecuteNonQuery();
            }
            using var drop = Command("DELETE FROM folder_baselines WHERE path_key=$key", transaction, ("$key", ""));
            foreach (var key in stale) { drop.Parameters["$key"].Value = key; drop.ExecuteNonQuery(); }
            transaction.Commit();
        }
    }

    public int RemoveMissingFolderFiles(FolderSyncPlan plan, IReadOnlyCollection<FolderChange> chosen, CancellationToken token = default)
    {
        lock (gate)
        {
            using var transaction = connection.BeginTransaction(); int removed = 0;
            foreach (var change in chosen)
            {
                token.ThrowIfCancellationRequested();
                if (change.Kind != FolderChangeKind.Missing || !plan.Changes.Contains(change)) throw new ArgumentException("Choose missing files from this check.");
                var folder = plan.Folders.Single(f => f.Id == change.FolderId);
                if (!Directory.Exists(folder.Path) || plan.Errors.Count > 0) continue;
                try { File.GetAttributes(change.Path); continue; }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                using var read = Command("SELECT document FROM media WHERE id=$id", transaction, ("$id", change.MediaId!.Value.ToString()));
                var current = read.ExecuteScalar() is string data ? JsonSerializer.Deserialize<MediaItem>(data, Json) : null;
                if (current == null) continue;
                if (current.Path == change.Path)
                {
                    if (current.Files.Count > 0) continue; // Promote a remaining version explicitly before removing the former main file.
                    DeleteMediaCore([current.Id], transaction);
                }
                else
                {
                    if (!current.Files.Any(f => f.Path == change.Path)) continue;
                    UpsertCore(current with { Files = current.Files.Where(f => f.Path != change.Path).ToList() }, transaction);
                }
                removed++;
            }
            token.ThrowIfCancellationRequested();
            transaction.Commit(); return removed;
        }
    }

    public void SaveFolderSyncReport(FolderSyncPlan plan, FolderSyncResult result, bool applied = true)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var create = Command("CREATE TABLE IF NOT EXISTS folder_sync_report(id INTEGER PRIMARY KEY, document TEXT NOT NULL)", null); create.ExecuteNonQuery();
            using var save = Command("INSERT OR REPLACE INTO folder_sync_report(id,document) VALUES(1,$report)", null,
             ("$report", JsonSerializer.Serialize(new FolderSyncReport(DateTimeOffset.UtcNow, result, plan.Changes) { Applied = applied, Errors = plan.Errors, Unreachable = plan.Unreachable }, Json)));
            save.ExecuteNonQuery();
        }
    }
    public FolderSyncReport? GetFolderSyncReport()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var exists = Command("SELECT 1 FROM sqlite_master WHERE type='table' AND name='folder_sync_report'", null);
            if (exists.ExecuteScalar() == null) return null;
            using var read = Command("SELECT document FROM folder_sync_report WHERE id=1", null);
            return read.ExecuteScalar() is string json ? JsonSerializer.Deserialize<FolderSyncReport>(json, Json) : null;
        }
    }
}
