using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>What the catalog check looks at, in the order it reports.</summary>
public enum CatalogFindingKind
{
    /// <summary>SQLite's own integrity check; only a backup can repair it.</summary>
    Integrity,
    /// <summary>Scenes, bookmarks, graphics or query rows of media that no longer exist.</summary>
    OrphanRows,
    /// <summary>Media entries whose stored document cannot be read.</summary>
    UnreadableEntries,
    /// <summary>The query table (sorting, filters, tags) differs from the media entries.</summary>
    QueryTable,
    /// <summary>The quick-search index differs from the query table.</summary>
    SearchIndex,
    /// <summary>The stored path key of an entry does not match its path.</summary>
    PathKeys,
    /// <summary>Several entries for the same file, written differently (Unicode normalization).</summary>
    SamePath,
    /// <summary>Media files that are gone while their drive is connected.</summary>
    MissingFiles,
    /// <summary>Media files on a drive that is not connected; not a problem.</summary>
    OfflineFiles,
    /// <summary>Thumbnails, graphics and cache files in the library folder that nothing uses any more.</summary>
    OrphanFiles,
    /// <summary>Videos without a thumbnail although the file is there.</summary>
    MissingThumbnails,
    /// <summary>Scene images, artwork, tag images or binary values of the original whose file beside the catalog is
    /// missing or damaged; only a backup brings them back.</summary>
    MissingPictures,
}

/// <summary>One kind of finding. <paramref name="Examples"/> are raw paths or SQLite messages, at most
/// <see cref="CatalogRepository.CheckExampleLimit"/>; <paramref name="Count"/> is the full number.</summary>
public sealed record CatalogFinding(CatalogFindingKind Kind, int Count, IReadOnlyList<string> Examples)
{
    /// <summary>Whether the check repairs it itself; the others need the user (backup, path mapping, preparing media).</summary>
    public bool Repairable => Kind is CatalogFindingKind.OrphanRows or CatalogFindingKind.QueryTable or CatalogFindingKind.SearchIndex
        or CatalogFindingKind.PathKeys or CatalogFindingKind.SamePath or CatalogFindingKind.MissingFiles or CatalogFindingKind.OrphanFiles;
    /// <summary>Offline drives are expected; everything else counts as a problem.</summary>
    public bool IsProblem => Kind != CatalogFindingKind.OfflineFiles && Count > 0;
}

public sealed record CatalogCheckProgress(CatalogFindingKind Step, int Done, int Total);

/// <summary>Result of <see cref="CatalogRepository.CheckCatalog"/>, with what a repair needs.</summary>
public sealed record CatalogCheckReport(DateTimeOffset CheckedAt, int Media, IReadOnlyList<CatalogFinding> Findings)
{
    internal IReadOnlyList<Guid> MissingMedia { get; init; } = [];
    internal IReadOnlyList<(string Path, long Bytes)> OrphanFiles { get; init; } = [];
    internal IReadOnlyList<IReadOnlyList<Guid>> SamePathGroups { get; init; } = [];
    internal IReadOnlyList<Guid> PathKeyMedia { get; init; } = [];
    /// <summary>Videos to give a thumbnail (for "Fehlende Vorschauen erstellen").</summary>
    public IReadOnlyList<Guid> MissingThumbnailMedia { get; init; } = [];
    public long OrphanFileBytes => OrphanFiles.Sum(file => file.Bytes);
    public int Issues => Findings.Where(f => f.IsProblem).Sum(f => f.Count);
    public CatalogFinding this[CatalogFindingKind kind] => Findings.First(f => f.Kind == kind);
}

/// <summary>What a repair changed, per kind, and where the copy of the catalog made before it lies.</summary>
public sealed record CatalogRepairResult(string BackupPath, IReadOnlyDictionary<CatalogFindingKind, int> Repaired);

/// <summary>Check and repair of the catalog (plan phase V3). The check only reads; every repair first copies the catalog.</summary>
public sealed partial class CatalogRepository
{
    public const int CheckExampleLimit = 200;
    /// <summary>Library subfolders whose files the check compares with the catalog.</summary>
    public static readonly IReadOnlyList<string> CheckedFolders = ["thumbnails", "media-assets", "tag-assets", "hover-cache", "clip-cache"];
    /// <summary>Newer files may still be written by a running task and are never reported as unused.</summary>
    private static readonly TimeSpan OrphanMinimumAge = TimeSpan.FromMinutes(10);

