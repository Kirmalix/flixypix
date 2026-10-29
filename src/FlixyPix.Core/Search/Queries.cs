using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public enum MediaSort { Title, Path, AddedAt, Rating, Duration, FileSize, FileDate, OCount, Resolution, Bitrate }

/// <summary>Database-side search. Text consists of whitespace-separated AND terms, compared with OrdinalIgnoreCase.</summary>
public sealed record MediaQuery
{
    public SearchExpression? Expression { get; init; }
    public string? Text { get; init; }
    public string? Tag { get; init; }
    /// <summary>Every tag must be present (AND).</summary>
    public IReadOnlyList<string> RequiredTags { get; init; } = [];
    /// <summary>None of these tags may be present.</summary>
    public IReadOnlyList<string> ExcludedTags { get; init; } = [];
    public string? TagCategory { get; init; }
    public string? Category { get; init; }
    public int MinimumRating { get; init; }
    public bool? IsFavorite { get; init; }
    public bool? WatchLater { get; init; }
    public MediaKind? Kind { get; init; }
    public string? Folder { get; init; }
    public bool IncludeSubfolders { get; init; } = true;
    public MediaSort Sort { get; init; }
    public bool Descending { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; } = 120;
}

public sealed record MediaPage(IReadOnlyList<MediaItem> Items, long TotalCount);
/// <param name="FolderCounts">Media directly in each folder (not in its subfolders).</param>
/// <param name="CategoryCounts">Media per category.</param>
/// <param name="TagCategoryCounts">Media with at least one tag of each tag category.</param>
public sealed record CatalogFacets(long MediaCount, IReadOnlyList<string> Tags, IReadOnlyList<string> Categories,
    IReadOnlyList<string> TagCategories, IReadOnlyList<string> Folders,
    IReadOnlyDictionary<string, IReadOnlyList<string>> CategorizedTags,
    IReadOnlyDictionary<string, long>? FolderCounts = null, IReadOnlyDictionary<string, long>? CategoryCounts = null, IReadOnlyDictionary<string, long>? TagCategoryCounts = null);

public sealed partial class CatalogRepository
{
    /// <summary>The consistent pre-migration backup created on this open, or null if no migration was needed.</summary>
    public string? MigrationBackupPath { get; private set; }

    /// <summary>Indexes use this collation, so every connection that verifies or rewrites the file needs it.</summary>
    internal static void AddOrdinalCollation(SqliteConnection target) =>
        target.CreateCollation(ProductIdentity.OrdinalCollation, (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right));

    private void RegisterQueryFunctions()
    {
        AddOrdinalCollation(connection);
        connection.CreateFunction<string?, string?, bool>("efp_contains", (value, term) =>
            value?.Contains(term ?? "", StringComparison.OrdinalIgnoreCase) ?? false, isDeterministic: true);
        connection.CreateFunction<string?, string?, bool>("efp_starts", (value, term) =>
            value?.StartsWith(term ?? "", StringComparison.OrdinalIgnoreCase) ?? false, isDeterministic: true);
        connection.CreateFunction<string?, string?, bool>("efp_ends", (value, term) =>
            value?.EndsWith(term ?? "", StringComparison.OrdinalIgnoreCase) ?? false, isDeterministic: true);
        connection.CreateFunction<string?, string>("efp_folder", value => Path.GetDirectoryName(value ?? "") ?? "", isDeterministic: true);
        connection.CreateFunction<string?, string>("efp_filename", value => Path.GetFileName(value ?? ""), isDeterministic: true);
        connection.CreateFunction<string?, string>("efp_extension", value => Path.GetExtension(value ?? "").TrimStart('.'), isDeterministic: true);
        connection.CreateFunction<string?, string>("efp_directory", value =>
        {
            var directory = Path.GetDirectoryName(value ?? "") ?? "";
            return directory.Length == 0 || Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        }, isDeterministic: true);
        connection.CreateFunction<string?, string?, bool, bool>("efp_in_folder", (path, folder, recursive) =>
        {
            if (path is null || folder is null) return false;
            var directory = Path.GetDirectoryName(path) ?? "";
            var root = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return directory.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                recursive && directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }, isDeterministic: true);
    }

