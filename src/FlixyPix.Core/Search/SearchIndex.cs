using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>Maps every code point to one fixed member of its OrdinalIgnoreCase class. Ordinal containment of folded texts is
/// then exactly OrdinalIgnoreCase containment, which <c>ToUpperInvariant</c> is not (for example the long s).</summary>
public static class SearchFold
{
    private static readonly Lazy<(char[] Basic, Dictionary<int, int> Supplementary, string Version)> table = new(Build);

    /// <summary>Changes when the runtime's casing tables change; a stored index built with another table is rebuilt.</summary>
    public static string Version => table.Value.Version;

    public static string Fold(string value)
    {
        var (basic, supplementary, _) = table.Value;
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                var point = char.ConvertToUtf32(c, value[++i]);
                result.Append(char.ConvertFromUtf32(supplementary.GetValueOrDefault(point, point)));
            }
            else result.Append(basic[c]);
        }
        return result.ToString();
    }

    private static (char[], Dictionary<int, int>, string) Build()
    {
        var representatives = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Representative(string text) { if (!representatives.TryGetValue(text, out var found)) representatives[text] = found = text; return found; }
        var basic = new char[0x10000];
        // Lone surrogates compare ordinally; they keep their own value.
        for (var c = 0; c < basic.Length; c++) basic[c] = c is >= 0xD800 and <= 0xDFFF ? (char)c : Representative(((char)c).ToString())[0];
        var supplementary = new Dictionary<int, int>();
        for (var point = 0x10000; point <= 0x10FFFF; point++)
        {
            var text = char.ConvertFromUtf32(point);
            var folded = char.ConvertToUtf32(Representative(text), 0);
            if (folded != point) supplementary[point] = folded;
        }
        var digest = new StringBuilder();
        foreach (var c in basic) digest.Append(c);
        foreach (var (from, to) in supplementary.OrderBy(p => p.Key)) digest.Append(from).Append(':').Append(to).Append(';');
        return (basic, supplementary, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digest.ToString())))[..16]);
    }
}

/// <summary>Trigram index over the folded quick-search text. Plain SQL triggers only queue changed rows, so any SQLite writer
/// keeps the queue right; FlixyPix folds and indexes queued rows before a search needs them.</summary>
public sealed partial class CatalogRepository
{
    /// <summary>The trigram index needs at least three characters; shorter terms are compared row by row.</summary>
    internal const int SearchIndexMinimumLength = 3;
    private const int SearchIndexBatch = 5000;
    /// <summary>A search indexes at most this many waiting rows itself; larger backlogs are left to <see cref="PrepareSearchIndex"/>.</summary>
    private const int SearchIndexQueryBatch = 2000;
    /// <summary>With more candidates the row-by-row comparison, which stops after one page, is faster than the index.</summary>
    private const int SearchIndexBroadLimit = 20000;

    private void InitializeSearchIndexSchema(SqliteTransaction transaction)
    {
        // The queue has no unique key: a conflict clause inside a trigger is overridden by the outer statement's, so
        // INSERT OR IGNORE still fails under an upsert. NOT EXISTS keeps the queue free of duplicates instead.
        using var create = Command("""
            CREATE VIRTUAL TABLE IF NOT EXISTS media_search USING fts5(text, tokenize='trigram case_sensitive 1');
            CREATE TABLE IF NOT EXISTS media_search_queue(id INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS media_search_queue_id ON media_search_queue(id);
            CREATE TABLE IF NOT EXISTS media_search_state(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TRIGGER IF NOT EXISTS media_search_added AFTER INSERT ON media_query BEGIN
                INSERT INTO media_search_queue SELECT NEW.rowid WHERE NOT EXISTS(SELECT 1 FROM media_search_queue WHERE id=NEW.rowid);
            END;
            CREATE TRIGGER IF NOT EXISTS media_search_removed AFTER DELETE ON media_query BEGIN
                INSERT INTO media_search_queue SELECT OLD.rowid WHERE NOT EXISTS(SELECT 1 FROM media_search_queue WHERE id=OLD.rowid);
            END;
            CREATE TRIGGER IF NOT EXISTS media_search_changed AFTER UPDATE ON media_query BEGIN
                INSERT INTO media_search_queue SELECT OLD.rowid WHERE NOT EXISTS(SELECT 1 FROM media_search_queue WHERE id=OLD.rowid);
                INSERT INTO media_search_queue SELECT NEW.rowid WHERE NOT EXISTS(SELECT 1 FROM media_search_queue WHERE id=NEW.rowid);
            END;
            """, transaction);
        create.ExecuteNonQuery();
        using var state = Command("SELECT value FROM media_search_state WHERE key='fold'", transaction);
        if (state.ExecuteScalar() as string == SearchFold.Version) return;
        // New index or changed casing tables: queue everything; the work itself happens on first use or in the background.
        using var reset = Command("""
            DELETE FROM media_search; DELETE FROM media_search_queue;
            INSERT INTO media_search_queue SELECT rowid FROM media_query;
            INSERT INTO media_search_state VALUES('fold',$version) ON CONFLICT(key) DO UPDATE SET value=excluded.value;
            """, transaction, ("$version", SearchFold.Version));
        reset.ExecuteNonQuery();
    }

