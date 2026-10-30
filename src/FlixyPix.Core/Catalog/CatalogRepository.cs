using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>A single process, thread-safe SQLite catalog. Each operation owns a transaction where needed.</summary>
public sealed partial class CatalogRepository : IDisposable
{
    private readonly object gate = new();
    private readonly SqliteConnection connection;
    private bool disposed;
    private List<MediaItem>? cachedMedia;
    private long cachedDataVersion = -1;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    /// <summary>Options for media documents in the catalog: as <see cref="Json"/>, without null properties (see
    /// <see cref="CompactDocuments"/>). Declared here, after <see cref="Json"/>, so it is initialized after it.</summary>
    internal static readonly JsonSerializerOptions MediaJson = new(Json) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>10 adds the file date and import date to the query table, 11 progress and last playback, 12 the O counter
    /// and the playback history, 13 bookmark tags, collection sets and the quick collection (older versions would drop them
    /// when they save), 14 technical data and further versions of a medium (query columns and the table media_files), 15 picture
    /// bytes in files beside the catalog instead of inside it (see <see cref="AssetStore"/>), 16 smaller documents (see
    /// <see cref="CompactDocuments"/>).</summary>
    internal const long SchemaVersion = 17;

    public string DatabasePath { get; }
    /// <summary>Data key of an encrypted library; snapshots and backups reuse it.</summary>
    private readonly CatalogKey? key;
    public bool IsEncrypted => key is not null;
    /// <summary>The catalog was brought to the current format when this repository opened it.</summary>
    public bool UpgradedOnOpen { get; }

    public CatalogRepository(string databasePath) : this(databasePath, null) { }

    /// <param name="key">Required exactly when the library has a key file; the repository keeps its own copy.</param>
    public CatalogRepository(string databasePath, CatalogKey? key) : this(databasePath, key, null) { }

