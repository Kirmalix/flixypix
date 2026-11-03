using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed record WallPage(IReadOnlyList<WallMark> Items, long TotalCount);

public sealed partial class CatalogRepository
{
    private sealed record WallTextResult(string Text, System.Globalization.CompareInfo Culture, (long, long) Stamp, bool All, string? Media, string? Marks, string? Candidates);
    private WallTextResult? wallTextResult;

    private void InitializeWallSchema(SqliteTransaction transaction, bool rebuild)
    {
        // ISO DateTimeOffset seconds plus all seven fractional digits; unlike julianday this preserves tick ordering.
        const string date = "json_extract(NEW.document,'$.addedAt')";
        var fraction = $"substr({date},21)";
        var digits = $"length({fraction})-length(ltrim({fraction},'0123456789'))";
        var added = $"CAST(strftime('%s',substr({date},1,19)||CASE WHEN substr({date},-1)='Z' THEN 'Z' ELSE substr({date},-6) END) AS INTEGER)*10000000+CASE WHEN substr({date},20,1)='.' THEN CAST(substr(substr({fraction},1,{digits})||'0000000',1,7) AS INTEGER) ELSE 0 END";
        using var create = Command($"""
            CREATE TABLE IF NOT EXISTS wall_media(
                id TEXT PRIMARY KEY REFERENCES media(id) ON DELETE CASCADE,
                title TEXT NOT NULL, description TEXT, added INTEGER NOT NULL, kind TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS wall_media_page ON wall_media(added DESC,id);
            CREATE TABLE IF NOT EXISTS wall_marks(
                kind INTEGER NOT NULL,id TEXT NOT NULL,media_id TEXT NOT NULL REFERENCES media(id) ON DELETE CASCADE,
                title TEXT NOT NULL,description TEXT,start REAL NOT NULL,end REAL,preview TEXT,tags TEXT NOT NULL,
                PRIMARY KEY(kind,id));
            CREATE INDEX IF NOT EXISTS wall_marks_media_page ON wall_marks(media_id,start,id,kind);
            CREATE TABLE IF NOT EXISTS wall_tags(
                kind INTEGER NOT NULL,id TEXT NOT NULL,tag TEXT NOT NULL,
                FOREIGN KEY(kind,id) REFERENCES wall_marks(kind,id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS wall_tags_mark ON wall_tags(kind,id,tag);
            CREATE INDEX IF NOT EXISTS wall_tags_tag ON wall_tags(tag COLLATE EFP_ORDINAL,kind,id);
            CREATE INDEX IF NOT EXISTS wall_tags_spelling ON wall_tags(tag,kind,id);
            CREATE INDEX IF NOT EXISTS wall_media_tag_spelling ON media_tags(category,tag,media_id);
            CREATE TRIGGER IF NOT EXISTS wall_media_insert AFTER INSERT ON media BEGIN
                INSERT INTO wall_media VALUES(NEW.id,coalesce(json_extract(NEW.document,'$.title'),''),json_extract(NEW.document,'$.description'),{added},
                CASE json_extract(NEW.document,'$.kind') WHEN 0 THEN 'Image' WHEN 1 THEN 'Video' WHEN 2 THEN 'Audio' ELSE json_extract(NEW.document,'$.kind') END);
            END;
            CREATE TRIGGER IF NOT EXISTS wall_media_update AFTER UPDATE OF document ON media BEGIN
                DELETE FROM wall_media WHERE id=OLD.id;
                INSERT INTO wall_media VALUES(NEW.id,coalesce(json_extract(NEW.document,'$.title'),''),json_extract(NEW.document,'$.description'),{added},
                CASE json_extract(NEW.document,'$.kind') WHEN 0 THEN 'Image' WHEN 1 THEN 'Video' WHEN 2 THEN 'Audio' ELSE json_extract(NEW.document,'$.kind') END);
                UPDATE scenes SET document=document WHERE media_id=NEW.id AND json_extract(OLD.document,'$.kind') IS NOT json_extract(NEW.document,'$.kind');
                UPDATE bookmarks SET document=document WHERE media_id=NEW.id AND json_extract(OLD.document,'$.kind') IS NOT json_extract(NEW.document,'$.kind');
            END;
            """, transaction);
        create.ExecuteNonQuery();
        foreach (var (table, kind, start, end) in new[] { ("scenes", 0, "startSeconds", "json_extract(NEW.document,'$.endSeconds')"), ("bookmarks", 1, "positionSeconds", "NULL") })
        {
            var insert = $"""
                INSERT INTO wall_marks SELECT {kind},NEW.id,NEW.media_id,coalesce(json_extract(NEW.document,'$.title'),''),json_extract(NEW.document,'$.description'),
                    coalesce(json_extract(NEW.document,'$.{start}'),0),{end},json_extract(NEW.document,'$.previewAssetId'),coalesce(json_extract(NEW.document,'$.tags'),'[]')
                    WHERE EXISTS(SELECT 1 FROM wall_media WHERE id=NEW.media_id AND kind='Video');
                INSERT INTO wall_tags SELECT {kind},NEW.id,value FROM json_each(NEW.document,'$.tags')
                    WHERE EXISTS(SELECT 1 FROM wall_marks WHERE kind={kind} AND id=NEW.id);
                """;
            using var triggers = Command($"""
                CREATE TRIGGER IF NOT EXISTS wall_{table}_insert AFTER INSERT ON {table} BEGIN {insert} END;
                CREATE TRIGGER IF NOT EXISTS wall_{table}_update AFTER UPDATE OF document ON {table} BEGIN
                    DELETE FROM wall_marks WHERE kind={kind} AND id=OLD.id; {insert}
                END;
                CREATE TRIGGER IF NOT EXISTS wall_{table}_delete AFTER DELETE ON {table} BEGIN
                    DELETE FROM wall_marks WHERE kind={kind} AND id=OLD.id;
                END;
                """, transaction);
            triggers.ExecuteNonQuery();
        }
        if (rebuild)
        {
            using var populate = Command("DELETE FROM wall_marks; DELETE FROM wall_media; UPDATE media SET document=document; UPDATE scenes SET document=document; UPDATE bookmarks SET document=document;", transaction);
            populate.ExecuteNonQuery();
        }
    }

