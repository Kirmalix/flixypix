using System.Text.Json;
namespace FlixyPix.Core;

public sealed record FolderSyncReport(DateTimeOffset At, FolderSyncResult Result, IReadOnlyList<FolderChange> Changes)
{
 public bool Applied { get; init; }
 public IReadOnlyList<string> Errors { get; init; } = [];
 public IReadOnlyList<LibraryFolder> Unreachable { get; init; } = [];
}
public sealed partial class CatalogRepository
{
 public void InitializeFolderReferences(IReadOnlyList<LibraryFolder> folders, CancellationToken token = default)
 {
  foreach (var item in Search())
  {
   token.ThrowIfCancellationRequested();
   MediaFile Reference(MediaFile old)
   {
    if (old.FileModifiedAtUtc != null || !folders.Any(f => IsInside(PathKey(old.Path), PathKey(f.Path)) && (f.IncludeSubfolders || string.Equals(Path.GetDirectoryName(old.Path), f.Path, StringComparison.OrdinalIgnoreCase)))) return old;
    try {
     var file = new FileInfo(old.Path);
     return file.Exists && (old.FileSize == null || old.FileSize == file.Length) ? old with { FileSize = file.Length, FileModifiedAtUtc = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) } : old;
    } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return old; }
   }
   var main = Reference(MediaVersions.Main(item)); var versions = item.Files.Select(Reference).ToList();
   if (main.FileModifiedAtUtc != item.FileModifiedAtUtc || !versions.SequenceEqual(item.Files))
    lock(gate) {
     if (GetMedia(item.Id) is not { } current || current.Path != item.Path || current.FileModifiedAtUtc != item.FileModifiedAtUtc || !current.Files.SequenceEqual(item.Files)) continue;
     Upsert(current with { FileSize = main.FileSize, FileModifiedAtUtc = main.FileModifiedAtUtc, Files = versions });
    }
  }
 }
 public int RemoveMissingFolderFiles(FolderSyncPlan plan, IReadOnlyCollection<FolderChange> chosen, CancellationToken token = default)
 {
  lock(gate)
  {
   using var transaction = connection.BeginTransaction(); int removed = 0;
   foreach (var change in chosen)
   {
    token.ThrowIfCancellationRequested();
    if (change.Kind != FolderChangeKind.Missing || !plan.Changes.Contains(change)) throw new ArgumentException("Choose missing files from this check.");
    var folder = plan.Folders.Single(f => f.Id == change.FolderId);
    if (!Directory.Exists(folder.Path) || plan.Errors.Count > 0) continue;
    try { File.GetAttributes(change.Path); continue; }
    catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
    using var read = Command("SELECT document FROM media WHERE id=$id", transaction, ("$id", change.MediaId!.Value.ToString()));
    var current = read.ExecuteScalar() is string data ? JsonSerializer.Deserialize<MediaItem>(data, Json) : null;
    if (current == null) continue;
    if (current.Path == change.Path) {
     if (current.Files.Count > 0) continue; // Promote a remaining version explicitly before removing the former main file.
     DeleteMediaCore([current.Id], transaction);
    } else {
     if (!current.Files.Any(f => f.Path == change.Path)) continue;
     UpsertCore(current with { Files = current.Files.Where(f => f.Path != change.Path).ToList() }, transaction);
    }
    removed++;
   }
   token.ThrowIfCancellationRequested();
   transaction.Commit(); cachedMedia = null; return removed;
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
