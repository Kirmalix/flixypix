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
    /// <summary>Category given to files imported from this folder.</summary>
    public string? Category { get; init; }
    /// <summary>Files the user removed from the catalog or declined; they are not offered again.</summary>
    public List<string> IgnoredPaths { get; init; } = [];
}

public enum FolderChangeKind { Moved, Renamed, New, Missing }

/// <param name="PreviousPath">Catalog path before a move or rename.</param>
public sealed record FolderChange(FolderChangeKind Kind, string Path, MediaKind MediaKind, long? Size, Guid FolderId,
    Guid? MediaId = null, string? PreviousPath = null);

/// <param name="Unreachable">Folders whose root does not exist (drive offline, share disconnected); their media are not reported missing.</param>
public sealed record FolderSyncPlan(IReadOnlyList<LibraryFolder> Folders, IReadOnlyList<FolderChange> Changes,
    IReadOnlyList<LibraryFolder> Unreachable, IReadOnlyList<string> Errors)
{
    public int Count(FolderChangeKind kind) => Changes.Count(c => c.Kind == kind);
    /// <summary>Changes the user can take over; missing files stay in the catalog and are only reported.</summary>
    public IReadOnlyList<FolderChange> Actionable => Changes.Where(c => c.Kind != FolderChangeKind.Missing).ToList();
}

