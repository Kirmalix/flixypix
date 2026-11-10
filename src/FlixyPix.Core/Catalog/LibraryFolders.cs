using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>A folder the library keeps in step with. New files are offered for import, moved or renamed files are
/// relinked and vanished files are reported. Source files are only read, never changed.</summary>
public sealed record LibraryFolder
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Path { get; init; }
    /// <summary>Only this media kind; null takes every kind the library's type lists know.</summary>
    public MediaKind? Kind { get; init; }
    public bool IncludeSubfolders { get; init; } = true;
    /// <summary>Watch the folder while FlixyPix runs; folders without it are checked only on request.</summary>
    public bool Watch { get; init; } = true;
    public bool AutoApply { get; init; }
    /// <summary>Category given to files imported from this folder.</summary>
    public string? Category { get; init; }
    /// <summary>Files the user removed from the catalog or declined; they are not offered again.</summary>
    public List<string> IgnoredPaths { get; init; } = [];
}

public enum FolderChangeKind { Moved, Renamed, New, Missing, Modified }

/// <param name="PreviousPath">Catalog path before a move or rename.</param>
/// <param name="PreviousSize">Size the catalog knew, so the window can say what changed.</param>
/// <param name="PreviousModifiedAtUtc">File date the catalog knew, or the one a check first saw for an entry without a date.</param>
public sealed record FolderChange(FolderChangeKind Kind, string Path, MediaKind MediaKind, long? Size, Guid FolderId,
    Guid? MediaId = null, string? PreviousPath = null, DateTimeOffset? ModifiedAtUtc = null, long? PreviousSize = null, DateTimeOffset? PreviousModifiedAtUtc = null);

/// <param name="Unreachable">Folders whose root does not exist (drive offline, share disconnected); their media are not reported missing.</param>
public sealed record FolderSyncPlan(IReadOnlyList<LibraryFolder> Folders, IReadOnlyList<FolderChange> Changes,
    IReadOnlyList<LibraryFolder> Unreachable, IReadOnlyList<string> Errors)
{
    public int Count(FolderChangeKind kind) => Changes.Count(c => c.Kind == kind);
    /// <summary>Changes the user can take over; missing files stay in the catalog and are only reported.</summary>
    public IReadOnlyList<FolderChange> Actionable => Changes.Where(c => c.Kind != FolderChangeKind.Missing).ToList();
}

public sealed record FolderSyncProgress(string Phase, int Done, int? Total, string Path);
public sealed record FolderSyncResult(int Imported, int Relinked, int Ignored, IReadOnlyList<string> Skipped)
{
    public int Updated { get; init; }
    public int Removed { get; init; }
}

public sealed partial class CatalogRepository
{
    /// <summary>Unique size of an unrelated file is only trusted as a rename from this size on.</summary>
    public const long RenameMinimumBytes = 1024 * 1024;
    /// <summary>Folders FlixyPix itself writes inside the library folder.</summary>
    public static IReadOnlyList<string> LibraryWorkFolders { get; } =
        ["thumbnails", "hover-cache", "clip-cache", "image-cache", "archive-cache", "media-assets", "tag-assets", "tag-artwork-cache", "scene-generations", "backups",
         Path.GetFileName(AssetStore.DirectoryFor(LibraryCatalogs.CatalogFileName))];

    public IReadOnlyList<LibraryFolder> GetLibraryFolders() => GetPreferences().LibraryFolders;