    public WallPage QueryWallPage(WallQuery query, int offset = 0, int limit = 120, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
        lock (gate)
        {
            ThrowIfDisposed();
            var culture = CultureInfo.CurrentCulture;
            connection.CreateFunction<string?, string?, bool>("efp_wall_contains", (value, text) => value?.Contains(text ?? "", StringComparison.CurrentCultureIgnoreCase) == true);
            var parameters = new List<(string, object)>();
            var conditions = new List<string> { "m.kind='Video'" };
            if (!query.Scenes) conditions.Add("w.kind<>0");
            if (!query.Bookmarks) conditions.Add("w.kind<>1");
            if (!string.IsNullOrWhiteSpace(query.Text))
            {
                conditions.Add("(efp_wall_contains(w.title,$text) OR efp_wall_contains(w.description,$text) OR efp_wall_contains(m.title,$text) OR efp_wall_contains(m.description,$text))");
                parameters.Add(("$text", query.Text.Trim()));
            }
            var tags = (query.Tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
            // Resolve culture-sensitive equality once per distinct spelling, then use binary indexed membership.
            // EFP_ORDINAL alone would change Turkish and other culture-dependent comparisons.
            using var transaction = connection.BeginTransaction();
            string[] Spellings(string sql)
            {
                using var command = Command(sql, transaction); using var cancel = cancellationToken.Register(command.Cancel);
                using var reader = command.ExecuteReader(); var result = new List<string>();
                while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); result.Add(reader.GetString(0)); }
                return result.ToArray();
            }
            if (tags.Length == 0 && !string.IsNullOrWhiteSpace(query.Text))
            {
                var text = query.Text.Trim();
                using var changes = Command("SELECT total_changes()", transaction); using var version = Command("PRAGMA data_version", transaction);
                var stamp = (Convert.ToInt64(changes.ExecuteScalar()), Convert.ToInt64(version.ExecuteScalar()));
                var result = wallTextResult;
                if (result == null || result.Text != text || !result.Culture.Equals(culture.CompareInfo) || result.Stamp != stamp)
                {
                    var mediaHits = new HashSet<string>(StringComparer.Ordinal); var mediaCount = 0;
                    bool Matches(string? value) => value != null && culture.CompareInfo.IndexOf(value, text, CompareOptions.IgnoreCase) >= 0;
                    using (var command = Command("SELECT id,title,description FROM wall_media WHERE kind='Video'", transaction))
                    {
                        using var cancel = cancellationToken.Register(command.Cancel); using var reader = command.ExecuteReader();
                        while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); mediaCount++; if (Matches(reader.GetString(1)) || !reader.IsDBNull(2) && Matches(reader.GetString(2))) mediaHits.Add(reader.GetString(0)); }
                    }
                    var all = mediaHits.Count == mediaCount; var markHits = new List<object[]>(); var candidates = new HashSet<string>(mediaHits, StringComparer.Ordinal);
                    if (!all)
                    using (var command = Command("SELECT kind,id,media_id,title,description FROM wall_marks", transaction))
                    {
                        using var cancel = cancellationToken.Register(command.Cancel); using var reader = command.ExecuteReader();
                        while (reader.Read())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (mediaHits.Count > 0 && mediaHits.Contains(reader.GetString(2))) continue;
                            if (Matches(reader.GetString(3)) || !reader.IsDBNull(4) && Matches(reader.GetString(4))) { markHits.Add([reader.GetInt32(0), reader.GetString(1)]); candidates.Add(reader.GetString(2)); }
                        }
                    }
                    result = new(text, culture.CompareInfo, stamp, all,
                        !all && mediaHits.Count > 0 ? JsonSerializer.Serialize(mediaHits) : null,
                        markHits.Count > 0 ? JsonSerializer.Serialize(markHits) : null,
                        !all && candidates.Count > 0 ? JsonSerializer.Serialize(candidates) : null);
                    cancellationToken.ThrowIfCancellationRequested();
                    // Retain one search only, bounded to 16 MiB of UTF-16 strings; never cache complete documents.
                    var characters = (result.Media?.Length ?? 0L) + (result.Marks?.Length ?? 0L) + (result.Candidates?.Length ?? 0L);
                    wallTextResult = characters <= 8 * 1024 * 1024 ? result : null;
                }
                conditions.RemoveAt(conditions.Count - 1); parameters.Clear();
                if (!result.All)
                {
                    if (result.Candidates == null) return new([], 0);
                    parameters.Add(("$textCandidates", result.Candidates)); conditions.Add("m.id IN(SELECT value FROM json_each($textCandidates))");
                    var alternatives = new List<string>();
                    if (result.Media != null) { parameters.Add(("$textMedia", result.Media)); alternatives.Add("m.id IN(SELECT value FROM json_each($textMedia))"); }
                    if (result.Marks != null) { parameters.Add(("$textMarks", result.Marks)); alternatives.Add("(w.kind,w.id) IN(SELECT json_extract(value,'$[0]'),json_extract(value,'$[1]') FROM json_each($textMarks))"); }
                    conditions.Add("(" + string.Join(" OR ", alternatives) + ")");
                }
            }
            var markSpellings = tags.Length == 0 ? [] : Spellings("SELECT DISTINCT tag FROM wall_tags INDEXED BY wall_tags_spelling");
            var mediaSpellings = tags.Length == 0 ? [] : Spellings("SELECT DISTINCT tag FROM media_tags INDEXED BY wall_media_tag_spelling WHERE category=''");
            foreach (var tag in tags)
            {
                var onMark = markSpellings.Where(value => culture.CompareInfo.Compare(value, tag, CompareOptions.IgnoreCase) == 0).ToArray();
                var onMedia = mediaSpellings.Where(value => culture.CompareInfo.Compare(value, tag, CompareOptions.IgnoreCase) == 0).ToArray();
                if (onMark.Length + onMedia.Length == 0) return new([], 0);
                var name = "$tag" + parameters.Count;
                var alternatives = new List<string>();
                if (onMark.Length > 0)
                {
                    parameters.Add((name + "mark", JsonSerializer.Serialize(onMark)));
                    alternatives.Add($"(w.kind,w.id) IN(SELECT kind,id FROM wall_tags INDEXED BY wall_tags_spelling WHERE tag IN(SELECT value FROM json_each({name}mark)))");
                }
                if (onMedia.Length > 0)
                {
                    parameters.Add((name + "media", JsonSerializer.Serialize(onMedia)));
                    alternatives.Add($"m.id IN(SELECT media_id FROM media_tags INDEXED BY wall_media_tag_spelling WHERE category='' AND tag IN(SELECT value FROM json_each({name}media)))");
                }
                conditions.Add("(" + string.Join(" OR ", alternatives) + ")");
            }
            var from = " FROM wall_media m CROSS JOIN wall_marks w ON w.media_id=m.id WHERE " + string.Join(" AND ", conditions);
            long count;
            var countSql = tags.Length == 0 && string.IsNullOrWhiteSpace(query.Text)
                ? "SELECT count(*) FROM wall_marks w WHERE " + (query.Scenes && query.Bookmarks ? "1" : query.Scenes ? "w.kind=0" : query.Bookmarks ? "w.kind=1" : "0")
                : "SELECT count(*)" + from;
            using (var command = Command(countSql, transaction, parameters.ToArray()))
            {
                using var cancel = cancellationToken.Register(command.Cancel);
                count = Convert.ToInt64(command.ExecuteScalar());
            }
            cancellationToken.ThrowIfCancellationRequested();
            parameters.Add(("$offset", offset)); parameters.Add(("$limit", limit));
            using var page = Command("SELECT w.kind,w.id,w.title,w.description,w.start,w.end,w.tags,w.preview,media.document FROM (SELECT w.*,m.added" + from
                + " ORDER BY m.added DESC,m.id,w.start,w.id,w.kind LIMIT $limit OFFSET $offset) w JOIN media ON media.id=w.media_id ORDER BY w.added DESC,w.media_id,w.start,w.id,w.kind", transaction, parameters.ToArray());
            using var cancellation = cancellationToken.Register(page.Cancel);
            var items = new List<WallMark>();
            using (var reader = page.ExecuteReader())
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var media = JsonSerializer.Deserialize<MediaItem>(reader.GetString(8), Json) ?? throw new InvalidDataException("Invalid media record.");
                    items.Add(new((WallMarkKind)reader.GetInt32(0), Guid.Parse(reader.GetString(1)), media, reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.GetDouble(4), reader.IsDBNull(5) ? null : reader.GetDouble(5), JsonSerializer.Deserialize<List<string>>(reader.GetString(6), Json) ?? [], reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7))));
                }
            transaction.Commit(); return new(items, count);
        }
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
    }
}
