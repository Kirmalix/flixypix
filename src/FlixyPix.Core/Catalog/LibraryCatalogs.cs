using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>A library this computer knows: its folder (catalog, thumbnails and caches), an optional name of its own
/// and when it was last opened. The list belongs to the computer, not to a catalog.</summary>
public sealed record KnownLibrary(string Directory, string? Name = null, DateTimeOffset? LastOpened = null);

/// <summary>What the library list shows about a folder without opening, unlocking or upgrading its catalog.</summary>
public sealed record LibrarySummary(string Directory, bool Exists, bool HasCatalog, bool Encrypted, long? MediaCount, long Bytes, string? Problem);

/// <summary>Several libraries, one open at a time (original: database manager <c>frmDatabase</c> with one SDF file
/// per database). Pure list operations for the computer's library list, a read-only look into a library folder,
/// and moving a folder that is not open.</summary>
public static class LibraryCatalogs
{
    public const string CatalogFileName = "catalog.db";

    public static string CatalogPath(string directory) => Path.Combine(directory, CatalogFileName);

    public static bool SameDirectory(string first, string second) =>
        string.Equals(Normalize(first), Normalize(second), StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    /// <summary>The name shown for a library: its own name, otherwise the folder name. The default folder
    /// "Library" of a first start is shown as "Meine Bibliothek".</summary>
    public static string DisplayName(KnownLibrary library)
    {
        if (!string.IsNullOrWhiteSpace(library.Name)) return library.Name.Trim();
        var folder = Path.GetFileName(Normalize(library.Directory));
        return folder.Equals("Library", StringComparison.OrdinalIgnoreCase) ? T("My library") : folder;
    }

    /// <summary>Adds the folder or updates when it was opened; the same folder never appears twice.</summary>
    public static IReadOnlyList<KnownLibrary> Remember(IReadOnlyList<KnownLibrary> libraries, string directory, DateTimeOffset openedAt)
    {
        var existing = libraries.FirstOrDefault(l => SameDirectory(l.Directory, directory));
        var entry = (existing ?? new KnownLibrary(Normalize(directory))) with { LastOpened = openedAt };
        return [.. libraries.Where(l => !SameDirectory(l.Directory, directory)), entry];
    }

    /// <summary>Gives a library a name of its own; an empty name goes back to the folder name.</summary>
    public static IReadOnlyList<KnownLibrary> Rename(IReadOnlyList<KnownLibrary> libraries, string directory, string? name) =>
        [.. libraries.Select(l => SameDirectory(l.Directory, directory) ? l with { Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim() } : l)];

    /// <summary>Removes a library from the list only; its folder stays as it is.</summary>
    public static IReadOnlyList<KnownLibrary> Forget(IReadOnlyList<KnownLibrary> libraries, string directory) =>
        [.. libraries.Where(l => !SameDirectory(l.Directory, directory))];

    /// <summary>Follows a moved folder; name and last opening stay.</summary>
    public static IReadOnlyList<KnownLibrary> Relocate(IReadOnlyList<KnownLibrary> libraries, string from, string to) =>
        [.. libraries.Select(l => SameDirectory(l.Directory, from) ? l with { Directory = Normalize(to) } : l)];

    /// <summary>The open library first, then the others by last opening, never opened ones by name.</summary>
    public static IReadOnlyList<KnownLibrary> Ordered(IReadOnlyList<KnownLibrary> libraries, string? openDirectory) =>
        [.. libraries.OrderByDescending(l => openDirectory != null && SameDirectory(l.Directory, openDirectory))
            .ThenByDescending(l => l.LastOpened ?? DateTimeOffset.MinValue)
            .ThenBy(DisplayName, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>The folder for a new library <paramref name="name"/> inside <paramref name="parent"/>. It must not
    /// exist yet, or be empty.</summary>
    public static string NewLibraryDirectory(string parent, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new ArgumentException(T("Please enter a name."));
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || trimmed is "." or "..")
            throw new ArgumentException(T("The name must not contain any of these characters: {0}", "\\ / : * ? \" < > |"));
        var directory = Normalize(Path.Combine(parent, trimmed));
        if (File.Exists(directory) || Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException(T("The folder “{0}” is already in use.", directory));
        return directory;
    }

    /// <summary>Counts media and bytes of a library folder. The catalog is opened read-only and only when it is not
    /// encrypted; nothing is unlocked, upgraded or written.</summary>
    public static LibrarySummary Inspect(string directory, CancellationToken token = default)
    {
        var folder = Normalize(directory);
        if (!Directory.Exists(folder)) return new(folder, false, false, false, null, 0, T("The folder cannot be reached."));
        long bytes = 0;
        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                token.ThrowIfCancellationRequested();
                bytes += file.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var catalog = CatalogPath(folder);
        if (!File.Exists(catalog)) return new(folder, true, false, false, 0, bytes, null);
        // Only look for the key file: CatalogEncryption.IsEncrypted may finish an interrupted change, which writes.
        if (File.Exists(CatalogEncryption.KeyFilePath(catalog))) return new(folder, true, true, true, null, bytes, null);
        try
        {
            using var connection = new SqliteConnection(CatalogEncryption.ConnectionString(catalog, null, SqliteOpenMode.ReadOnly).ToString());
            connection.Open();
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            if (Convert.ToInt64(version.ExecuteScalar()) > CatalogRepository.SchemaVersion)
                return new(folder, true, true, false, null, bytes, T("The library comes from a newer version of FlixyPix."));
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT CASE WHEN EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='media') THEN (SELECT count(*) FROM media) ELSE 0 END";
            return new(folder, true, true, false, Convert.ToInt64(count.ExecuteScalar()), bytes, null);
        }
        catch (SqliteException ex) { return new(folder, true, true, false, null, bytes, T("The catalog cannot be read: {0}", ex.Message)); }
    }

    /// <summary>Moves a library folder that is not open. On the same drive the folder is renamed; otherwise every file
    /// is copied and checked by size, and the old folder is removed only after the copy is complete. A cancelled or
    /// failed copy removes the partial target and leaves the source untouched.</summary>
    public static async Task<string> MoveAsync(string source, string destination, IProgress<double>? progress = null, CancellationToken token = default)
    {
        var from = Normalize(source); var to = Normalize(destination);
        if (!Directory.Exists(from)) throw new DirectoryNotFoundException(T("The folder cannot be reached."));
        if (Directory.Exists(to) || File.Exists(to)) throw new IOException(T("The folder “{0}” is already in use.", to));
        if (to.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || SameDirectory(from, to))
            throw new IOException(T("A library cannot be moved into itself."));
        FileAccessPolicy.Current.EnsureWritable(from); FileAccessPolicy.Current.EnsureWritable(to);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(from, to);
            progress?.Report(1);
            return to;
        }
        await CopyAsync(from, to, progress, token);
        Directory.Delete(from, true);
        return to;
    }

    /// <summary>Copies a library folder file by file into a new folder and checks every size; a cancelled or failed
    /// copy removes the partial target. The source is only read.</summary>
    public static async Task CopyAsync(string source, string destination, IProgress<double>? progress = null, CancellationToken token = default)
    {
        var from = Normalize(source); var to = Normalize(destination);
        if (Directory.Exists(to) || File.Exists(to)) throw new IOException(T("The folder “{0}” is already in use.", to));
        FileAccessPolicy.Current.EnsureWritable(to);
        var files = new DirectoryInfo(from).GetFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint });
        var total = Math.Max(1, files.Sum(f => f.Length)); long done = 0;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
            Directory.CreateDirectory(to);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var target = Path.Combine(to, Path.GetRelativePath(from, file.FullName));
                await using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true))
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true))
                    await input.CopyToAsync(output, token);
                if (new FileInfo(target).Length != file.Length) throw new IOException(T("The copy of “{0}” is incomplete.", file.Name));
                File.SetLastWriteTimeUtc(target, file.LastWriteTimeUtc);
                done += file.Length; progress?.Report((double)done / total);
            }
        }
        catch
        {
            try { Directory.Delete(to, true); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}
