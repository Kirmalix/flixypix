using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

internal sealed class CatalogFacetsCache(CatalogDatabase database)
{
    internal CatalogFacets? Value { get; private set; }
    private (long Changes, long DataVersion) stamp;
    private sealed record TagFacets(IReadOnlyList<string> Tags, IReadOnlyDictionary<string, IReadOnlyList<string>> Groups,
        IReadOnlyDictionary<string, long> Counts);

    internal CatalogFacets Read(CancellationToken token = default) => database.Run(token, () =>
    {
        var current = database.ChangeStamp();
        if (Value is not null && stamp == current) return Value;
        using var transaction = database.Connection.BeginTransaction();
        using var count = database.Command("SELECT count(*) FROM media_query", transaction);
        var mediaCount = Convert.ToInt64(count.ExecuteScalar());
        var tags = ReadTags(transaction, token);
        var (categories, categoryCounts) = ReadCounted("SELECT category COLLATE EFP_ORDINAL,count(*) FROM media_query WHERE category IS NOT NULL GROUP BY 1 ORDER BY 1 COLLATE EFP_ORDINAL", transaction, token);
        var (folders, folderCounts) = ReadCounted("SELECT efp_folder(path) COLLATE EFP_ORDINAL,count(*) FROM media_query GROUP BY 1 ORDER BY 1 COLLATE EFP_ORDINAL", transaction, token);
        transaction.Commit();
        Value = new(mediaCount, tags.Tags, categories, tags.Groups.Keys.ToArray(), folders, tags.Groups, folderCounts, categoryCounts, tags.Counts);
        stamp = current;
        return Value;
    });

    internal void PreserveAfterIndexing(long before, long after)
    {
        if (Value is not null && stamp.Changes == before) stamp = stamp with { Changes = after };
    }

    internal void CarryAcross((long Changes, long DataVersion) before, (long Changes, long DataVersion) after,
        IReadOnlyList<(MediaItem Old, MediaItem New)> changes, bool samePaths, bool sameTags)
    {
        if (Value is not { } facets || stamp != before || !samePaths ||
            changes.Any(c => !string.Equals(c.Old.Category, c.New.Category, StringComparison.Ordinal))) return;
        if (!sameTags)
        {
            var tags = ReadTags(null, CancellationToken.None);
            facets = facets with { Tags = tags.Tags, TagCategories = tags.Groups.Keys.ToArray(), CategorizedTags = tags.Groups, TagCategoryCounts = tags.Counts };
        }
        Value = facets;
        stamp = after;
    }

    private TagFacets ReadTags(SqliteTransaction? transaction, CancellationToken token)
    {
        var tags = new List<string>();
        using (var command = database.Command("SELECT DISTINCT tag COLLATE EFP_ORDINAL FROM media_tags ORDER BY tag COLLATE EFP_ORDINAL", transaction))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) { token.ThrowIfCancellationRequested(); tags.Add(reader.GetString(0)); }
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using (var command = database.Command("SELECT DISTINCT category COLLATE EFP_ORDINAL,tag COLLATE EFP_ORDINAL FROM media_tags WHERE category<>'' ORDER BY category COLLATE EFP_ORDINAL,tag COLLATE EFP_ORDINAL", transaction))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var category = reader.GetString(0);
                if (!groups.TryGetValue(category, out var values)) groups[category] = values = [];
                values.Add(reader.GetString(1));
            }
        var (_, counts) = ReadCounted("SELECT category COLLATE EFP_ORDINAL,count(DISTINCT media_id) FROM media_tags WHERE category<>'' GROUP BY 1", transaction, token);
        return new(tags, groups.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value.ToArray(), StringComparer.OrdinalIgnoreCase), counts);
    }

    private (IReadOnlyList<string> Values, IReadOnlyDictionary<string, long> Counts) ReadCounted(string sql, SqliteTransaction? transaction, CancellationToken token)
    {
        using var command = database.Command(sql, transaction);
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.IsDBNull(0)) continue;
            var value = reader.GetString(0);
            values.Add(value);
            counts[value] = reader.GetInt64(1);
        }
        return (values, counts);
    }
}