    public IReadOnlyList<LibraryFolder> SaveLibraryFolders(IReadOnlyList<LibraryFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var normalized = folders.Select(NormalizeLibraryFolder).ToList();
        if (normalized.Select(f => PathKey(f.Path)).Distinct().Count() != normalized.Count || normalized.Select(f => f.Id).Distinct().Count() != normalized.Count)
            throw new ArgumentException(T("Each media folder may appear only once."), nameof(folders));
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            SaveDocument("preferences", Guid.Empty, ReadPreferences(transaction) with { LibraryFolders = normalized }, transaction);
            transaction.Commit();
        }
        return normalized;
    }

    /// <summary>Remembers removed or declined files so the folder check does not offer them again.</summary>
    public int IgnoreInLibraryFolders(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var preferences = ReadPreferences(transaction);
            var folders = preferences.LibraryFolders.ToList();
            var count = IgnoreCore(folders, paths);
            if (count > 0) SaveDocument("preferences", Guid.Empty, preferences with { LibraryFolders = folders }, transaction);
            transaction.Commit();
            return count;
        }
    }

    /// <summary>Content folders of a library adopted from EazyFlixPix 4.8.4 that are not yet library folders. Folders the original
    /// refreshed automatically are watched; the others are checked only on request.</summary>
    public IReadOnlyList<LibraryFolder> SuggestLibraryFoldersFromOriginal()
    {
        var existing = GetLibraryFolders().Select(f => PathKey(f.Path)).ToHashSet();
        var result = new List<LibraryFolder>();
        foreach (var folder in GetHistoricalContentFolders())
        {
            if (!System.IO.Path.IsPathFullyQualified(folder.Path) || !existing.Add(PathKey(folder.Path))) continue;
            result.Add(new LibraryFolder { Path = System.IO.Path.GetFullPath(folder.Path), Kind = folder.Kind, Category = folder.Category, Watch = folder.OriginalAlwaysRefresh == true });
        }
        return result;
    }

    /// <summary>Time between progress reports of a folder check: one report per file would flood the window, which then
    /// handles them before it draws anything.</summary>
    private static readonly TimeSpan FolderProgressInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Compares the folders with the catalog. Reads the file system only; media are not changed. For entries without a
    /// file date it notes the size and date seen first, so a later check can tell a changed file.</summary>
    public FolderSyncPlan PlanFolderSync(IReadOnlyList<LibraryFolder>? folders = null, CancellationToken cancellationToken = default, IProgress<FolderSyncProgress>? progress = null)
    {
        int scanned = 0;
        var reported = System.Diagnostics.Stopwatch.StartNew();
        void Report(string path, bool now = false)
        {
            if (progress == null || !now && reported.Elapsed < FolderProgressInterval) return;
            reported.Restart();
            progress.Report(new("Checking folders …", scanned, null, path));
        }
        var baselines = new Dictionary<string, (long Size, DateTimeOffset Modified)>();
        var preferences = GetPreferences();
        var chosen = (folders ?? preferences.LibraryFolders).Select(NormalizeLibraryFolder).ToList();
        var kindOf = MediaScanner.FolderKinds(preferences.Previews);
        var found = new Dictionary<string, (string Path, MediaKind Kind, long Size, DateTimeOffset Modified, LibraryFolder Folder)>();
        var cataloged = new Dictionary<string, (Guid Id, string Path, MediaKind Kind, long? Size, DateTimeOffset? Modified, LibraryFolder Folder)>();
        var unreachable = new List<LibraryFolder>();
        var known = new HashSet<string>();
        var unscanned = new List<string>();
        var errors = new List<string>();
        var ignored = chosen.SelectMany(f => f.IgnoredPaths).Select(PathKey).ToHashSet();
        // Work folders of the library (thumbnails, caches, stored artwork) are never media, even inside a media folder.
        // Only those are skipped: a library kept above its media, such as D:\Medien\catalog.db, still shows D:\Medien\Fotos.
        var library = System.IO.Path.GetDirectoryName(DatabasePath)!;
        var work = LibraryWorkFolders.Select(name => PathKey(System.IO.Path.Combine(library, name))).ToArray();
        bool InLibrary(string path) { var key = PathKey(path); return work.Any(w => key == w || IsInside(key, w)); }
        // The most specific folder decides kind filter and category when folders are nested.
        foreach (var folder in chosen.OrderByDescending(f => f.Path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(folder.Path)) { unreachable.Add(folder); continue; }
            var pending = new Stack<string>([folder.Path]);
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Report(entry.FullName, ++scanned == 1);
                        if (entry is DirectoryInfo child)
                        {
                            if (!folder.IncludeSubfolders || child.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                            if (child.Attributes.HasFlag(FileAttributes.Hidden) && !preferences.General.IncludeHiddenFolders) continue;
                            if (InLibrary(child.FullName)) continue;
                            pending.Push(child.FullName);
                        }
                        else if (entry is FileInfo file && kindOf(file.FullName) is { } kind && (folder.Kind is null || folder.Kind == kind) && !InLibrary(directory) &&
                                 !(kind == MediaKind.Image && preferences.General.SkipsImage(file.FullName)))
                            found.TryAdd(PathKey(file.FullName), (file.FullName, kind, file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero), folder));
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    unscanned.Add(PathKey(directory));
                    errors.Add($"{directory}: {error.Message}");
                }
            }
            // Reading the catalog part of a large folder takes a moment; the window says so instead of standing still.
            Report(folder.Path, now: true);
            // Further versions of media are cataloged with their medium and never new.
            foreach (var key in VersionKeysUnder(folder.Path)) known.Add(key);
            var (media, seen) = CatalogUnder(folder.Path);
            foreach (var (key, state) in seen) baselines.TryAdd(key, state);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in media)
            {
                var key = PathKey(item.Path);
                known.Add(key);
                if (folder.IncludeSubfolders || string.Equals(System.IO.Path.GetDirectoryName(item.Path), folder.Path, StringComparison.OrdinalIgnoreCase))
                    cataloged.TryAdd(key, (item.Id, item.Path, item.Kind, item.Size, item.Modified, folder));
            }
        }
        var added = found.Where(f => !ignored.Contains(f.Key) && !known.Contains(f.Key)).Select(f => f.Value).ToList();
        // A file outside the type lists or in a skipped hidden folder is not listed, yet still present.
        var missing = cataloged.Where(c => !found.ContainsKey(c.Key) && !unscanned.Any(u => c.Key.StartsWith(u + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)) && !File.Exists(c.Value.Path))
            .Select(c => c.Value).ToList();
        var changes = new List<FolderChange>();
        var noted = new List<(string Key, long Size, DateTimeOffset Modified)>();
        foreach (var entry in found)
        {
            if (!cataloged.TryGetValue(entry.Key, out var old)) continue;

            var stamp = entry.Value.Modified;
            var (size, previous) = (old.Size, old.Modified);
            if (previous is null)
            {
                // The state seen first counts as the cataloged one; a size that already differs is a change of its own.
                if (baselines.TryGetValue(entry.Key, out var first)) (size, previous) = (first.Size, first.Modified);
                else if (size is null || size == entry.Value.Size) { noted.Add((entry.Key, entry.Value.Size, stamp)); continue; }
            }
            // Same size and a date moved by whole hours is a clock change on a FAT drive or memory card, not new content.
            if (size is { } bytes && bytes != entry.Value.Size || previous is { } date && date != stamp && !(size == entry.Value.Size && IsClockShift(date, stamp)))
                changes.Add(new(FolderChangeKind.Modified, entry.Value.Path, entry.Value.Kind, entry.Value.Size, entry.Value.Folder.Id, old.Id, old.Path, stamp, size, previous));
        }
        // A noted state is no longer needed once the entry has a date of its own or left the catalog.
        var stale = baselines.Keys.Where(key => !cataloged.TryGetValue(key, out var item) || item.Modified != null).ToList();
        cancellationToken.ThrowIfCancellationRequested();
        SaveFolderBaselines(noted, stale);
        void Pair(FolderChangeKind kind, Func<string, long?, MediaKind, string?> key)
        {
            var targets = added.GroupBy(a => key(a.Path, a.Size, a.Kind)).Where(g => g.Key != null && g.Count() == 1).ToDictionary(g => g.Key!, g => g.Single());
            foreach (var group in missing.GroupBy(m => key(m.Path, m.Size, m.Kind)).Where(g => g.Key != null && g.Count() == 1).ToList())
            {
                if (!targets.TryGetValue(group.Key!, out var target)) continue;
                var source = group.Single();
                changes.Add(new(kind, target.Path, target.Kind, target.Size, target.Folder.Id, source.Id, source.Path, target.Modified, source.Size, source.Modified));
                added.Remove(target); missing.Remove(source);
            }
        }
        // Moved keeps name and size; a rename keeps only a size that is large and unique on both sides.
        Pair(FolderChangeKind.Moved, (path, size, kind) => size is { } bytes ? $"{kind}|{System.IO.Path.GetFileName(path).ToUpperInvariant()}|{bytes}" : null);
        Pair(FolderChangeKind.Renamed, (path, size, kind) => size is >= RenameMinimumBytes ? $"{kind}|{System.IO.Path.GetExtension(path).ToUpperInvariant()}|{size}" : null);
        changes.AddRange(added.Select(a => new FolderChange(FolderChangeKind.New, a.Path, a.Kind, a.Size, a.Folder.Id, ModifiedAtUtc: a.Modified)));
        changes.AddRange(missing.Select(m => new FolderChange(FolderChangeKind.Missing, m.Path, m.Kind, m.Size, m.Folder.Id, m.Id, PreviousSize: m.Size, PreviousModifiedAtUtc: m.Modified)));
        return new(chosen, changes.OrderBy(c => c.Kind).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList(), unreachable, errors);
    }

    /// <summary>Takes over the chosen changes in one transaction. Each change is checked again against the file system and
    /// the catalog; a change that no longer holds is skipped and reported, never forced.</summary>
    /// <param name="ignoreDeclined">New files of the plan that were not chosen are not offered again.</param>
    public FolderSyncResult ApplyFolderSync(FolderSyncPlan plan, IReadOnlyCollection<FolderChange> chosen, bool ignoreDeclined = false, CancellationToken cancellationToken = default, IProgress<FolderSyncProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(chosen);
        if (chosen.Any(c => !plan.Changes.Contains(c))) throw new ArgumentException("Only changes of this plan can be applied.", nameof(chosen));
        var folders = plan.Folders.ToDictionary(f => f.Id);
        var skipped = new List<string>();
        int imported = 0, relinked = 0, ignored = 0, updated = 0, done = 0;
        var general = GetPreferences().General;
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            bool Cataloged(string path) { using var lookup = Command("SELECT 1 FROM media WHERE path_key=$path", transaction, ("$path", PathKey(path))); if (lookup.ExecuteScalar() is not null) return true; using var version = Command("SELECT 1 FROM media_files WHERE path_key=$path", transaction, ("$path", PathKey(path))); return version.ExecuteScalar() is not null; }
            foreach (var change in chosen.Where(c => c.Kind != FolderChangeKind.Missing))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(change.Path)) { skipped.Add(T("{0}: the file no longer exists.", change.Path)); continue; }
                progress?.Report(new("Apply selected", ++done, chosen.Count, change.Path));
                try { using var stable = new FileStream(change.Path, FileMode.Open, FileAccess.Read, FileShare.Read); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { skipped.Add(change.Path + ": " + error.Message); continue; }
                var file = new FileInfo(change.Path);
                if (file.Length != change.Size || change.ModifiedAtUtc is { } expected && expected.UtcDateTime != file.LastWriteTimeUtc)
                { skipped.Add(T("{0}: the file changed since the check.", change.Path)); continue; }
                if (change.Kind == FolderChangeKind.Modified)
                {
                    using var lookup = Command("SELECT document FROM media WHERE id=$id", transaction, ("$id", change.MediaId.ToString()!));
                    var current = lookup.ExecuteScalar() is string data ? JsonSerializer.Deserialize<MediaItem>(data, Json) : null;
                    // The catalog may spell the path differently from the file system (entries taken over from EazyFlixPix 4);
                    // the check matched them by path key, so taking over does too.
                    var key = PathKey(change.Path);
                    if (current == null) { skipped.Add(T("{0}: the catalog entry changed since the check.", change.Path)); continue; }
                    if (PathKey(current.Path) != key)
                    {
                        if (!current.Files.Any(v => PathKey(v.Path) == key)) { skipped.Add(T("{0}: the catalog entry changed since the check.", change.Path)); continue; }
                        UpsertCore(current with { Files = current.Files.Select(v => PathKey(v.Path) == key ? v with { FileSize = file.Length, FileModifiedAtUtc = change.ModifiedAtUtc, Width = null, Height = null, DurationSeconds = null, Technical = null } : v).ToList() }, transaction);
                        updated++; continue;
                    }
                    UpsertCore(current with { FileSize = file.Length, FileModifiedAtUtc = change.ModifiedAtUtc,
                        Width = null, Height = null, DurationSeconds = null, Technical = null,
                        Extra = new(current.Extra) { ["folders.previewStale"] = "true" } }, transaction);
                    updated++; continue;
                }
                if (Cataloged(change.Path)) { skipped.Add(T("{0}: the file is now in the catalog.", change.Path)); continue; }
                if (change.Kind == FolderChangeKind.New)
                {
                    var category = folders.TryGetValue(change.FolderId, out var folder) ? folder.Category : null;
                    UpsertCore(new MediaItem { Path = change.Path, Title = general.ImportTitle(change.Path), Kind = change.MediaKind, FileSize = change.Size, Category = category,
                        FileModifiedAtUtc = change.ModifiedAtUtc, FileCreatedAt = MediaScanner.FileDate(new FileInfo(change.Path)) }, transaction);
                    imported++;
                    continue;
                }
                using var read = Command("SELECT document FROM media WHERE id=$id", transaction, ("$id", change.MediaId.ToString()!));
                var item = read.ExecuteScalar() is string json ? JsonSerializer.Deserialize<MediaItem>(json, Json) : null;
                if (item != null && item.Path != change.PreviousPath && item.Files.Any(v => v.Path == change.PreviousPath))
                {
                    if (File.Exists(change.PreviousPath)) { skipped.Add(T("{0}: the file is back in its old place.", change.PreviousPath)); continue; }
                    UpsertCore(item with { Files = item.Files.Select(v => v.Path == change.PreviousPath ? v with { Path = change.Path, FileSize = change.Size, FileModifiedAtUtc = change.ModifiedAtUtc } : v).ToList() }, transaction);
                    relinked++; continue;
                }
                if (item is null || !string.Equals(item.Path, change.PreviousPath, StringComparison.Ordinal)) { skipped.Add(T("{0}: the catalog entry changed since the check.", change.PreviousPath)); continue; }
                if (File.Exists(item.Path)) { skipped.Add(T("{0}: the file is back in its old place.", item.Path)); continue; }
                // A title the user never changed follows the new file name.
                var title = item.Title == System.IO.Path.GetFileNameWithoutExtension(item.Path) || item.Title == general.ImportTitle(item.Path) ? general.ImportTitle(change.Path) : item.Title;
                var extra = new Dictionary<string, string?>(item.Extra) { ["folders.previousPath"] = item.Path };
                UpsertCore(item with { Path = change.Path, Title = title, FileSize = change.Size, FileModifiedAtUtc = change.ModifiedAtUtc, Extra = extra }, transaction);
                relinked++;
            }
            if (ignoreDeclined)
            {
                var preferences = ReadPreferences(transaction);
                var current = preferences.LibraryFolders.ToList();
                ignored = IgnoreCore(current, plan.Changes.Where(c => c.Kind == FolderChangeKind.New && !chosen.Contains(c)).Select(c => c.Path));
                if (ignored > 0) SaveDocument("preferences", Guid.Empty, preferences with { LibraryFolders = current }, transaction);
            }
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        return new(imported, relinked, ignored, skipped) { Updated = updated };
    }

    private static int IgnoreCore(List<LibraryFolder> folders, IEnumerable<string> paths)
    {
        var count = 0;
        foreach (var path in paths)
        {
            var key = PathKey(path);
            var index = folders.Select((f, i) => (f, i)).Where(x => IsInside(key, PathKey(x.f.Path))).OrderByDescending(x => x.f.Path.Length).Select(x => x.i).DefaultIfEmpty(-1).First();
            if (index < 0 || folders[index].IgnoredPaths.Any(p => PathKey(p) == key)) continue;
            folders[index] = folders[index] with { IgnoredPaths = [.. folders[index].IgnoredPaths, System.IO.Path.GetFullPath(path)] };
            count++;
        }
        return count;
    }

    /// <summary>FAT keeps local time, so after a clock or time zone change the same file reads whole hours apart.</summary>
    internal static bool IsClockShift(DateTimeOffset before, DateTimeOffset now)
    {
        var apart = (now - before).Duration();
        return apart.Ticks % TimeSpan.TicksPerHour == 0 && apart.TotalHours is >= 1 and <= 14;
    }

    private static bool IsInside(string key, string folderKey) => key.StartsWith(folderKey + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>Catalog entries below a folder and the states noted for them, read through the path index on a reader of its
    /// own: with many media this takes long, and the window goes on working meanwhile.</summary>
    private (List<(Guid Id, string Path, MediaKind Kind, long? Size, DateTimeOffset? Modified)> Media, Dictionary<string, (long Size, DateTimeOffset Modified)> Baselines) CatalogUnder(string folder)
    {
        lock (gate) ThrowIfDisposed();
        using var aside = OpenAside();
        return (MediaUnder(folder, aside), FolderBaselinesUnder(folder, aside));
    }

    private static List<(Guid Id, string Path, MediaKind Kind, long? Size, DateTimeOffset? Modified)> MediaUnder(string folder, SqliteConnection source)
    {
        var prefix = PathKey(folder) + System.IO.Path.DirectorySeparatorChar;
        using var command = source.CreateCommand();
        command.CommandText = "SELECT document FROM media WHERE (path_key >= $prefix AND path_key < $end) OR id IN (SELECT media_id FROM media_files WHERE path_key >= $prefix AND path_key < $end)";
        command.Parameters.AddWithValue("$prefix", prefix); command.Parameters.AddWithValue("$end", prefix[..^1] + (char)(System.IO.Path.DirectorySeparatorChar + 1));
        using var reader = command.ExecuteReader();
        var result = new List<(Guid, string, MediaKind, long?, DateTimeOffset?)>();
        while (reader.Read())
        {
            var item = JsonSerializer.Deserialize<MediaItem>(reader.GetString(0), Json)!;
            foreach (var file in MediaVersions.All(item))
                if (PathKey(file.Path).StartsWith(prefix, StringComparison.Ordinal)) result.Add((item.Id, file.Path, item.Kind, file.FileSize, file.FileModifiedAtUtc));
        }
        return result;
    }

    private static LibraryFolder NormalizeLibraryFolder(LibraryFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (folder.Id == Guid.Empty || string.IsNullOrWhiteSpace(folder.Path) || !System.IO.Path.IsPathFullyQualified(folder.Path) || folder.Kind is { } kind && !Enum.IsDefined(kind) || folder.IgnoredPaths is null)
            throw new ArgumentException(T("A media folder needs a complete path and a known media type."), nameof(folder));
        var path = System.IO.Path.GetFullPath(folder.Path);
        var root = System.IO.Path.GetPathRoot(path);
        if (!string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) path = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        return folder with { Path = path, Category = string.IsNullOrWhiteSpace(folder.Category) ? null : folder.Category.Trim(), IgnoredPaths = folder.IgnoredPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() };
    }
}