    /// <summary>Copies the catalog page by page beside it before its format changes, reporting the pages copied.</summary>
    private void CreateMigrationBackup(long version, IProgress<CatalogOpenProgress>? progress, CancellationToken token)
    {
        var final = DatabasePath + $".before-v{SchemaVersion}-v{version}-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}.bak";
        FileAccessPolicy.Current.EnsureWritable(final);
        var temporary = final + ".tmp";
        var needed = new FileInfo(DatabasePath).Length + (File.Exists(DatabasePath + "-wal") ? new FileInfo(DatabasePath + "-wal").Length : 0);
        try
        {
            var free = new DriveInfo(System.IO.Path.GetPathRoot(DatabasePath)!).AvailableFreeSpace;
            if (free < needed + 64L * 1024 * 1024)
                throw new IOException(T("Not enough free space for the safety copy of the catalog: {0:N0} MB needed, {1:N0} MB free.", needed / 1048576 + 64, free / 1048576));
        }
        catch (ArgumentException) { } // a path without a drive letter (network share): the copy itself reports a full disk
        try
        {
            using (var target = new SqliteConnection(CatalogEncryption.ConnectionString(temporary, key).ToString()))
            {
                target.Open();
                var backup = SQLitePCL.raw.sqlite3_backup_init(target.Handle, "main", connection.Handle, "main");
                if (backup is null) throw new IOException(T("The safety copy of the catalog could not be started."));
                try
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        var result = SQLitePCL.raw.sqlite3_backup_step(backup, 2048);
                        var total = SQLitePCL.raw.sqlite3_backup_pagecount(backup);
                        progress?.Report(new(CatalogOpenPhase.SafetyCopy, total - SQLitePCL.raw.sqlite3_backup_remaining(backup), total));
                        if (result == SQLitePCL.raw.SQLITE_DONE) break;
                        if (result is not (SQLitePCL.raw.SQLITE_OK or SQLitePCL.raw.SQLITE_BUSY or SQLitePCL.raw.SQLITE_LOCKED))
                            throw new IOException(T("The safety copy of the catalog failed (SQLite {0}).", result));
                    }
                }
                finally { SQLitePCL.raw.sqlite3_backup_finish(backup); }
            }
            // An encrypted backup is only usable together with the wrapped keys valid at backup time.
            if (key is not null) File.Copy(CatalogEncryption.KeyFilePath(DatabasePath), CatalogEncryption.KeyFilePath(final));
            File.Move(temporary, final);
            MigrationBackupPath = final;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void InitializeQuerySchema(SqliteTransaction transaction, bool rebuild)
    {
        // Triggers use only built-in JSON/SQL operations, so third-party SQLite writers do not need our .NET functions.
        var projection = $"""
            json_extract(NEW.document,'$.title'), json_extract(NEW.document,'$.path'),
            CASE json_extract(NEW.document,'$.kind') WHEN 0 THEN 'Image' WHEN 1 THEN 'Video' WHEN 2 THEN 'Audio' ELSE json_extract(NEW.document,'$.kind') END, coalesce(json_extract(NEW.document,'$.rating'),0),
            coalesce(json_extract(NEW.document,'$.isFavorite'),0), coalesce(json_extract(NEW.document,'$.watchLater'),0),
            json_extract(NEW.document,'$.category'), julianday(json_extract(NEW.document,'$.addedAt')),
            json_extract(NEW.document,'$.durationSeconds'), json_extract(NEW.document,'$.fileSize'),
            coalesce(json_extract(NEW.document,'$.title'),'') || char(10) || coalesce(json_extract(NEW.document,'$.path'),'') || char(10) ||
            coalesce(json_extract(NEW.document,'$.description'),'') || char(10) ||
            coalesce((SELECT group_concat(value,char(10)) FROM json_each(NEW.document,'$.tags')),''),
            julianday(coalesce(json_extract(NEW.document,'$.fileCreatedAt'), json_extract(NEW.document,'$.originalFileDate'))),
            julianday(coalesce(json_extract(NEW.document,'$.originalAddedAt'), json_extract(NEW.document,'$.addedAt'))),
            CASE WHEN coalesce(json_extract(NEW.document,'$.durationSeconds'),0) > 0 AND coalesce(json_extract(NEW.document,'$.playbackPositionSeconds'),0) > 0
                THEN 1.0 * json_extract(NEW.document,'$.playbackPositionSeconds') / json_extract(NEW.document,'$.durationSeconds') END,
            julianday(json_extract(NEW.document,'$.lastPlayedAt')), coalesce(json_extract(NEW.document,'$.oCount'),0),
            json_extract(NEW.document,'$.technical.videoCodec'), json_extract(NEW.document,'$.technical.frameRate'),
            coalesce(json_extract(NEW.document,'$.technical.bitRate')/1000.0, CASE WHEN json_extract(NEW.document,'$.durationSeconds')>0 AND json_extract(NEW.document,'$.fileSize') IS NOT NULL
                THEN json_extract(NEW.document,'$.fileSize')*8.0/json_extract(NEW.document,'$.durationSeconds')/1000 END),
            CASE WHEN json_type(NEW.document,'$.technical')='object' THEN coalesce(json_extract(NEW.document,'$.technical.hdr'),0) END,
            1 + coalesce(json_array_length(NEW.document,'$.files'),0),
            min(json_extract(NEW.document,'$.width'),json_extract(NEW.document,'$.height'))
            """;
        // Columns are named: added columns sit at the end of an upgraded table, in the order they were added.
        const string QueryColumns = "id,title,path,kind,rating,pinned,watch_later,category,added,duration,file_size,search_text,file_date,imported,progress,last_played,o_count,video_codec,frame_rate,bit_rate,hdr,versions,lines";
        // Older libraries lack later columns (10: file date and import date; 11: progress and last playback for the start
        // page; 12: the O counter; 14: technical data, number of versions and the shorter picture side): add them, recreate the
        // triggers that fill them and fill them once. Sorting by "added" uses the import date, so the old paging index goes;
        // sorting by file date is rare and needs no index. Bit rate falls back to file size and duration, as the search did before.
        string[] added = ["file_date", "imported", "progress", "last_played", "o_count", "video_codec", "frame_rate", "bit_rate", "hdr", "versions", "lines"];
        var present = new HashSet<string>(StringComparer.Ordinal); var hasTable = false;
        using (var probe = Command("SELECT name FROM pragma_table_info('media_query')", transaction))
        using (var reader = probe.ExecuteReader()) while (reader.Read()) { hasTable = true; present.Add(reader.GetString(0)); }
        var missing = added.Where(column => !present.Contains(column)).ToList();
        if (hasTable && missing.Count > 0)
        {
            using var upgrade = Command(string.Concat(missing.Select(column => $"ALTER TABLE media_query ADD COLUMN {column} {(column == "video_codec" ? "TEXT" : "REAL")};")) + """
                DROP TRIGGER IF EXISTS media_query_insert; DROP TRIGGER IF EXISTS media_query_update;
                DROP INDEX IF EXISTS media_query_added_page;
                """, transaction);
            upgrade.ExecuteNonQuery();
            rebuild = true;
        }
        using var create = Command($"""
            CREATE TABLE IF NOT EXISTS media_query(
                id TEXT PRIMARY KEY REFERENCES media(id) ON DELETE CASCADE, title TEXT NOT NULL, path TEXT NOT NULL,
                kind TEXT NOT NULL, rating INTEGER NOT NULL, pinned INTEGER NOT NULL, watch_later INTEGER NOT NULL,
                category TEXT, added REAL, duration REAL, file_size INTEGER, search_text TEXT NOT NULL, file_date REAL, imported REAL, progress REAL, last_played REAL, o_count REAL,
                video_codec TEXT, frame_rate REAL, bit_rate REAL, hdr REAL, versions REAL, lines REAL);
            CREATE TABLE IF NOT EXISTS media_tags(
                media_id TEXT NOT NULL REFERENCES media(id) ON DELETE CASCADE, tag TEXT NOT NULL, category TEXT NOT NULL DEFAULT '');
            CREATE INDEX IF NOT EXISTS media_query_title_page ON media_query(title COLLATE EFP_ORDINAL,path COLLATE EFP_ORDINAL,id);
            CREATE INDEX IF NOT EXISTS media_query_path_page ON media_query(path COLLATE EFP_ORDINAL,title COLLATE EFP_ORDINAL,id);
            CREATE INDEX IF NOT EXISTS media_query_rating_page ON media_query(rating,title COLLATE EFP_ORDINAL,path COLLATE EFP_ORDINAL,id);
            CREATE INDEX IF NOT EXISTS media_query_duration_page ON media_query(duration,title COLLATE EFP_ORDINAL,path COLLATE EFP_ORDINAL,id);
            CREATE INDEX IF NOT EXISTS media_query_size_page ON media_query(file_size,title COLLATE EFP_ORDINAL,path COLLATE EFP_ORDINAL,id);
            CREATE INDEX IF NOT EXISTS media_query_imported_page ON media_query(imported,title COLLATE EFP_ORDINAL,path COLLATE EFP_ORDINAL,id);
            CREATE INDEX IF NOT EXISTS media_query_category ON media_query(category COLLATE EFP_ORDINAL);
            CREATE INDEX IF NOT EXISTS media_query_started ON media_query(progress) WHERE progress > 0;
            CREATE INDEX IF NOT EXISTS media_query_last_played ON media_query(last_played) WHERE last_played IS NOT NULL;
            CREATE INDEX IF NOT EXISTS media_query_kind ON media_query(kind);
            CREATE INDEX IF NOT EXISTS media_query_o_page ON media_query(o_count,title COLLATE EFP_ORDINAL,path COLLATE EFP_ORDINAL,id);
            CREATE INDEX IF NOT EXISTS media_query_lines_page ON media_query(lines,title COLLATE EFP_ORDINAL,path COLLATE EFP_ORDINAL,id);
            CREATE INDEX IF NOT EXISTS media_query_bit_rate_page ON media_query(bit_rate,title COLLATE EFP_ORDINAL,path COLLATE EFP_ORDINAL,id);
            CREATE INDEX IF NOT EXISTS media_query_video_codec ON media_query(video_codec) WHERE video_codec IS NOT NULL;
            CREATE TABLE IF NOT EXISTS media_files(path_key TEXT PRIMARY KEY, media_id TEXT NOT NULL REFERENCES media(id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS media_files_media ON media_files(media_id);
            CREATE INDEX IF NOT EXISTS media_tags_tag ON media_tags(tag COLLATE EFP_ORDINAL,category COLLATE EFP_ORDINAL,media_id);
            CREATE INDEX IF NOT EXISTS media_tags_category ON media_tags(category COLLATE EFP_ORDINAL,tag COLLATE EFP_ORDINAL,media_id);
            CREATE INDEX IF NOT EXISTS media_tags_media ON media_tags(media_id);
            CREATE TRIGGER IF NOT EXISTS media_query_insert AFTER INSERT ON media BEGIN
                INSERT INTO media_query({QueryColumns}) VALUES(NEW.id,{projection});
                INSERT INTO media_tags(media_id,tag) SELECT NEW.id,value FROM json_each(NEW.document,'$.tags');
                INSERT INTO media_tags(media_id,tag,category) SELECT NEW.id,t.value,c.key FROM json_each(NEW.document,'$.tagCategories') c,json_each(c.value) t;
            END;
            CREATE TRIGGER IF NOT EXISTS media_query_update AFTER UPDATE OF document ON media BEGIN
                DELETE FROM media_query WHERE id=OLD.id;
                INSERT INTO media_query({QueryColumns}) VALUES(NEW.id,{projection});
                DELETE FROM media_tags WHERE media_id=OLD.id;
                INSERT INTO media_tags(media_id,tag) SELECT NEW.id,value FROM json_each(NEW.document,'$.tags');
                INSERT INTO media_tags(media_id,tag,category) SELECT NEW.id,t.value,c.key FROM json_each(NEW.document,'$.tagCategories') c,json_each(c.value) t;
            END;
            """, transaction);
        create.ExecuteNonQuery();
        if (rebuild)
        {
            // Update invokes the projection trigger without retaining the entire catalog in managed memory.
            using var populate = Command("DELETE FROM media_query; DELETE FROM media_tags; UPDATE media SET document=document;", transaction);
            populate.ExecuteNonQuery();
        }
    }

