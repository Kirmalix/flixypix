using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

internal sealed class CatalogFacetsCache(CatalogDatabase database)
{
 internal CatalogFacets? Value { get; private set; }
 private (long Changes,long DataVersion) Stamp { get; set; }
    internal CatalogFacets Read(CancellationToken cancellationToken = default)
    {
        lock (database.Gate)
        {
            database.ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
            var stamp = database.ChangeStamp();
            if (Value is not null && Stamp == stamp) return Value;
            using var transaction = database.Connection.BeginTransaction();
            IReadOnlyList<string> Values(string sql)
            {
                using var command = database.Command(sql, transaction);
                using var cancel = cancellationToken.Register(command.Cancel);
                using var reader = command.ExecuteReader();
                var values = new List<string>();
                while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); if (!reader.IsDBNull(0)) values.Add(reader.GetString(0)); }
                return values;
            }
            long count;
            using (var countCommand = database.Command("SELECT count(*) FROM media_query", transaction)) count = Convert.ToInt64(countCommand.ExecuteScalar());
            var tags = Values("SELECT DISTINCT tag COLLATE EFP_ORDINAL FROM media_tags ORDER BY tag COLLATE EFP_ORDINAL");
            // Values and how many media carry each, in the order of the value.
            (IReadOnlyList<string> Values, Dictionary<string, long> Counts) Counted(string sql)
            {
                using var command = database.Command(sql, transaction);
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
            using (var command = database.Command("SELECT DISTINCT category COLLATE EFP_ORDINAL,tag COLLATE EFP_ORDINAL FROM media_tags WHERE category<>'' ORDER BY category COLLATE EFP_ORDINAL,tag COLLATE EFP_ORDINAL", transaction))
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
            Value = new(count, tags, categories, groups.Keys.ToArray(), folders, groups, folderCounts, categoryCounts, tagCategoryCounts);
            Stamp = stamp;
            return Value;
        }
    }
 internal void PreserveAfterIndexing(long before,long after)
 {
  if(Value is not null && Stamp.Changes==before)Stamp=Stamp with {Changes=after};
 }
 internal void CarryAcross((long Changes,long DataVersion) before,(long Changes,long DataVersion) after,
  IReadOnlyList<(MediaItem Old,MediaItem New)> changes,bool samePaths,bool sameTags)
 {
        if (Value is { } facets && Stamp == before && samePaths && changes.All(c => string.Equals(c.Old.Category, c.New.Category, StringComparison.Ordinal)))
        {
            if (!sameTags)
            {
                IReadOnlyList<string> Values(string sql) { using var command = database.Command(sql, null); using var reader = command.ExecuteReader(); var values = new List<string>(); while (reader.Read()) values.Add(reader.GetString(0)); return values; }
                var groups = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                using (var command = database.Command("SELECT DISTINCT category COLLATE EFP_ORDINAL,tag COLLATE EFP_ORDINAL FROM media_tags WHERE category<>'' ORDER BY category COLLATE EFP_ORDINAL,tag COLLATE EFP_ORDINAL", null))
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) { if (!groups.TryGetValue(reader.GetString(0), out var list)) groups[reader.GetString(0)] = list = new List<string>(); ((List<string>)list).Add(reader.GetString(1)); }
                var categoryCounts = new Dictionary<string, long>(StringComparer.Ordinal);
                using (var command = database.Command("SELECT category COLLATE EFP_ORDINAL,count(DISTINCT media_id) FROM media_tags WHERE category<>'' GROUP BY 1", null))
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) categoryCounts[reader.GetString(0)] = reader.GetInt64(1);
                facets = facets with { Tags = Values("SELECT DISTINCT tag COLLATE EFP_ORDINAL FROM media_tags ORDER BY tag COLLATE EFP_ORDINAL"), TagCategories = groups.Keys.ToArray(), CategorizedTags = groups, TagCategoryCounts = categoryCounts };
            }
            Value = facets; Stamp = after;
        }
 }
}
