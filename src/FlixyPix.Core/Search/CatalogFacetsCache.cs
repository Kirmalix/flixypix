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

    /// <summary>What is known without reading: the facets if still valid, otherwise null.</summary>
    internal CatalogFacets? Known() => Value is not null && stamp == database.ChangeStamp() ? Value : null;

    /// <summary>Takes facets read elsewhere, if nothing changed since that reading began.</summary>
    internal bool Install(CatalogFacets facets, (long Changes, long DataVersion) before)
    {
        if (database.ChangeStamp() != before) return false;
        Value = facets; stamp = before;
        return true;
    }

    internal void PreserveAfterIndexing(long before, long after)
    {
        if (Value is not null && stamp.Changes == before) stamp = stamp with { Changes = after };
    }

    internal void CarryAcross((long Changes, long DataVersion) before, (long Changes, long DataVersion) after,
        IReadOnlyList<(MediaItem Old, MediaItem New)> changes, bool samePaths, bool sameTags)
    {
        if (Value is not { } facets || stamp != before || !samePaths ||
            changes.Any(c => !string.Equals(c.Old.Category, c.New.Category, StringComparison.Ordinal))) return;
        Value = sameTags ? facets : WithTagChanges(facets, changes);
        stamp = after;
    }

    /// <summary>The tag lists after an edit of some media. Reading them anew costs a pass over all tags of the library at every
    /// click; only what the edit took away has to be looked up, because other media may still use it.</summary>
    private CatalogFacets WithTagChanges(CatalogFacets facets, IReadOnlyList<(MediaItem Old, MediaItem New)> changes)
    {
        var tags = facets.Tags.ToList();
        var groups = facets.CategorizedTags.ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, long>(facets.TagCategoryCounts ?? new Dictionary<string, long>(), StringComparer.OrdinalIgnoreCase);
        var removed = new HashSet<(string Category, string Tag)>(TagRowComparer);
        foreach (var (old, updated) in changes)
        {
            var before = TagRows(old); var now = TagRows(updated);
            foreach (var (category, tag) in now.Except(before, TagRowComparer))
            {
                InsertSorted(tags, tag);
                if (category.Length == 0) continue;
                if (!groups.TryGetValue(category, out var group)) groups[category] = group = [];
                InsertSorted(group, tag);
            }
            removed.UnionWith(before.Except(now, TagRowComparer));
            var categoriesBefore = before.Select(r => r.Category).Where(c => c.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var categoriesNow = now.Select(r => r.Category).Where(c => c.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var category in categoriesNow.Except(categoriesBefore, StringComparer.OrdinalIgnoreCase)) counts[category] = counts.GetValueOrDefault(category) + 1;
            foreach (var category in categoriesBefore.Except(categoriesNow, StringComparer.OrdinalIgnoreCase))
                if (counts.GetValueOrDefault(category) <= 1) counts.Remove(category); else counts[category]--;
        }
        foreach (var (category, tag) in removed)
        {
            if (category.Length > 0 && !Exists("SELECT 1 FROM media_tags WHERE category=$category COLLATE EFP_ORDINAL AND tag=$tag COLLATE EFP_ORDINAL LIMIT 1", ("$category", category), ("$tag", tag))
                && groups.TryGetValue(category, out var group))
            {
                RemoveSorted(group, tag);
                if (group.Count == 0) groups.Remove(category);
            }
            if (!Exists("SELECT 1 FROM media_tags WHERE tag=$tag COLLATE EFP_ORDINAL LIMIT 1", ("$tag", tag))) RemoveSorted(tags, tag);
        }
        var ordered = groups.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        return facets with
        {
            Tags = tags, TagCategories = ordered.Select(p => p.Key).ToArray(), TagCategoryCounts = counts,
            CategorizedTags = ordered.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
        };
        bool Exists(string sql, params (string Name, object Value)[] values) { using var command = database.Command(sql, null, values); return command.ExecuteScalar() is not null; }
    }

    private static readonly IEqualityComparer<(string Category, string Tag)> TagRowComparer = EqualityComparer<(string Category, string Tag)>.Create(
        (a, b) => string.Equals(a.Category, b.Category, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Tag, b.Tag, StringComparison.OrdinalIgnoreCase),
        row => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(row.Category), StringComparer.OrdinalIgnoreCase.GetHashCode(row.Tag)));

    /// <summary>The rows the triggers keep in media_tags for this medium.</summary>
    private static HashSet<(string Category, string Tag)> TagRows(MediaItem item) =>
        item.Tags.Select(tag => ("", tag)).Concat(item.TagCategories.SelectMany(group => group.Value.Select(tag => (group.Key, tag)))).ToHashSet(TagRowComparer);

    private static void InsertSorted(List<string> list, string value)
    {
        var place = list.BinarySearch(value, StringComparer.OrdinalIgnoreCase);
        if (place < 0) list.Insert(~place, value);
    }

    private static void RemoveSorted(List<string> list, string value)
    {
        var place = list.BinarySearch(value, StringComparer.OrdinalIgnoreCase);
        if (place >= 0) list.RemoveAt(place);
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