    /// <summary>Opens the library and, if needed, brings it to the current format (see <see cref="Inspect"/> for what that
    /// takes). <paramref name="progress"/> hears each long phase; <paramref name="token"/> cancels it. A cancelled or failed
    /// change leaves the catalog as it was: everything runs in one transaction, and the safety copy made for it is removed.</summary>
    public CatalogRepository(string databasePath, CatalogKey? key, IProgress<CatalogOpenProgress>? progress, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = System.IO.Path.GetFullPath(databasePath);
        FileAccessPolicy.Current.EnsureWritable(DatabasePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DatabasePath)!);
        var encrypted = CatalogEncryption.IsEncrypted(DatabasePath);
        if (encrypted && key is null) throw new CatalogLockedException(T("The library is encrypted and must be unlocked first."));
        if (!encrypted && key is not null) throw new InvalidOperationException(T("The library is not encrypted."));
        this.key = key?.Clone();
        connection = new SqliteConnection(CatalogEncryption.ConnectionString(DatabasePath, this.key).ToString());
        long schemaVersion = 0; var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            assets = new AssetStore(DatabasePath, this.key, progress, token);
            connection.Open();
            RegisterQueryFunctions();
            using (var schemaCommand = Command("PRAGMA user_version", null)) schemaVersion = Convert.ToInt64(schemaCommand.ExecuteScalar());
            if (schemaVersion > SchemaVersion) throw new NotSupportedException("This catalog uses a newer database schema.");
            if (schemaVersion is > 0 and < SchemaVersion) CreateMigrationBackup(schemaVersion, progress, token);
            using (var pragmas = Command("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;", null)) pragmas.ExecuteNonQuery();
            using var transaction = connection.BeginTransaction();
            if (schemaVersion == 1)
            {
                using var migration = Command("""
                    ALTER TABLE legacy_records RENAME TO legacy_records_v1;
                    CREATE TABLE legacy_records(id INTEGER PRIMARY KEY, batch TEXT NOT NULL, ordinal INTEGER NOT NULL, record TEXT NOT NULL, UNIQUE(batch,ordinal));
                    INSERT INTO legacy_records(batch,ordinal,record) SELECT 'legacy-v1',id,record FROM legacy_records_v1 ORDER BY id;
                    DROP TABLE legacy_records_v1;
                    """, transaction);
                migration.ExecuteNonQuery();
            }
            using var command = Command("""
                CREATE TABLE IF NOT EXISTS media(id TEXT PRIMARY KEY, path_key TEXT NOT NULL UNIQUE, document TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS playlists(id TEXT PRIMARY KEY, document TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS scenes(id TEXT PRIMARY KEY, media_id TEXT NOT NULL REFERENCES media(id) ON DELETE CASCADE, document TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS legacy_records(id INTEGER PRIMARY KEY, batch TEXT NOT NULL, ordinal INTEGER NOT NULL, record TEXT NOT NULL, UNIQUE(batch,ordinal));
                CREATE TABLE IF NOT EXISTS saved_searches(id TEXT PRIMARY KEY, document TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS filename_rules(id TEXT PRIMARY KEY, document TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS bookmarks(id TEXT PRIMARY KEY, media_id TEXT NOT NULL REFERENCES media(id) ON DELETE CASCADE, document TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS preferences(id TEXT PRIMARY KEY, document TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS ui_state(id TEXT PRIMARY KEY, document TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS media_assets(id TEXT PRIMARY KEY, media_id TEXT NOT NULL REFERENCES media(id) ON DELETE CASCADE, position INTEGER NOT NULL, document TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS media_assets_media ON media_assets(media_id,position);
                CREATE INDEX IF NOT EXISTS bookmarks_media ON bookmarks(media_id);
                CREATE INDEX IF NOT EXISTS scenes_media ON scenes(media_id);
                CREATE TABLE IF NOT EXISTS collection_sets(id TEXT PRIMARY KEY, document TEXT NOT NULL);
                """, transaction);
            command.ExecuteNonQuery();
            // Schema 16 reads the original's dates from the medium itself; the triggers are created anew.
            if (schemaVersion is > 0 and < 16) using (var drop = Command("DROP TRIGGER IF EXISTS media_query_insert; DROP TRIGGER IF EXISTS media_query_update;", transaction)) drop.ExecuteNonQuery();
            InitializeQuerySchema(transaction, schemaVersion < 7);
            using (var health = Command("CREATE TABLE IF NOT EXISTS media_thumbnail_state(media_id TEXT PRIMARY KEY REFERENCES media(id) ON DELETE CASCADE, signature TEXT NOT NULL, available INTEGER NOT NULL); CREATE INDEX IF NOT EXISTS thumbnail_available ON media_thumbnail_state(available,media_id);", transaction)) health.ExecuteNonQuery();
            InitializeSearchIndexSchema(transaction);
            InitializeHistorySchema(transaction);
            InitializeMigrationSchema(transaction);
            InitializeHistoricalSchema(transaction);
            InitializeLegacyParserSchema(transaction);
            InitializeTagAssetSchema(transaction);
            InitializeHistoricalParserExecutionSchema(transaction);
            InitializeLegacyCombineListSchema(transaction);
            InitializeHistoricalLocalSettingsSchema(transaction);
            if (schemaVersion is > 0 and < 15) MoveBinariesIntoFiles(transaction, progress, token);
            InitializeAssetSchema(transaction);
            if (schemaVersion is > 0 and < 16) CompactDocuments(transaction, progress, token);
            token.ThrowIfCancellationRequested();
            using (var version = Command($"PRAGMA user_version={SchemaVersion}", transaction)) version.ExecuteNonQuery();
            transaction.Commit();
        }
        catch (Exception error)
        {
            connection.Dispose(); assets?.Dispose(); this.key?.Dispose();
            // The catalog is unchanged, so the copy made for the change is not needed.
            if (error is OperationCanceledException && MigrationBackupPath is { } copy)
                foreach (var file in new[] { copy, CatalogEncryption.KeyFilePath(copy) }) try { File.Delete(file); } catch (IOException) { }
            throw;
        }
        // The freed space is given back later ("Compact catalog"); the start offers it, so opening does not wait for it.
        UpgradedOnOpen = schemaVersion is > 0 and < SchemaVersion;
        if (UpgradedOnOpen)
            UpdateMaintenanceLog(log => log with { LastUpgradeAt = DateTimeOffset.Now, LastUpgradeFrom = schemaVersion, LastUpgradeTo = SchemaVersion, LastUpgradeSeconds = Math.Round(watch.Elapsed.TotalSeconds, 1) });
    }

    public IReadOnlyList<MediaItem> Search(string? text = null, string? tag = null, int minimumRating = 0,
        bool? isFavorite = null, bool? watchLater = null, string? category = null, string? tagCategory = null)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return QueryItems(new MediaQuery { Text = text, Tag = tag, MinimumRating = minimumRating,
                IsFavorite = isFavorite, WatchLater = watchLater, Category = category, TagCategory = tagCategory },
                null, CancellationToken.None);
        }
    }

    public IReadOnlyList<string> GetTags() => GetFacets().Tags;

    public MediaItem Upsert(MediaItem item)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            // Edits such as rating or favourite keep the browsing caches; see CarryCachesAcross.
            var carry = item is not null && (cachedFacets is not null || cachedTagStatistics is not null);
            var before = carry ? ChangeStamp() : default;
            var old = carry ? ReadMediaDocument(item!.Id, null) : null;
            using var transaction = connection.BeginTransaction();
            var result = UpsertCore(item!, transaction);
            transaction.Commit();
            if (old != null && old.Id == result.Id) CarryCachesAcross(before, [(old, result)]);
            return result;
        }
    }

    internal MediaItem? FindPath(string path)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            // A further version of a medium counts as cataloged with it (P6).
            using var command = Command("SELECT document FROM media WHERE path_key=$path", null, ("$path", PathKey(path)));
            return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<MediaItem>(json, Json) : FindVersionOwner(PathKey(path));
        }
    }

    internal bool AddScannedItem(MediaItem item)
    {
        lock (gate)
        {
            if (FindPath(item.Path) is not null) return false;
            Upsert(item);
            return true;
        }
    }

    public void Delete(Guid id)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            using (var command = Command("DELETE FROM media WHERE id=$id", transaction, ("$id", id.ToString()))) command.ExecuteNonQuery();
            cachedMedia = null;
            foreach (var playlist in ReadDocuments<Playlist>("playlists", transaction))
                if (playlist.MediaIds.Contains(id)) SavePlaylist(playlist with
                {
                    MediaIds = playlist.MediaIds.Where(x => x != id).ToList(),
                    Entries = playlist.Entries.Where(x => x.MediaId != id).ToList()
                }, transaction);
            transaction.Commit();
            FlushReleasedAssets();
        }
    }

    public IReadOnlyList<Playlist> GetPlaylists()
    {
        lock (gate) { ThrowIfDisposed(); return ReadDocuments<Playlist>("playlists").OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(); }
    }

    public Playlist UpsertPlaylist(Playlist playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        if (playlist.Id == Guid.Empty || string.IsNullOrWhiteSpace(playlist.Name) || playlist.MediaIds is null || playlist.Entries is null || playlist.Extra is null ||
            !Enum.IsDefined(playlist.Kind) || !double.IsFinite(playlist.SlideSeconds) || playlist.SlideSeconds <= 0)
            throw new ArgumentException("A playlist needs an id, name, items and positive slide duration.", nameof(playlist));
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            EnsureMediaExist(playlist.MediaIds, transaction);
            ValidatePlaylistEntries(playlist, transaction);
            EnsureCollectionPlacement(playlist, transaction);
            var saved = playlist with { Name = playlist.Name.Trim(), MediaIds = playlist.MediaIds.ToList() };
            SavePlaylist(saved, transaction);
            transaction.Commit();
            return saved;
        }
    }

    public void DeletePlaylist(Guid id) => DeleteDocument("playlists", id);

    public IReadOnlyList<Scene> GetScenes(Guid? mediaId = null)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return ReadDocuments<Scene>("scenes").Where(x => mediaId is null || x.MediaId == mediaId)
                .OrderBy(x => x.StartSeconds).ToArray();
        }
    }

    public Scene UpsertScene(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.Id == Guid.Empty || scene.MediaId == Guid.Empty || scene.Title is null || !double.IsFinite(scene.StartSeconds) ||
            !double.IsFinite(scene.EndSeconds) || scene.StartSeconds < 0 || scene.EndSeconds <= scene.StartSeconds)
            throw new ArgumentException("A scene needs a media item and a finite end after its nonnegative start.", nameof(scene));
        scene = scene with { Tags = NormalizeSceneTags(scene.Tags) };
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            EnsureMediaExist([scene.MediaId], transaction);
            EnsurePreviewOwnership(scene.MediaId, scene.PreviewAssetId, transaction);
            SaveScene(scene, transaction);
            transaction.Commit();
            return scene;
        }
    }

    public void DeleteScene(Guid id)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            foreach (var playlist in ReadDocuments<Playlist>("playlists", transaction).Where(x => x.Entries.Any(e => e.SceneId == id)))
            {
                var entries = playlist.Entries.Where(x => x.SceneId != id).ToList();
                SavePlaylist(playlist with { Entries = entries, MediaIds = entries.Select(x => x.MediaId).ToList() }, transaction);
            }
            using var command = Command("DELETE FROM scenes WHERE id=$id", transaction, ("$id", id.ToString()));
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    public void Export(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        FileAccessPolicy.Current.EnsureWritable(fullPath);
        if (new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm" }.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Export destination must differ from the database files.", nameof(path));
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            lock (gate)
            {
                ThrowIfDisposed();
                using var transaction = connection.BeginTransaction();
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    // Exports of an encrypted library stay encrypted and open with its password or recovery key.
                    using (var stream = key is null ? new NonClosingStream(file) : EncryptedContainer.Create(file, EncryptedContainer.PortableContent, CatalogEncryption.ReadSlots(DatabasePath), key))
                    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("format", "flixypix-catalog"); writer.WriteNumber("version", 4);
                        WriteDocuments("items", "media"); WriteDocuments("playlists", "playlists"); WriteDocuments("scenes", "scenes");
                        WriteDocuments("savedSearches", "saved_searches"); WriteDocuments("filenameTagRules", "filename_rules");
                        WriteDocuments("bookmarks", "bookmarks"); WriteDocuments("mediaAssets", "media_assets", withData: true); WriteDocuments("collectionSets", "collection_sets");
                        writer.WritePropertyName("preferences"); JsonSerializer.Serialize(writer, ReadPreferences(transaction), Json);
                        writer.WriteStartArray("legacyRecords");
                        using (var command = Command("SELECT record FROM legacy_records ORDER BY id", transaction))
                        using (var reader = command.ExecuteReader())
                            while (reader.Read()) writer.WriteStringValue(reader.GetString(0));
                        writer.WriteEndArray(); WriteMigrationPortable(writer,transaction); writer.WriteEndObject(); writer.Flush();

                        void WriteDocuments(string property, string table, bool withData = false)
                        {
                            writer.WriteStartArray(property);
                            using var command = Command($"SELECT document FROM {table} ORDER BY rowid", transaction);
                            using var reader = command.ExecuteReader();
                            // The portable format carries picture bytes inline.
                            while (reader.Read()) if (withData) WriteWithData(writer, reader.GetString(0)); else writer.WriteRawValue(reader.GetString(0));
                            writer.WriteEndArray();
                        }
                    }
                    file.Flush(flushToDisk: true);
                }
                transaction.Commit();
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public const int MaximumPortableEntityBytes = 64 * 1024 * 1024;
    public ImportResult ImportJson(string path) => ImportJson(path, MaximumPortableEntityBytes);
    public ImportResult ImportJson(string path, CancellationToken cancellationToken) => ImportJson(path, MaximumPortableEntityBytes, cancellationToken);

    /// <summary>Reads one bounded entity at a time into disk-backed staging, then atomically projects all sections.</summary>
    /// <param name="secret">Password or recovery key, needed only for encrypted exports.</param>
    public ImportResult ImportJson(string path, int maximumEntityBytes, CancellationToken cancellationToken = default, string? secret = null)
    {
        if (maximumEntityBytes is < 1024 or > MaximumPortableEntityBytes) throw new ArgumentOutOfRangeException(nameof(maximumEntityBytes), "Entity limit must be between 1024 bytes and 64 MiB.");
        lock (gate)
        {
            ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
            using (var configure = Command("PRAGMA temp_store=FILE", null)) configure.ExecuteNonQuery();
            InitializeAssetSchema(null); // changing temp_store drops the temporary tables and triggers
            using var transaction = connection.BeginTransaction();
            var errors = new List<string>();
            var mediaCount = 0;
            try
            {
                StagePortable(path, maximumEntityBytes, transaction, cancellationToken, secret);
                using (var count = Command("SELECT count(*) FROM temp.portable_stage WHERE section='items'", transaction)) mediaCount = Convert.ToInt32(count.ExecuteScalar());
                var imported = 0;
                var skipped = 0;
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var item in ReadStagedPortable<MediaItem>("items", transaction, cancellationToken))
                {
                    try
                    {
                        var saved = UpsertCore(item, transaction);
                        SavePortableIdMap(item.Id, saved.Id, transaction);
                        imported++;
                    }
                    catch (Exception ex) when (ex is ArgumentException or SqliteException)
                    { AddPortableError(errors, $"Media {item?.Path ?? "<null>"}: {ex.Message}"); skipped++; }
                }
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var scene in ReadStagedPortable<Scene>("scenes", transaction, cancellationToken))
                {
                    try
                    {
                        if (scene is null || scene.Id == Guid.Empty || scene.MediaId == Guid.Empty || scene.Title is null ||
                            !double.IsFinite(scene.StartSeconds) || !double.IsFinite(scene.EndSeconds) || scene.StartSeconds < 0 || scene.EndSeconds <= scene.StartSeconds)
                            throw new ArgumentException("Invalid scene range or identity.");
                        var mapped = scene with { MediaId = MapPortableId(scene.MediaId, transaction), Tags = NormalizeSceneTags(scene.Tags) };
                        EnsureMediaExist([mapped.MediaId], transaction);
                        SaveScene(mapped, transaction);
                    }
                    catch (Exception ex) when (ex is ArgumentException or SqliteException) { AddPortableError(errors, $"Scene: {ex.Message}"); skipped++; }
                }
                cancellationToken.ThrowIfCancellationRequested();
                ImportStagedCollectionSets(errors, ref skipped, transaction, cancellationToken);
                foreach (var playlist in ReadStagedPortable<Playlist>("playlists", transaction, cancellationToken))
                {
                    try
                    {
                        if (playlist is null || playlist.MediaIds is null || playlist.Entries is null || playlist.Extra is null || string.IsNullOrWhiteSpace(playlist.Name) || playlist.Id == Guid.Empty ||
                            !Enum.IsDefined(playlist.Kind) || !double.IsFinite(playlist.SlideSeconds) || playlist.SlideSeconds <= 0)
                            throw new ArgumentException("Invalid playlist.");
                        if (playlist.Entries.Any(x => x is null)) throw new ArgumentException("Playlist has a null entry.");
                        var mapped = playlist with
                        {
                            MediaIds = playlist.MediaIds.Select(x => MapPortableId(x, transaction)).ToList(),
                            Entries = playlist.Entries.Select(x => x with { MediaId = MapPortableId(x.MediaId, transaction) }).ToList()
                        };
                        EnsureMediaExist(mapped.MediaIds, transaction);
                        ValidatePlaylistEntries(mapped, transaction);
                        SavePlaylist(PortablePlacement(mapped, transaction), transaction);
                    }
                    catch (Exception ex) when (ex is ArgumentException or SqliteException) { AddPortableError(errors, $"Playlist: {ex.Message}"); skipped++; }
                }
                ImportStagedPortableLegacy(transaction, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var saved in ReadStagedPortable<SavedSearch>("savedsearches", transaction, cancellationToken))
                {
                    try { ValidateSavedSearch(saved); SaveDocument("saved_searches", saved.Id, saved with { ParentId = ExistingSet(saved.ParentId, transaction) }, transaction); }
                    catch (ArgumentException ex) { AddPortableError(errors, $"Saved search: {ex.Message}"); skipped++; }
                }
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var rule in ReadStagedPortable<FilenameTagRule>("filenametagrules", transaction, cancellationToken))
                {
                    try { var validated = ValidateFilenameRule(rule); SaveDocument("filename_rules", validated.Id, validated, transaction); }
                    catch (ArgumentException ex) { AddPortableError(errors, $"Filename rule: {ex.Message}"); skipped++; }
                }
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var bookmark in ReadStagedPortable<Bookmark>("bookmarks", transaction, cancellationToken))
                {
                    try
                    {
                        var mapped = ValidateBookmark(bookmark) with { MediaId = MapPortableId(bookmark.MediaId, transaction) };
                        EnsureMediaExist([mapped.MediaId], transaction);
                        SaveBookmark(mapped, transaction);
                    }
                    catch (ArgumentException ex) { AddPortableError(errors, $"Bookmark: {ex.Message}"); skipped++; }
                }
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var asset in ReadStagedPortable<MediaAsset>("mediaassets", transaction, cancellationToken))
                {
                    try
                    {
                        ValidateAsset(asset);
                        var mapped = asset with { MediaId = MapPortableId(asset.MediaId, transaction) };
                        EnsureMediaExist([mapped.MediaId], transaction); SaveAsset(mapped, transaction);
                    }
                    catch (ArgumentException error) { AddPortableError(errors, $"Media asset: {error.Message}"); skipped++; }
                }
                ImportMigrationPortable(transaction, cancellationToken);
                ValidatePortablePreviewReferences(transaction, cancellationToken);
                var preferences = ReadStagedPortable<CatalogPreferences>("preferences", transaction, cancellationToken).SingleOrDefault();
                if (preferences is not null)
                {
                    try { ValidatePreferences(preferences); SaveDocument("preferences", Guid.Empty, preferences, transaction); }
                    catch (ArgumentException ex) { AddPortableError(errors, $"Preferences: {ex.Message}"); skipped++; }
                }
                if (errors.Count != 0)
                {
                    transaction.Rollback(); cachedMedia = null;
                    return new ImportResult { Imported = 0, Skipped = mediaCount, Errors = errors,
                        Warnings = ["Import rolled back: no catalog changes were retained."] };
                }
                using (var drop = Command("DROP TABLE temp.portable_stage; DROP TABLE temp.portable_id_map; DROP TABLE temp.portable_legacy_rows; DROP TABLE temp.portable_legacy_batches;", transaction)) drop.ExecuteNonQuery();
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
                return new ImportResult { Imported = imported, Skipped = skipped, Errors = errors };
            }
            catch (OperationCanceledException)
            {
                transaction.Rollback(); cachedMedia = null; throw;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or SqliteException or OverflowException)
            {
                transaction.Rollback(); cachedMedia = null;
                if (errors.Count < 100) errors.Add(error.Message);
                return new ImportResult { Imported = 0, Skipped = mediaCount, Errors = errors,
                    Warnings = ["Import rolled back: no catalog changes were retained."] };
            }
        }
    }

    private MediaItem UpsertCore(MediaItem item, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.Path) || item.Title is null || !Enum.IsDefined(item.Kind) ||
            item.Tags is null || item.TagCategories is null || item.TagCategories.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Value is null) ||
            item.Extra is null || item.PlayCount is <0 || item.ViewCount is <0 || item.OCount is <0 || item.WatchCount is <0 || item.Rating is < 0 or > 10 || item.FileSize is < 0 || item.Width is <= 0 || item.Height is <= 0 ||
            item.DurationSeconds is { } duration && (!double.IsFinite(duration) || duration < 0) ||
            !double.IsFinite(item.PlaybackPositionSeconds) || item.PlaybackPositionSeconds < 0)
            throw new ArgumentException("Invalid media id, path, metadata, rating (0–10), or playback position.", nameof(item));
        var pathKey = PathKey(item.Path);
        using var lookup = Command("SELECT id FROM media WHERE path_key=$path", transaction, ("$path", pathKey));
        var existingId = lookup.ExecuteScalar() as string;
        if (existingId is not null && existingId != item.Id.ToString())
        {
            using var ownId = Command("SELECT 1 FROM media WHERE id=$id", transaction, ("$id", item.Id.ToString()));
            if (ownId.ExecuteScalar() is not null) throw new ArgumentException("Another catalog item already uses this path; an existing item cannot be silently merged.", nameof(item));
        }
        var categories = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, tags) in item.TagCategories)
        {
            var existing = categories.GetValueOrDefault(name.Trim()) ?? [];
            categories[name.Trim()] = existing.Concat(tags).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        var saved = item with
        {
            Id = existingId is null ? item.Id : Guid.Parse(existingId),
            Tags = item.Tags.Concat(categories.Values.SelectMany(x => x)).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            TagCategories = categories,
            Category = string.IsNullOrWhiteSpace(item.Category) ? null : item.Category.Trim(),
            Extra = new Dictionary<string, string?>(item.Extra)
        };
        if(legacySession!=null)
        {
            using var existingDocument=Command("SELECT document FROM media WHERE id=$id",transaction,("$id",saved.Id.ToString()));
            if(existingDocument.ExecuteScalar() is string document)saved=saved with { AddedAt=JsonSerializer.Deserialize<MediaItem>(document,Json)!.AddedAt };
        }
        var json = JsonSerializer.Serialize(saved, MediaJson);
        if(KeepLegacyExisting("media",saved.Id.ToString(),json,transaction))
        {using var original=Command("SELECT document FROM media WHERE id=$id",transaction,("$id",saved.Id.ToString()));return JsonSerializer.Deserialize<MediaItem>((string)original.ExecuteScalar()!,Json)!;}
        using var command = Command("INSERT INTO media(id,path_key,document) VALUES($id,$path,$json) ON CONFLICT(id) DO UPDATE SET path_key=excluded.path_key,document=excluded.document",
            transaction, ("$id", saved.Id.ToString()), ("$path", pathKey), ("$json", json));
        command.ExecuteNonQuery();
        SyncVersionPaths(saved, pathKey, transaction);
        cachedMedia = null;
        return saved;
    }

    private void SavePlaylist(Playlist playlist, SqliteTransaction transaction)
    {
        if(KeepLegacyExisting("playlists",playlist.Id.ToString(),JsonSerializer.Serialize(playlist,Json),transaction))return;
        using var command = Command("INSERT INTO playlists(id,document) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET document=excluded.document",
            transaction, ("$id", playlist.Id.ToString()), ("$json", JsonSerializer.Serialize(playlist, Json)));
        command.ExecuteNonQuery();
    }

    private void SaveScene(Scene scene, SqliteTransaction transaction)
    {
        if(KeepLegacyExisting("scenes",scene.Id.ToString(),JsonSerializer.Serialize(scene,Json),transaction))return;
        using var command = Command("INSERT INTO scenes(id,media_id,document) VALUES($id,$media,$json) ON CONFLICT(id) DO UPDATE SET media_id=excluded.media_id,document=excluded.document",
            transaction, ("$id", scene.Id.ToString()), ("$media", scene.MediaId.ToString()), ("$json", JsonSerializer.Serialize(scene, Json)));
        command.ExecuteNonQuery();
    }

    private void EnsureMediaExist(IEnumerable<Guid> ids, SqliteTransaction transaction)
    {
        foreach (var id in ids.Distinct())
        {
            using var command = Command("SELECT 1 FROM media WHERE id=$id", transaction, ("$id", id.ToString()));
            if (command.ExecuteScalar() is null) throw new ArgumentException($"Media item {id} does not exist.");
        }
    }

    private void DeleteDocument(string table, Guid id)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = Command($"DELETE FROM {table} WHERE id=$id", null, ("$id", id.ToString()));
            command.ExecuteNonQuery();
        }
    }

    private List<T> ReadDocuments<T>(string table, SqliteTransaction? transaction = null)
    {
        using var command = Command($"SELECT document FROM {table} ORDER BY rowid", transaction);
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json) ?? throw new InvalidDataException($"Invalid {table} record."));
        return result;
    }

    private IReadOnlyList<MediaItem> ReadMediaCached()
    {
        using var version = Command("PRAGMA data_version", null);
        var currentVersion = Convert.ToInt64(version.ExecuteScalar());
        if (cachedMedia is null || cachedDataVersion != currentVersion)
        {
            cachedMedia = ReadDocuments<MediaItem>("media");
            cachedDataVersion = currentVersion;
        }
        return cachedMedia;
    }

    private static MediaItem CloneItem(MediaItem item) => item with
    {
        Tags = item.Tags.ToList(), Extra = new Dictionary<string, string?>(item.Extra),
        TagCategories = item.TagCategories.ToDictionary(x => x.Key, x => x.Value.ToList(), StringComparer.OrdinalIgnoreCase)
    };

    private List<string> ReadLegacyRecords(SqliteTransaction? transaction = null)
    {
        using var command = Command("SELECT record FROM legacy_records ORDER BY id", transaction);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private void SaveLegacyRecords(IReadOnlyList<string> records, SqliteTransaction transaction)
    {
        var group = new List<string>();
        foreach (var raw in records)
        {
            using var parsed = JsonDocument.Parse(raw);
            var type = Text(parsed.RootElement, "type");
            if (type == "header" && group.Count != 0) { SaveGroup(group); group.Clear(); }
            group.Add(raw);
            if (type == "footer") { SaveGroup(group); group.Clear(); }
        }
        if (group.Count != 0) SaveGroup(group);

        void SaveGroup(List<string> lines)
        {
            var batch = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(lines))));
            for (var ordinal = 0; ordinal < lines.Count; ordinal++)
            {
                using var command = Command("INSERT OR IGNORE INTO legacy_records(batch,ordinal,record) VALUES($batch,$ordinal,$raw)", transaction,
                    ("$batch", batch), ("$ordinal", ordinal), ("$raw", lines[ordinal]));
                command.ExecuteNonQuery();
            }
        }
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    // Windows paths are case-insensitive; retain their original spelling in the document.
    internal static string PathKey(string path) => System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar).ToUpperInvariant();
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    /// <summary>Rebuilds the catalog file without unused pages (original "Compact database") and removes picture files
    /// nothing uses; data stays the same.
    /// Returns the size of the catalog file before and after.</summary>
    public (long Before, long After) Compact()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            long Size() => new FileInfo(DatabasePath).Length + (File.Exists(DatabasePath + "-wal") ? new FileInfo(DatabasePath + "-wal").Length : 0);
            var before = Size();
            using (var command = Command("PRAGMA wal_checkpoint(TRUNCATE); VACUUM; PRAGMA wal_checkpoint(TRUNCATE);", null)) command.ExecuteNonQuery();
            RemoveUnusedAssetFiles();
            return (before, Size());
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            try { FlushReleasedAssets(); } catch (SqliteException) { }
            connection.Dispose(); assets.Dispose(); key?.Dispose(); disposed = true;
        }
    }
}