    public MediaItem? GetMedia(Guid id)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = Command("SELECT document FROM media WHERE id=$id", null, ("$id", id.ToString()));
            return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<MediaItem>(json, Json) : null;
        }
    }

    public MediaPage QueryPage(MediaQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0 || query.Limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(query), "Offset must be nonnegative and page size 1–1000.");
        lock (gate)
        {
            ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
            using var transaction = connection.BeginTransaction();
            var (where, parameters) = BuildQuery(query, transaction);
            long count;
            using (var command = Command("SELECT count(*) FROM media_query q " + where, transaction, parameters))
            {
                using var cancel = cancellationToken.Register(command.Cancel);
                count = Convert.ToInt64(command.ExecuteScalar());
            }
            var items = QueryItems(query, transaction, cancellationToken, where, parameters);
            CommitAfterIndexing(transaction);
            return new MediaPage(items, count);
        }
    }

    private IReadOnlyList<MediaItem> QueryItems(MediaQuery query, SqliteTransaction? transaction, CancellationToken cancellationToken,
        string? where = null, (string Name, object Value)[]? parameters = null)
    {
        if (where is null) (where, parameters) = BuildQuery(query, transaction);
        var order = FullOrderSql(query);
        // Apply OFFSET/LIMIT to the compact projection before fetching large JSON documents.
        // A deep page must not pull 100000 complete records through the media table.
        var sql = transaction is null
            ? $"SELECT m.document FROM media_query q JOIN media m ON m.id=q.id {where} ORDER BY {order}"
            : $"SELECT m.document FROM (SELECT q.* FROM media_query q {where} ORDER BY {order} LIMIT $limit OFFSET $offset) q JOIN media m ON m.id=q.id ORDER BY {order}";
        using var command = Command(sql, transaction, parameters!);
        if (transaction is not null) { command.Parameters.AddWithValue("$limit", query.Limit); command.Parameters.AddWithValue("$offset", query.Offset); }
        using var cancel = cancellationToken.Register(command.Cancel);
        using var reader = command.ExecuteReader();
        var items = new List<MediaItem>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(JsonSerializer.Deserialize<MediaItem>(reader.GetString(0), Json) ?? throw new InvalidDataException("Invalid media record."));
        }
        return items;
    }

    private (string Where, (string Name, object Value)[] Parameters) BuildQuery(MediaQuery query, SqliteTransaction? transaction)
    {
        var conditions = new List<string>();
        var parameters = new List<(string Name, object Value)>();
        string Parameter(object value) { var name = "$q" + parameters.Count; parameters.Add((name, value)); return name; }
        if (query.MinimumRating != 0) conditions.Add("q.rating >= " + Parameter(query.MinimumRating));
        if (query.WatchLater is { } later) conditions.Add("q.watch_later=" + Parameter(later ? 1 : 0));
        if (query.Kind is { } kind)
        {
            if (!Enum.IsDefined(kind)) throw new ArgumentException("Invalid media kind.", nameof(query));
            conditions.Add("q.kind=" + Parameter(kind.ToString()));
        }
        if (query.IsFavorite is { } favorite)
        {
            var preferences = ReadPreferences(transaction);
            var expression = $"(q.pinned=1 OR q.rating >= CASE q.kind WHEN 'Image' THEN {preferences.ImageFavoriteMinimumRating} WHEN 'Video' THEN {preferences.VideoFavoriteMinimumRating} ELSE {preferences.AudioFavoriteMinimumRating} END)";
            conditions.Add(favorite ? expression : "NOT " + expression);
        }
        if (!string.IsNullOrWhiteSpace(query.Category)) conditions.Add("q.category=" + Parameter(query.Category.Trim()) + " COLLATE EFP_ORDINAL");
        if (!string.IsNullOrWhiteSpace(query.TagCategory))
        {
            var filter = "t.category=" + Parameter(query.TagCategory.Trim()) + " COLLATE EFP_ORDINAL";
            if (!string.IsNullOrWhiteSpace(query.Tag)) filter += " AND t.tag=" + Parameter(query.Tag.Trim()) + " COLLATE EFP_ORDINAL";
            conditions.Add("q.id IN (SELECT t.media_id FROM media_tags t WHERE " + filter + ")");
        }
        else if (!string.IsNullOrWhiteSpace(query.Tag)) conditions.Add("q.id IN (SELECT t.media_id FROM media_tags t WHERE t.tag=" + Parameter(query.Tag.Trim()) + " COLLATE EFP_ORDINAL)");
        foreach (var tag in query.RequiredTags.Where(t => !string.IsNullOrWhiteSpace(t)))
            conditions.Add("q.id IN (SELECT t.media_id FROM media_tags t WHERE t.tag=" + Parameter(tag.Trim()) + " COLLATE EFP_ORDINAL)");
        foreach (var tag in query.ExcludedTags.Where(t => !string.IsNullOrWhiteSpace(t)))
            conditions.Add("q.id NOT IN (SELECT t.media_id FROM media_tags t WHERE t.tag=" + Parameter(tag.Trim()) + " COLLATE EFP_ORDINAL)");
        if (!string.IsNullOrWhiteSpace(query.Folder)) conditions.Add($"efp_in_folder(q.path,{Parameter(query.Folder)},{Parameter(query.IncludeSubfolders ? 1 : 0)})");
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            // The trigram index answers terms of three or more characters when it is current; it finds the same hits.
            var terms = query.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var vocabulary = ReadPreferences(transaction).TagVocabulary;
            var ordinary = new List<string>();
            foreach (var term in terms)
            {
                var tags = vocabulary.Where(v => (v.Aliases ?? []).Any(a => a.Contains(term, StringComparison.OrdinalIgnoreCase))).Select(v => v.Tag).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (tags.Length == 0) { ordinary.Add(term); continue; }
                var literal = UsesSearchIndex(term) ? SearchIndexCondition([term], transaction, Parameter) : null;
                literal ??= "efp_contains(q.search_text," + Parameter(term) + ")";
                var targets = Parameter(JsonSerializer.Serialize(tags));
                conditions.Add("(" + literal + " OR q.id IN (SELECT media_id FROM media_tags WHERE tag COLLATE EFP_ORDINAL IN (SELECT value FROM json_each(" + targets + "))))");
            }
            var indexed = ordinary.Where(UsesSearchIndex).ToList();
            if (SearchIndexCondition(indexed, transaction, Parameter) is { } condition) conditions.Add(condition); else indexed.Clear();
            foreach (var term in ordinary.Where(t => !indexed.Contains(t))) conditions.Add("efp_contains(q.search_text," + Parameter(term) + ")");
        }
        if (query.Expression is not null)
        {
            ValidateSearchExpression(query.Expression);
            conditions.Add(BuildExpressionSql(query.Expression, Parameter, transaction));
        }
        return (conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions), parameters.ToArray());
    }

    private CatalogFacets? cachedFacets;
    private (long Changes, long DataVersion) cachedFacetStamp;

    // Facets scan every media row; browsing asks for them on each refresh, so reuse them
    // until this connection writes (total_changes) or another connection commits (data_version).
    private (long Changes, long DataVersion) ChangeStamp()
    {
        using var changes = Command("SELECT total_changes()", null);
        using var version = Command("PRAGMA data_version", null);
        return (Convert.ToInt64(changes.ExecuteScalar()), Convert.ToInt64(version.ExecuteScalar()));
    }

    public CatalogFacets GetFacets(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
            var stamp = ChangeStamp();
            if (cachedFacets is not null && cachedFacetStamp == stamp) return cachedFacets;
            using var transaction = connection.BeginTransaction();
            IReadOnlyList<string> Values(string sql)
            {
                using var command = Command(sql, transaction);
                using var cancel = cancellationToken.Register(command.Cancel);
                using var reader = command.ExecuteReader();
                var values = new List<string>();
                while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); if (!reader.IsDBNull(0)) values.Add(reader.GetString(0)); }
                return values;
            }
            long count;
            using (var countCommand = Command("SELECT count(*) FROM media_query", transaction)) count = Convert.ToInt64(countCommand.ExecuteScalar());
            var tags = Values("SELECT DISTINCT tag COLLATE EFP_ORDINAL FROM media_tags ORDER BY tag COLLATE EFP_ORDINAL");
            // Values and how many media carry each, in the order of the value.
            (IReadOnlyList<string> Values, Dictionary<string, long> Counts) Counted(string sql)
            {
                using var command = Command(sql, transaction);
                using var cancel = cancellationToken.Register(command.Cancel);
                using var reader = command.ExecuteReader();
                var values = new List<string>(); var counts = new Dictionary<string, long>(StringComparer.Ordinal);
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested(); if (reader.IsDBNull(0)) continue;
                    var value = reader.GetString(0); values.Add(value); counts[value] = reader.GetInt64(1);
                }
                return (values, counts);
            }
            var (categories, categoryCounts) = Counted("SELECT category COLLATE EFP_ORDINAL,count(*) FROM media_query WHERE category IS NOT NULL GROUP BY 1 ORDER BY 1 COLLATE EFP_ORDINAL");
            var groups = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            using (var command = Command("SELECT DISTINCT category COLLATE EFP_ORDINAL,tag COLLATE EFP_ORDINAL FROM media_tags WHERE category<>'' ORDER BY category COLLATE EFP_ORDINAL,tag COLLATE EFP_ORDINAL", transaction))
            {
                using var cancel = cancellationToken.Register(command.Cancel);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var group = reader.GetString(0);
                    if (!groups.TryGetValue(group, out var list)) groups[group] = list = new List<string>();
                    ((List<string>)list).Add(reader.GetString(1));
                }
            }
            var (folders, folderCounts) = Counted("SELECT efp_folder(path) COLLATE EFP_ORDINAL,count(*) FROM media_query GROUP BY 1 ORDER BY 1 COLLATE EFP_ORDINAL");
            var (_, tagCategoryCounts) = Counted("SELECT category COLLATE EFP_ORDINAL,count(DISTINCT media_id) FROM media_tags WHERE category<>'' GROUP BY 1");
            transaction.Commit();
            cachedFacets = new(count, tags, categories, groups.Keys.ToArray(), folders, groups, folderCounts, categoryCounts, tagCategoryCounts);
            cachedFacetStamp = stamp;
            return cachedFacets;
        }
    }
}