    /// <summary>Indexes queued rows in portions, releasing the catalog between them, so a large first build does not hold up
    /// browsing. Returns the number of rows indexed.</summary>
    public int PrepareSearchIndex(CancellationToken cancellationToken = default)
    {
        var total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                ThrowIfDisposed();
                using var transaction = connection.BeginTransaction();
                var done = SyncSearchIndex(transaction, SearchIndexBatch);
                CommitAfterIndexing(transaction);
                total += done;
                if (done < SearchIndexBatch) return total;
            }
        }
    }

    public int PendingSearchIndexRows()
    {
        lock (gate) { ThrowIfDisposed(); using var count = Command("SELECT count(DISTINCT id) FROM media_search_queue", null); return Convert.ToInt32(count.ExecuteScalar()); }
    }

    private int SyncSearchIndex(SqliteTransaction transaction, int limit)
    {
        using var pending = Command("SELECT s.id,q.search_text FROM (SELECT DISTINCT id FROM media_search_queue ORDER BY id LIMIT $limit) s LEFT JOIN media_query q ON q.rowid=s.id ORDER BY s.id",
            transaction, ("$limit", limit));
        var rows = new List<(long Id, string? Text)>();
        using (var reader = pending.ExecuteReader()) while (reader.Read()) rows.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        if (rows.Count == 0) return 0;
        if (indexing?.Transaction != transaction) { using var changes = Command("SELECT total_changes()", transaction); indexing = (transaction, Convert.ToInt64(changes.ExecuteScalar())); }
        // The portion holds every queued id up to its last one, so both ranges are cleared as a whole.
        var last = rows[^1].Id;
        using (var remove = Command("DELETE FROM media_search WHERE rowid IN (SELECT id FROM media_search_queue WHERE id<=$last)", transaction, ("$last", last))) remove.ExecuteNonQuery();
        using (var insert = Command("INSERT INTO media_search(rowid,text) VALUES($id,$text)", transaction, ("$id", 0L), ("$text", "")))
            foreach (var (id, text) in rows)
            {
                if (text is null) continue;
                insert.Parameters["$id"].Value = id; insert.Parameters["$text"].Value = SearchFold.Fold(text); insert.ExecuteNonQuery();
            }
        using (var dequeue = Command("DELETE FROM media_search_queue WHERE id<=$last", transaction, ("$last", last))) dequeue.ExecuteNonQuery();
        return rows.Count;
    }

    // Transaction that indexed for the quick search, with total_changes() before its first portion.
    private (SqliteTransaction Transaction, long ChangesBefore)? indexing;

    /// <summary>Commits a transaction whose only writes may be quick-search indexing. Indexing changes no media, so facets and
    /// tag statistics that were current stay current. FTS5 writes its pending data during the commit, hence the count after it.</summary>
    private void CommitAfterIndexing(SqliteTransaction transaction)
    {
        transaction.Commit();
        if (indexing is not { } run || run.Transaction != transaction) return;
        indexing = null;
        using var changes = Command("SELECT total_changes()", null); var after = Convert.ToInt64(changes.ExecuteScalar());
        if (cachedFacets is not null && cachedFacetStamp.Changes == run.ChangesBefore) cachedFacetStamp = cachedFacetStamp with { Changes = after };
        if (cachedTagStatistics is not null && cachedTagStatisticsStamp.Changes == run.ChangesBefore) cachedTagStatisticsStamp = cachedTagStatisticsStamp with { Changes = after };
    }

    /// <summary>Index condition for the quick-search terms of three or more characters, or null when the row-by-row comparison
    /// should answer: outside a transaction, while rows still wait to be indexed, or for very common terms.</summary>
    private string? SearchIndexCondition(IReadOnlyList<string> terms, SqliteTransaction? transaction, Func<object, string> parameter)
    {
        if (transaction is null || terms.Count == 0) return null;
        SyncSearchIndex(transaction, SearchIndexQueryBatch);
        using (var waiting = Command("SELECT EXISTS(SELECT 1 FROM media_search_queue)", transaction))
            if (Convert.ToInt64(waiting.ExecuteScalar()) != 0) return null;
        var patterns = terms.Select(SearchPattern).ToArray();
        var match = string.Join(" AND ", patterns.Select((_, i) => "text GLOB $p" + i));
        using (var probe = Command($"SELECT count(*) FROM (SELECT 1 FROM media_search WHERE {match} LIMIT {SearchIndexBroadLimit + 1})", transaction,
            patterns.Select((p, i) => ("$p" + i, (object)p)).ToArray()))
            if (Convert.ToInt64(probe.ExecuteScalar()) > SearchIndexBroadLimit) return null;
        return "q.rowid IN (SELECT rowid FROM media_search WHERE " + string.Join(" AND ", patterns.Select(p => "text GLOB " + parameter(p))) + ")";
    }

    /// <summary>GLOB pattern for an exact substring of folded text; GLOB metacharacters become one-character classes.</summary>
    internal static string SearchPattern(string term)
    {
        var pattern = new StringBuilder("*");
        foreach (var c in SearchFold.Fold(term)) pattern.Append(c is '*' or '?' or '[' ? "[" + c + "]" : c.ToString());
        return pattern.Append('*').ToString();
    }

    /// <summary>Trigrams count code points, not UTF-16 units.</summary>
    internal static bool UsesSearchIndex(string term) => term.EnumerateRunes().Count() >= SearchIndexMinimumLength;
}