public sealed record FolderSyncResult(int Imported, int Relinked, int Ignored, IReadOnlyList<string> Skipped);

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

    /// <summary>Compares the folders with the catalog. Reads the file system only; nothing is changed.</summary>
    public FolderSyncPlan PlanFolderSync(IReadOnlyList<LibraryFolder>? folders = null, CancellationToken cancellationToken = default)
    {
        var preferences = GetPreferences();
        var chosen = (folders ?? preferences.LibraryFolders).Select(NormalizeLibraryFolder).ToList();
        var kindOf = MediaScanner.FolderKinds(preferences.Previews);
        var found = new Dictionary<string, (string Path, MediaKind Kind, long Size, LibraryFolder Folder)>();
        var cataloged = new Dictionary<string, (Guid Id, string Path, MediaKind Kind, long? Size, LibraryFolder Folder)>();
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
                        if (entry is DirectoryInfo child)
                        {
                            if (!folder.IncludeSubfolders || child.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                            if (child.Attributes.HasFlag(FileAttributes.Hidden) && !preferences.General.IncludeHiddenFolders) continue;
                            if (InLibrary(child.FullName)) continue;
                            pending.Push(child.FullName);
                        }
                        else if (entry is FileInfo file && kindOf(file.FullName) is { } kind && (folder.Kind is null || folder.Kind == kind) && !InLibrary(directory) &&
                                 !(kind == MediaKind.Image && preferences.General.SkipsImage(file.FullName)))
                            found.TryAdd(PathKey(file.FullName), (file.FullName, kind, file.Length, folder));
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    unscanned.Add(PathKey(directory));
                    errors.Add($"{directory}: {error.Message}");
                }
            }
            // Further versions of media are cataloged with their medium and never new.
            foreach (var key in VersionKeysUnder(folder.Path)) known.Add(key);
            foreach (var item in MediaUnder(folder.Path))
            {
                var key = PathKey(item.Path);
                known.Add(key);
                if (folder.IncludeSubfolders || string.Equals(System.IO.Path.GetDirectoryName(item.Path), folder.Path, StringComparison.OrdinalIgnoreCase))
                    cataloged.TryAdd(key, (item.Id, item.Path, item.Kind, item.Size, folder));
            }
        }
        var added = found.Where(f => !ignored.Contains(f.Key) && !known.Contains(f.Key)).Select(f => f.Value).ToList();
        // A file outside the type lists or in a skipped hidden folder is not listed, yet still present.
        var missing = cataloged.Where(c => !found.ContainsKey(c.Key) && !unscanned.Any(u => c.Key.StartsWith(u + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)) && !File.Exists(c.Value.Path))
            .Select(c => c.Value).ToList();
        var changes = new List<FolderChange>();
        void Pair(FolderChangeKind kind, Func<string, long?, MediaKind, string?> key)
        {
            var targets = added.GroupBy(a => key(a.Path, a.Size, a.Kind)).Where(g => g.Key != null && g.Count() == 1).ToDictionary(g => g.Key!, g => g.Single());
            foreach (var group in missing.GroupBy(m => key(m.Path, m.Size, m.Kind)).Where(g => g.Key != null && g.Count() == 1).ToList())
            {
                if (!targets.TryGetValue(group.Key!, out var target)) continue;
                var source = group.Single();
                changes.Add(new(kind, target.Path, target.Kind, target.Size, target.Folder.Id, source.Id, source.Path));
                added.Remove(target); missing.Remove(source);
            }
        }
        // Moved keeps name and size; a rename keeps only a size that is large and unique on both sides.
        Pair(FolderChangeKind.Moved, (path, size, kind) => size is { } bytes ? $"{kind}|{System.IO.Path.GetFileName(path).ToUpperInvariant()}|{bytes}" : null);
        Pair(FolderChangeKind.Renamed, (path, size, kind) => size is >= RenameMinimumBytes ? $"{kind}|{System.IO.Path.GetExtension(path).ToUpperInvariant()}|{size}" : null);
        changes.AddRange(added.Select(a => new FolderChange(FolderChangeKind.New, a.Path, a.Kind, a.Size, a.Folder.Id)));
        changes.AddRange(missing.Select(m => new FolderChange(FolderChangeKind.Missing, m.Path, m.Kind, m.Size, m.Folder.Id, m.Id)));
        return new(chosen, changes.OrderBy(c => c.Kind).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList(), unreachable, errors);
    }

    /// <summary>Takes over the chosen changes in one transaction. Each change is checked again against the file system and
    /// the catalog; a change that no longer holds is skipped and reported, never forced.</summary>
    /// <param name="ignoreDeclined">New files of the plan that were not chosen are not offered again.</param>
    public FolderSyncResult ApplyFolderSync(FolderSyncPlan plan, IReadOnlyCollection<FolderChange> chosen, bool ignoreDeclined = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(chosen);
        if (chosen.Any(c => !plan.Changes.Contains(c))) throw new ArgumentException("Only changes of this plan can be applied.", nameof(chosen));
        var folders = plan.Folders.ToDictionary(f => f.Id);
        var skipped = new List<string>();
        int imported = 0, relinked = 0, ignored = 0;
        var general = GetPreferences().General;
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            bool Cataloged(string path) { using var lookup = Command("SELECT 1 FROM media WHERE path_key=$path", transaction, ("$path", PathKey(path))); return lookup.ExecuteScalar() is not null; }
            foreach (var change in chosen.Where(c => c.Kind != FolderChangeKind.Missing))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(change.Path)) { skipped.Add(T("{0}: the file no longer exists.", change.Path)); continue; }
                if (Cataloged(change.Path)) { skipped.Add(T("{0}: the file is now in the catalog.", change.Path)); continue; }
                if (change.Kind == FolderChangeKind.New)
                {
                    var category = folders.TryGetValue(change.FolderId, out var folder) ? folder.Category : null;
                    UpsertCore(new MediaItem { Path = change.Path, Title = general.ImportTitle(change.Path), Kind = change.MediaKind, FileSize = change.Size, Category = category,
                        FileCreatedAt = MediaScanner.FileDate(new FileInfo(change.Path)) }, transaction);
                    imported++;
                    continue;
                }
                using var read = Command("SELECT document FROM media WHERE id=$id", transaction, ("$id", change.MediaId.ToString()!));
                var item = read.ExecuteScalar() is string json ? JsonSerializer.Deserialize<MediaItem>(json, Json) : null;
                if (item is null || !string.Equals(item.Path, change.PreviousPath, StringComparison.Ordinal)) { skipped.Add(T("{0}: the catalog entry changed since the check.", change.PreviousPath)); continue; }
                if (File.Exists(item.Path)) { skipped.Add(T("{0}: the file is back in its old place.", item.Path)); continue; }
                // A title the user never changed follows the new file name.
                var title = item.Title == System.IO.Path.GetFileNameWithoutExtension(item.Path) || item.Title == general.ImportTitle(item.Path) ? general.ImportTitle(change.Path) : item.Title;
                var extra = new Dictionary<string, string?>(item.Extra) { ["folders.previousPath"] = item.Path };
                UpsertCore(item with { Path = change.Path, Title = title, FileSize = change.Size, Extra = extra }, transaction);
                relinked++;
            }
            if (ignoreDeclined)
            {
                var preferences = ReadPreferences(transaction);
                var current = preferences.LibraryFolders.ToList();
                ignored = IgnoreCore(current, plan.Changes.Where(c => c.Kind == FolderChangeKind.New && !chosen.Contains(c)).Select(c => c.Path));
                if (ignored > 0) SaveDocument("preferences", Guid.Empty, preferences with { LibraryFolders = current }, transaction);
            }
            transaction.Commit();
        }
        return new(imported, relinked, ignored, skipped);
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

    private static bool IsInside(string key, string folderKey) => key.StartsWith(folderKey + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>Catalog entries below a folder, read through the path index without loading whole documents.</summary>
    private List<(Guid Id, string Path, MediaKind Kind, long? Size)> MediaUnder(string folder)
    {
        var prefix = PathKey(folder) + System.IO.Path.DirectorySeparatorChar;
        // ']' follows '\' in the code table, so the range covers exactly the keys below the folder.
        var end = prefix[..^1] + (char)(System.IO.Path.DirectorySeparatorChar + 1);
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = Command("SELECT id,json_extract(document,'$.path'),json_extract(document,'$.kind'),json_extract(document,'$.fileSize') FROM media WHERE path_key>=$start AND path_key<$end",
                null, ("$start", prefix), ("$end", end));
            using var reader = command.ExecuteReader();
            var result = new List<(Guid, string, MediaKind, long?)>();
            while (reader.Read())
                result.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1), Enum.Parse<MediaKind>(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetInt64(3)));
            return result;
        }
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