    /// <summary>Checks the catalog and the library folder <paramref name="libraryDirectory"/>. Reads only, except that the
    /// time and the number of problems are noted for the overview.</summary>
    public CatalogCheckReport CheckCatalog(string libraryDirectory, IProgress<CatalogCheckProgress>? progress = null, CancellationToken token = default)
    {
        var findings = new List<CatalogFinding>();
        void Add(CatalogFindingKind kind, int count, IEnumerable<string> examples) => findings.Add(new(kind, count, examples.Take(CheckExampleLimit).ToArray()));
        long Scalar(string sql) { lock (gate) { ThrowIfDisposed(); using var command = Command(sql, null); using var cancel = token.Register(command.Cancel); return Convert.ToInt64(command.ExecuteScalar() ?? 0L); } }

        // 1. SQLite and the links between the tables.
        progress?.Report(new(CatalogFindingKind.Integrity, 0, 1));
        var integrity = new List<string>(); var orphanRows = new List<string>();
        lock (gate)
        {
            ThrowIfDisposed();
            using (var command = Command("PRAGMA integrity_check(1000)", null))
            using (token.Register(command.Cancel))
            using (var reader = command.ExecuteReader()) while (reader.Read()) { var value = reader.GetString(0); if (value != "ok") integrity.Add(value); }
            using (var command = Command("PRAGMA foreign_key_check", null))
            using (token.Register(command.Cancel))
            using (var reader = command.ExecuteReader()) while (reader.Read()) orphanRows.Add($"{reader.GetString(0)} #{reader.GetValue(1)} → {reader.GetString(2)}");
        }
        Add(CatalogFindingKind.Integrity, integrity.Count, integrity);
        Add(CatalogFindingKind.OrphanRows, orphanRows.Count, orphanRows);
        token.ThrowIfCancellationRequested();

        // 2. The media entries themselves.
        var media = new List<MediaItem>(); var unreadable = new List<string>(); var pathKeys = new List<(Guid Id, string Path)>();
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = (int)Scalar("SELECT count(*) FROM media");
        var after = "";
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var rows = new List<(string Id, string Key, string Document)>();
            lock (gate)
            {
                ThrowIfDisposed();
                using var command = Command("SELECT id,path_key,document FROM media WHERE id>$after ORDER BY id LIMIT 1000", null, ("$after", after));
                using var reader = command.ExecuteReader();
                while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
            if (rows.Count == 0) break;
            after = rows[^1].Id;
            foreach (var (id, key, document) in rows)
            {
                // Paths of unreadable entries still keep their thumbnails in use.
                CollectFileNames(document, referenced);
                MediaItem? item = null;
                try { item = JsonSerializer.Deserialize<MediaItem>(document, Json); } catch (JsonException) { }
                if (item is null || string.IsNullOrWhiteSpace(item.Path) || item.Id.ToString() != id) { unreadable.Add(id); continue; }
                string expected;
                try { expected = PathKey(item.Path); } catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { unreadable.Add(item.Path); continue; }
                if (expected != key) pathKeys.Add((item.Id, item.Path));
                media.Add(item);
            }
            progress?.Report(new(CatalogFindingKind.UnreadableEntries, media.Count + unreadable.Count, total));
        }
        Add(CatalogFindingKind.UnreadableEntries, unreadable.Count, unreadable);

        // 3. Query table and quick-search index, compared in SQL.
        progress?.Report(new(CatalogFindingKind.QueryTable, 0, 1));
        var stale = new List<string>(); int queryProblems, searchProblems; var searchMessages = new List<string>();
        lock (gate)
        {
            ThrowIfDisposed();
            const string Differs = """
                FROM media m LEFT JOIN media_query q ON q.id=m.id
                WHERE q.id IS NULL OR q.path IS NOT json_extract(m.document,'$.path') OR q.title IS NOT coalesce(json_extract(m.document,'$.title'),'')
                   OR (SELECT count(*) FROM media_tags t WHERE t.media_id=m.id) <>
                      (SELECT count(*) FROM json_each(m.document,'$.tags')) + (SELECT count(*) FROM json_each(m.document,'$.tagCategories') c, json_each(c.value))
                """;
            queryProblems = (int)Scalar("SELECT count(*) " + Differs);
            using (var command = Command($"SELECT json_extract(m.document,'$.path') {Differs} LIMIT {CheckExampleLimit}", null))
            using (var reader = command.ExecuteReader()) while (reader.Read()) stale.Add(reader.IsDBNull(0) ? "?" : reader.GetString(0));
            progress?.Report(new(CatalogFindingKind.SearchIndex, 0, 1));
            searchProblems = (int)Scalar("""
                SELECT (SELECT count(*) FROM media_query q WHERE NOT EXISTS(SELECT 1 FROM media_search s WHERE s.rowid=q.rowid)
                        AND NOT EXISTS(SELECT 1 FROM media_search_queue w WHERE w.id=q.rowid))
                     + (SELECT count(*) FROM media_search s WHERE NOT EXISTS(SELECT 1 FROM media_query q WHERE q.rowid=s.rowid)
                        AND NOT EXISTS(SELECT 1 FROM media_search_queue w WHERE w.id=s.rowid))
                """);
            if (searchProblems > 0) searchMessages.Add($"{searchProblems} rows");
            try { using var command = Command("INSERT INTO media_search(media_search) VALUES('integrity-check')", null); command.ExecuteNonQuery(); }
            catch (SqliteException error) { searchProblems++; searchMessages.Add(error.Message); }
        }
        Add(CatalogFindingKind.QueryTable, queryProblems, stale);
        Add(CatalogFindingKind.SearchIndex, searchProblems, searchMessages);
        Add(CatalogFindingKind.PathKeys, pathKeys.Count, pathKeys.Select(p => p.Path));

        // 4. The same file under several spellings: equal after Unicode normalization.
        var samePath = media.GroupBy(item => SamePathKey(item.Path)).Where(group => group.Count() > 1)
            .Select(group => (IReadOnlyList<Guid>)group.OrderBy(item => item.AddedAt).ThenBy(item => item.Id).Select(item => item.Id).ToArray()).ToArray();
        var byId = media.ToDictionary(item => item.Id);
        Add(CatalogFindingKind.SamePath, samePath.Sum(group => group.Count - 1), samePath.SelectMany(group => group.Skip(1).Select(id => byId[id].Path)));

        // 5. Media files and thumbnails on disk.
        var missing = new List<MediaItem>(); var offline = new List<MediaItem>(); var noThumbnail = new List<MediaItem>();
        var roots = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < media.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var item = media[index];
            if (index % 200 == 0) progress?.Report(new(CatalogFindingKind.MissingFiles, index, media.Count));
            var root = SafeRoot(item.Path);
            if (root is null || !roots.TryGetValue(root, out var connected)) roots[root ?? ""] = connected = root is not null && Directory.Exists(root);
            if (!connected) { offline.Add(item); continue; }
            bool exists;
            try { exists = File.Exists(item.Path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { exists = true; }
            if (!exists) { missing.Add(item); continue; }
            if (item.Kind == MediaKind.Video && (string.IsNullOrEmpty(item.ThumbnailPath) || !File.Exists(item.ThumbnailPath)) && !item.Extra.ContainsKey("thumbnail.assetId"))
                noThumbnail.Add(item);
        }
        Add(CatalogFindingKind.MissingFiles, missing.Count, missing.Select(item => item.Path));
        Add(CatalogFindingKind.OfflineFiles, offline.Count, offline.Select(item => item.Path));

        // 6. Files in the library folder that nothing refers to.
        var orphans = FindOrphanFiles(libraryDirectory, media, referenced, progress, token);
        Add(CatalogFindingKind.OrphanFiles, orphans.Count, orphans.Select(file => Path.GetRelativePath(libraryDirectory, file.Path)));
        Add(CatalogFindingKind.MissingThumbnails, noThumbnail.Count, noThumbnail.Select(item => item.Path));

        // 7. Every picture file the catalog uses: present, complete and with the recorded checksum.
        var damaged = FindMissingPictures(progress, token);
        Add(CatalogFindingKind.MissingPictures, damaged.Count, damaged);

        var report = new CatalogCheckReport(DateTimeOffset.Now, media.Count + unreadable.Count, findings)
        {
            MissingMedia = missing.Select(item => item.Id).ToArray(), OrphanFiles = orphans, SamePathGroups = samePath,
            PathKeyMedia = pathKeys.Select(p => p.Id).ToArray(), MissingThumbnailMedia = noThumbnail.Select(item => item.Id).ToArray(),
        };
        RecordCheck(report.Issues);
        return report;
    }

    /// <summary>Key under which two spellings of one path meet: full path, composed Unicode, upper case.</summary>
    private static string SamePathKey(string path)
    {
        try { return PathKey(path.Normalize(NormalizationForm.FormC)); } catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    private static string? SafeRoot(string path)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(path)) is { Length: > 0 } root ? root : null; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>Adds the file names of all string values in a JSON document that look like paths.</summary>
    private static void CollectFileNames(string json, HashSet<string> names)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            void Visit(JsonElement element)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.Object: foreach (var property in element.EnumerateObject()) Visit(property.Value); break;
                    case JsonValueKind.Array: foreach (var value in element.EnumerateArray()) Visit(value); break;
                    case JsonValueKind.String:
                        var text = element.GetString();
                        if (text is { Length: < 1024 } && (text.Contains('\\') || text.Contains('/'))) names.Add(Path.GetFileName(text.TrimEnd('\\', '/')));
                        break;
                }
            }
            Visit(document.RootElement);
        }
        catch (JsonException) { }
    }

    private List<(string Path, long Bytes)> FindOrphanFiles(string libraryDirectory, IReadOnlyList<MediaItem> media, HashSet<string> referenced,
        IProgress<CatalogCheckProgress>? progress, CancellationToken token)
    {
        progress?.Report(new(CatalogFindingKind.OrphanFiles, 0, 1));
        // Paths in scenes, bookmarks, collections and settings count as used as well.
        foreach (var table in new[] { "scenes", "bookmarks", "playlists", "ui_state", "preferences" })
            foreach (var json in ReadDocumentTexts(table, token)) CollectFileNames(json, referenced);
        // Cached copies in "media-assets" and "tag-assets" are named after the hash of their bytes.
        var hashes = ReferencedAssetHashes().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = media.Select(item => item.Id.ToString("N")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphans = new List<(string, long)>(); var now = DateTime.UtcNow;
        lock (gate)
        {
            ThrowIfDisposed();
            foreach (var file in assets.Unused(UsedAssetPaths(hashes), token))
                if (now - file.LastWriteTimeUtc >= OrphanMinimumAge) orphans.Add((file.FullName, file.Length));
        }
        foreach (var folder in CheckedFolders)
        {
            var directory = Path.Combine(libraryDirectory, folder);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                token.ThrowIfCancellationRequested();
                if (now - file.LastWriteTimeUtc < OrphanMinimumAge) continue;
                var name = file.Name; var stem = Path.GetFileNameWithoutExtension(name);
                var leftover = name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".part.jpg", StringComparison.OrdinalIgnoreCase);
                var used = !leftover && (referenced.Contains(name) || folder switch
                {
                    "media-assets" or "tag-assets" => hashes.Contains(stem),
                    "hover-cache" or "clip-cache" => stem.Length >= 32 && ids.Contains(stem[..32]),
                    // Thumbnails are named "<media id>-<random>.jpg"; only the stored path keeps one in use.
                    _ => false,
                });
                if (!used) orphans.Add((file.FullName, file.Length));
            }
        }
        return orphans;
    }

    private HashSet<string> UsedAssetPaths(IEnumerable<string> hashes) =>
        hashes.SelectMany(assets.Candidates).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads every picture file the catalog uses and verifies length and checksum; returns the problems.</summary>
    private List<string> FindMissingPictures(IProgress<CatalogCheckProgress>? progress, CancellationToken token)
    {
        var rows = new List<(string Table, string Hash, long Length)>();
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = Command("""
                SELECT 'media_assets',json_extract(document,'$.sha256'),json_extract(document,'$.byteLength') FROM media_assets
                UNION SELECT 'tag_assets',json_extract(document,'$.sha256'),json_extract(document,'$.byteLength') FROM tag_assets
                UNION SELECT 'legacy_blobs',sha256,length FROM legacy_blobs
                """, null);
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1), reader.IsDBNull(2) ? -1 : reader.GetInt64(2)));
        }
        var problems = new List<string>();
        for (var index = 0; index < rows.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            if (index % 200 == 0) progress?.Report(new(CatalogFindingKind.MissingPictures, index, rows.Count));
            var (table, hash, length) = rows[index];
            string? problem;
            try { problem = length < 0 ? "invalid" : assets.CopyTo(hash, length, Stream.Null, token) ? null : "missing"; }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { problem = error.Message; }
            if (problem is not null) problems.Add($"{table} {hash} · {problem}");
        }
        return problems;
    }

    private IEnumerable<string> ReadDocumentTexts(string table, CancellationToken token)
    {
        var after = "";
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var rows = new List<(string Id, string Document)>();
            lock (gate)
            {
                ThrowIfDisposed();
                using var command = Command($"SELECT id,document FROM {table} WHERE id>$after ORDER BY id LIMIT 500", null, ("$after", after));
                using var reader = command.ExecuteReader();
                while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
            }
            if (rows.Count == 0) yield break;
            after = rows[^1].Id;
            foreach (var row in rows) yield return row.Document;
        }
    }

    /// <summary>Copies the catalog (and, for an encrypted library, its key file) into <paramref name="directory"/> before a
    /// change and keeps only the newest copies (count of the backup schedule); returns the path of the copy.</summary>
    public string CreateSafetyCopy(string directory, string reason)
    {
        Directory.CreateDirectory(directory);
        var final = Path.Combine(directory, $"catalog-before-{reason}-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        if (File.Exists(final)) final = Path.Combine(directory, $"catalog-before-{reason}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
        FileAccessPolicy.Current.EnsureWritable(final);
        var temporary = final + ".tmp";
        try
        {
            lock (gate)
            {
                ThrowIfDisposed();
                using (var target = new SqliteConnection(CatalogEncryption.ConnectionString(temporary, key).ToString()))
                {
                    target.Open();
                    connection.BackupDatabase(target);
                }
                if (key is not null) File.Copy(CatalogEncryption.KeyFilePath(DatabasePath), CatalogEncryption.KeyFilePath(final));
            }
            File.Move(temporary, final);
            BackupRotation.Prune(directory, GetBackupSchedule().Keep);
            return final;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Repairs the chosen kinds of <paramref name="report"/> after copying the catalog into <paramref name="backupDirectory"/>.
    /// Missing files are removed from the catalog only when they are still missing; unused files only when they still exist.</summary>
    public CatalogRepairResult RepairCatalog(CatalogCheckReport report, IReadOnlySet<CatalogFindingKind> kinds, string backupDirectory, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(report); ArgumentNullException.ThrowIfNull(kinds);
        var backup = CreateSafetyCopy(backupDirectory, "repair");
        var repaired = new Dictionary<CatalogFindingKind, int>();
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            int Execute(string sql, params (string, object)[] parameters) { using var command = Command(sql, transaction, parameters); return command.ExecuteNonQuery(); }
            if (kinds.Contains(CatalogFindingKind.OrphanRows))
                repaired[CatalogFindingKind.OrphanRows] = new[] { "scenes", "bookmarks", "media_assets", "media_tags", "media_fingerprints", "play_history" }
                    .Where(table => { using var exists = Command("SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name", transaction, ("$name", table)); return exists.ExecuteScalar() is not null; })
                    .Sum(table => Execute($"DELETE FROM {table} WHERE NOT EXISTS(SELECT 1 FROM media m WHERE m.id={table}.media_id)"))
                    + Execute("DELETE FROM media_query WHERE NOT EXISTS(SELECT 1 FROM media m WHERE m.id=media_query.id)");
            if (kinds.Contains(CatalogFindingKind.PathKeys))
            {
                var fixedKeys = 0;
                foreach (var id in report.PathKeyMedia)
                {
                    token.ThrowIfCancellationRequested();
                    if (ReadMediaDocument(id, transaction) is not { } item) continue;
                    var keyText = PathKey(item.Path);
                    using var taken = Command("SELECT EXISTS(SELECT 1 FROM media WHERE path_key=$key AND id<>$id)", transaction, ("$key", keyText), ("$id", id.ToString()));
                    if (Convert.ToInt64(taken.ExecuteScalar()) != 0) continue;
                    fixedKeys += Execute("UPDATE media SET path_key=$key WHERE id=$id", ("$key", keyText), ("$id", id.ToString()));
                }
                repaired[CatalogFindingKind.PathKeys] = fixedKeys;
            }
            if (kinds.Contains(CatalogFindingKind.SamePath))
                repaired[CatalogFindingKind.SamePath] = report.SamePathGroups.Sum(group => MergeEntries(group, transaction, token));
            if (kinds.Contains(CatalogFindingKind.MissingFiles))
            {
                var gone = report.MissingMedia.Where(id => ReadMediaDocument(id, transaction) is { } item && SafeRoot(item.Path) is { } root && Directory.Exists(root) && !File.Exists(item.Path)).ToArray();
                DeleteMediaCore(gone, transaction);
                repaired[CatalogFindingKind.MissingFiles] = gone.Length;
            }
            var rebuild = kinds.Contains(CatalogFindingKind.QueryTable) || kinds.Contains(CatalogFindingKind.SearchIndex);
            if (rebuild) { RebuildIndexesCore(transaction); repaired[CatalogFindingKind.QueryTable] = report[CatalogFindingKind.QueryTable].Count; repaired[CatalogFindingKind.SearchIndex] = report[CatalogFindingKind.SearchIndex].Count; }
            transaction.Commit();
            cachedMedia = null;
        }
        if (kinds.Contains(CatalogFindingKind.QueryTable) || kinds.Contains(CatalogFindingKind.SearchIndex)) PrepareSearchIndex(token);
        if (kinds.Contains(CatalogFindingKind.OrphanFiles))
        {
            var removed = 0;
            lock (gate)
            {
                // A picture file may be in use again since the check (the same bytes stored anew); such files stay.
                ThrowIfDisposed(); var used = UsedAssetPaths(ReferencedAssetHashes());
                foreach (var (path, _) in report.OrphanFiles)
                {
                    if (used.Contains(path)) continue;
                    try { if (File.Exists(path)) { FileAccessPolicy.Current.EnsureWritable(path); File.Delete(path); removed++; } }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
            repaired[CatalogFindingKind.OrphanFiles] = removed;
        }
        return new(backup, repaired);
    }

    /// <summary>Builds query table, tag rows and quick-search index anew from the media entries ("Suchindex neu aufbauen").
    /// Returns the number of media indexed.</summary>
    public int RebuildSearchIndex(CancellationToken token = default)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            RebuildIndexesCore(transaction);
            transaction.Commit();
            cachedMedia = null;
        }
        return PrepareSearchIndex(token);
    }

    private void RebuildIndexesCore(SqliteTransaction transaction)
    {
        // The update re-runs the projection triggers; they queue every row for the quick-search index again.
        using var command = Command("""
            DELETE FROM media_query; DELETE FROM media_tags; UPDATE media SET document=document;
            DELETE FROM media_search; DELETE FROM media_search_queue; INSERT INTO media_search_queue SELECT rowid FROM media_query;
            """, transaction);
        command.ExecuteNonQuery();
    }

    /// <summary>Removes several media in one transaction, also from collections.</summary>
    public void DeleteMedia(IReadOnlyCollection<Guid> ids)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            DeleteMediaCore(ids, transaction);
            transaction.Commit();
            cachedMedia = null;
        }
    }

    private void DeleteMediaCore(IReadOnlyCollection<Guid> ids, SqliteTransaction transaction)
    {
        if (ids.Count == 0) return;
        var gone = ids.ToHashSet();
        using (var command = Command("DELETE FROM media WHERE id=$id", transaction, ("$id", "")))
            foreach (var id in gone) { command.Parameters["$id"].Value = id.ToString(); command.ExecuteNonQuery(); }
        foreach (var playlist in ReadDocuments<Playlist>("playlists", transaction))
            if (playlist.MediaIds.Any(gone.Contains) || playlist.Entries.Any(entry => gone.Contains(entry.MediaId)))
                SavePlaylist(playlist with { MediaIds = playlist.MediaIds.Where(id => !gone.Contains(id)).ToList(), Entries = playlist.Entries.Where(entry => !gone.Contains(entry.MediaId)).ToList() }, transaction);
    }

    /// <summary>Merges entries of one file into the first (oldest) one: tags and categories joined, the higher rating, marks
    /// kept, empty fields filled; scenes, bookmarks, graphics, history and collection places move over. Returns the number removed.</summary>
    private int MergeEntries(IReadOnlyList<Guid> group, SqliteTransaction transaction, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var items = group.Select(id => ReadMediaDocument(id, transaction)).OfType<MediaItem>().ToList();
        if (items.Count < 2) return 0;
        var keep = items[0]; var others = items.Skip(1).ToArray();
        var tags = keep.Tags.ToList();
        var categories = keep.TagCategories.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
        foreach (var other in others)
        {
            foreach (var tag in other.Tags) if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
            foreach (var (category, values) in other.TagCategories)
            {
                if (!categories.TryGetValue(category, out var list)) categories[category] = list = [];
                foreach (var value in values) if (!list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value);
            }
        }
        var extra = new Dictionary<string, string?>(keep.Extra);
        foreach (var other in others) foreach (var (name, value) in other.Extra) extra.TryAdd(name, value);
        var merged = keep with
        {
            Tags = tags, TagCategories = categories, Extra = extra,
            Rating = items.Max(item => item.Rating), IsFavorite = items.Any(item => item.IsFavorite), WatchLater = items.Any(item => item.WatchLater),
            Title = string.IsNullOrWhiteSpace(keep.Title) ? others.Select(item => item.Title).FirstOrDefault(title => !string.IsNullOrWhiteSpace(title)) ?? keep.Title : keep.Title,
            Description = keep.Description ?? others.Select(item => item.Description).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)),
            Category = keep.Category ?? others.Select(item => item.Category).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)),
            ThumbnailPath = keep.ThumbnailPath ?? others.Select(item => item.ThumbnailPath).FirstOrDefault(path => path is not null),
            PlayCount = items.Sum(item => item.PlayCount ?? 0) is var plays and > 0 ? plays : keep.PlayCount,
            OCount = items.Sum(item => item.OCount ?? 0) is var climaxes and > 0 ? climaxes : keep.OCount,
            WatchCount = items.All(item => item.WatchCount is null) ? null : items.Sum(item => item.WatchCount ?? item.PlayCount ?? 0),
        };
        foreach (var other in others)
            foreach (var table in new[] { "scenes", "bookmarks", "media_assets" })
            {
                using var move = Command($"UPDATE {table} SET media_id=$keep, document=json_set(document,'$.mediaId',$keep) WHERE media_id=$other", transaction,
                    ("$keep", keep.Id.ToString()), ("$other", other.Id.ToString()));
                move.ExecuteNonQuery();
            }
        foreach (var other in others)
        {
            using var history = Command("UPDATE play_history SET media_id=$keep WHERE media_id=$other", transaction, ("$keep", keep.Id.ToString()), ("$other", other.Id.ToString()));
            history.ExecuteNonQuery();
        }
        var removed = others.Select(item => item.Id).ToHashSet();
        foreach (var playlist in ReadDocuments<Playlist>("playlists", transaction))
            if (playlist.MediaIds.Any(removed.Contains) || playlist.Entries.Any(entry => removed.Contains(entry.MediaId)))
                SavePlaylist(playlist with
                {
                    MediaIds = playlist.MediaIds.Select(id => removed.Contains(id) ? keep.Id : id).Distinct().ToList(),
                    Entries = playlist.Entries.Select(entry => removed.Contains(entry.MediaId) ? entry with { MediaId = keep.Id } : entry).ToList(),
                }, transaction);
        using (var delete = Command("DELETE FROM media WHERE id=$id", transaction, ("$id", "")))
            foreach (var id in removed) { delete.Parameters["$id"].Value = id.ToString(); delete.ExecuteNonQuery(); }
        UpsertCore(merged, transaction);
        return removed.Count;
    }
}
