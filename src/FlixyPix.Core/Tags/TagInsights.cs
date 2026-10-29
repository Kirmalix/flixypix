using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed record TagUsage(string Tag, int MediaCount);
public enum TagSuggestionSource { CoOccurrence, Folder, Name }
/// <summary>A tag worth adding, with a human-readable reason so suggestions stay explainable.</summary>
public sealed record TagSuggestion(string Tag, double Score, TagSuggestionSource Source, string Reason);
public sealed record TagCombination(IReadOnlyList<string> Tags, int MediaCount);

/// <summary>Tag counts, co-occurrence suggestions, typical combinations and bulk tag edits.
/// Statistics are computed locally from the catalog and reused until the catalog changes.</summary>
public sealed partial class CatalogRepository
{
    private const int CombinationTagsPerMedia = 12, MaximumCombinationSize = 4, CombinationCandidates = 60;
    private TagStatistics? cachedTagStatistics;
    private (long Changes, long DataVersion) cachedTagStatisticsStamp;

    private sealed class TagStatistics
    {
        public Dictionary<string, int> Counts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Display { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Dictionary<string, int>> Neighbours { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Dictionary<string, int>> FolderTags { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> FolderMedia { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Normalized tags per tagged medium (by id), so a tag edit can update the statistics in place.</summary>
        public Dictionary<string, string[]> TagSets { get; } = new(StringComparer.Ordinal);
        public List<(string Tag, string Normalized)> NameCandidates { get; } = [];
        public IReadOnlyList<TagCombination>? Combinations { get; set; }
    }

    public IReadOnlyList<TagUsage> GetTagUsage()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var statistics = Statistics();
            return statistics.Counts.Select(pair => new TagUsage(statistics.Display[pair.Key], pair.Value))
                .OrderByDescending(usage => usage.MediaCount).ThenBy(usage => usage.Tag, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    /// <summary>Suggests tags for the given media from co-occurring tags, tags of folder neighbours and words in file or folder names.
    /// Tags already present on every given medium are never suggested.</summary>
    public IReadOnlyList<TagSuggestion> SuggestTags(IReadOnlyCollection<Guid> mediaIds, int limit = 12)
    {
        ArgumentNullException.ThrowIfNull(mediaIds);
        lock (gate)
        {
            ThrowIfDisposed();
            var items = mediaIds.Distinct().Select(GetMediaCore).OfType<MediaItem>().ToArray();
            if (items.Length == 0 || limit <= 0) return [];
            var statistics = Statistics();
            var onAll = new HashSet<string>(items[0].Tags, StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.Skip(1)) onAll.IntersectWith(item.Tags);
            var present = new HashSet<string>(items.SelectMany(item => item.Tags), StringComparer.OrdinalIgnoreCase);
            var best = new Dictionary<string, TagSuggestion>(StringComparer.OrdinalIgnoreCase);
            void Offer(string tag, double score, TagSuggestionSource source, string reason)
            {
                if (onAll.Contains(tag)) return;
                var display = statistics.Display.GetValueOrDefault(tag, tag);
                if (!best.TryGetValue(tag, out var current) || current.Score < score) best[tag] = new(display, score, source, reason);
            }
            foreach (var tag in present)
            {
                if (!statistics.Counts.TryGetValue(tag, out var count) || count < 2 || !statistics.Neighbours.TryGetValue(tag, out var neighbours)) continue;
                foreach (var (other, together) in neighbours)
                {
                    var confidence = (double)together / count;
                    if (together >= 2 && confidence >= .2)
                        Offer(other, confidence, TagSuggestionSource.CoOccurrence, T("{0:P0} of the items with “{1}” also have “{2}”", confidence, statistics.Display[tag], statistics.Display[other]));
                }
            }
            foreach (var folder in items.Select(item => Path.GetDirectoryName(item.Path) ?? "").Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!statistics.FolderMedia.TryGetValue(folder, out var total) || total < 2 || !statistics.FolderTags.TryGetValue(folder, out var tags)) continue;
                foreach (var (tag, count) in tags)
                {
                    var share = (double)count / total;
                    if (count >= 2 && share >= .25) Offer(tag, share * .9, TagSuggestionSource.Folder, T("{0} of {1} items in the same folder have “{2}”", count, total, statistics.Display[tag]));
                }
            }
            foreach (var item in items)
            {
                var name = " " + NormalizeWords(NameContext(item.Path)) + " ";
                foreach (var (tag, normalized) in statistics.NameCandidates)
                    if (name.Contains(" " + normalized + " ", StringComparison.OrdinalIgnoreCase))
                        Offer(tag, statistics.Counts.ContainsKey(tag) ? .6 : .5, TagSuggestionSource.Name, T("“{0}” is in the file or folder name", tag));
            }
            return best.Values.OrderByDescending(s => s.Score).ThenByDescending(s => statistics.Counts.GetValueOrDefault(s.Tag)).ThenBy(s => s.Tag, StringComparer.OrdinalIgnoreCase)
                .Take(limit).ToArray();
        }
    }

    /// <summary>Frequent tag combinations (2–4 tags). With <paramref name="context"/>, combinations that extend these tags come first.</summary>
    public IReadOnlyList<TagCombination> GetTagCombinations(IReadOnlyCollection<string>? context = null, int limit = 8)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var statistics = Statistics();
            var combinations = statistics.Combinations ??= MineCombinations(statistics);
            if (context is not { Count: > 0 }) return combinations.Take(limit).ToArray();
            var present = new HashSet<string>(context, StringComparer.OrdinalIgnoreCase);
            return combinations.Select(c => (Combination: c, Overlap: c.Tags.Count(present.Contains)))
                .Where(x => x.Overlap > 0 && x.Overlap < x.Combination.Tags.Count)
                .OrderByDescending(x => x.Overlap).ThenByDescending(x => x.Combination.MediaCount).Select(x => x.Combination).Take(limit).ToArray();
        }
    }

    /// <summary>Adds and removes flat tags on several media in one transaction. Removal also clears the tag from tag categories.</summary>
    public int UpdateTags(IReadOnlyCollection<Guid> mediaIds, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaIds); ArgumentNullException.ThrowIfNull(add); ArgumentNullException.ThrowIfNull(remove);
        if (add.Concat(remove).Any(string.IsNullOrWhiteSpace)) throw new ArgumentException(T("Tags must not be empty."));
        var adding = add.Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var removing = new HashSet<string>(remove.Select(t => t.Trim()), StringComparer.OrdinalIgnoreCase);
        if (adding.Any(removing.Contains)) throw new ArgumentException(T("A tag cannot be added and removed at the same time."));
        lock (gate)
        {
            ThrowIfDisposed();
            var before = ChangeStamp();
            var changes = new List<(MediaItem Old, MediaItem New)>();
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var id in mediaIds.Distinct())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var command = Command("SELECT document FROM media WHERE id=$id", transaction, ("$id", id.ToString()));
                    if (command.ExecuteScalar() is not string json) continue;
                    var item = JsonSerializer.Deserialize<MediaItem>(json, Json)!;
                    var tags = item.Tags.Where(t => !removing.Contains(t)).ToList();
                    tags.AddRange(adding.Where(t => !tags.Contains(t, StringComparer.OrdinalIgnoreCase)));
                    var categories = item.TagCategories.Select(p => (p.Key, Tags: p.Value.Where(t => !removing.Contains(t)).ToList())).Where(p => p.Tags.Count > 0)
                        .ToDictionary(p => p.Key, p => p.Tags, StringComparer.OrdinalIgnoreCase);
                    if (tags.SequenceEqual(item.Tags) && categories.Count == item.TagCategories.Count && categories.All(p => item.TagCategories[p.Key].Count == p.Value.Count)) continue;
                    changes.Add((item, UpsertCore(item with { Tags = tags, TagCategories = categories }, transaction)));
                }
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
            }
            if (changes.Count > 0) CarryCachesAcross(before, changes);
            return changes.Count;
        }
    }

    private MediaItem? GetMediaCore(Guid id)
    {
        using var command = Command("SELECT document FROM media WHERE id=$id", null, ("$id", id.ToString()));
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<MediaItem>(json, Json) : null;
    }

    private TagStatistics Statistics()
    {
        var stamp = ChangeStamp();
        if (cachedTagStatistics is not null && cachedTagStatisticsStamp == stamp) return cachedTagStatistics;
        var statistics = new TagStatistics();
        var media = new Dictionary<string, (string Folder, List<string> Tags)>(StringComparer.Ordinal);
        using (var command = Command("SELECT t.media_id,t.tag,q.path FROM media_tags t JOIN media_query q ON q.id=t.media_id", null))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var tag = reader.GetString(1).Trim(); if (tag.Length == 0) continue;
                if (!media.TryGetValue(reader.GetString(0), out var entry)) media[reader.GetString(0)] = entry = (Path.GetDirectoryName(reader.GetString(2)) ?? "", []);
                if (!entry.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) entry.Tags.Add(tag);
                statistics.Display.TryAdd(tag, tag);
            }
        using (var command = Command("SELECT path FROM media_query", null))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var folder = Path.GetDirectoryName(reader.GetString(0)) ?? "";
                statistics.FolderMedia[folder] = statistics.FolderMedia.GetValueOrDefault(folder) + 1;
            }
        foreach (var (id, (folder, tags)) in media) CountTags(statistics, id, folder, tags.ToArray(), +1);
        BuildNameCandidates(statistics);
        cachedTagStatistics = statistics; cachedTagStatisticsStamp = stamp;
        return statistics;
    }

    /// <summary>Adds (+1) or takes away (−1) one medium's tags; entries that fall to zero disappear, as if never counted.</summary>
    private static void CountTags(TagStatistics statistics, string id, string folder, string[] tags, int sign)
    {
        if (tags.Length == 0) return;
        if (sign > 0) statistics.TagSets[id] = tags; else statistics.TagSets.Remove(id);
        static void Add(Dictionary<string, int> counts, string key, int delta) { var value = counts.GetValueOrDefault(key) + delta; if (value > 0) counts[key] = value; else counts.Remove(key); }
        if (!statistics.FolderTags.TryGetValue(folder, out var folderTags)) statistics.FolderTags[folder] = folderTags = new(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            Add(statistics.Counts, tag, sign); Add(folderTags, tag, sign);
            if (sign > 0) statistics.Display.TryAdd(tag, tag);
            if (!statistics.Neighbours.TryGetValue(tag, out var neighbours)) statistics.Neighbours[tag] = neighbours = new(StringComparer.OrdinalIgnoreCase);
            foreach (var other in tags) if (!string.Equals(other, tag, StringComparison.OrdinalIgnoreCase)) Add(neighbours, other, sign);
            if (!statistics.Counts.ContainsKey(tag)) statistics.Neighbours.Remove(tag);
        }
        if (folderTags.Count == 0) statistics.FolderTags.Remove(folder);
    }

    private void BuildNameCandidates(TagStatistics statistics)
    {
        statistics.NameCandidates.Clear();
        var names = new HashSet<string>(statistics.Counts.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ReadPreferences().TagVocabulary) if (!string.IsNullOrWhiteSpace(entry.Tag)) { names.Add(entry.Tag.Trim()); statistics.Display.TryAdd(entry.Tag.Trim(), entry.Tag.Trim()); }
        foreach (var entry in ReadPreferences().TagVocabulary)
            foreach (var alias in entry.Aliases ?? [])
            {
                var normalizedAlias = NormalizeWords(alias);
                if (normalizedAlias.Length >= 3) statistics.NameCandidates.Add((entry.Tag, normalizedAlias));
            }
        foreach (var tag in names)
        {
            var normalized = NormalizeWords(tag);
            if (normalized.Length >= 3) statistics.NameCandidates.Add((statistics.Display[tag], normalized));
        }
    }

    /// <summary>The tags of one medium as the statistics count them (media_tags: flat and categorized, trimmed, once per medium).</summary>
    private static string[] StatisticsTags(MediaItem item) =>
        item.Tags.Concat(item.TagCategories.Values.SelectMany(t => t)).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Keeps the facet and tag-statistics caches valid across a write that changed only existing media, without
    /// rescanning the catalog: a write that leaves paths and categories alone keeps folders and categories, and a tag edit
    /// updates the tag lists and statistics in place. <paramref name="before"/> is the change stamp taken before the write.</summary>
    private void CarryCachesAcross((long Changes, long DataVersion) before, IReadOnlyList<(MediaItem Old, MediaItem New)> changes)
    {
        var after = ChangeStamp();
        if (after.DataVersion != before.DataVersion) return; // another connection committed meanwhile
        var samePaths = changes.All(c => string.Equals(c.Old.Path, c.New.Path, StringComparison.Ordinal));
        static IEnumerable<string> Raw(MediaItem item) => item.Tags.Concat(item.TagCategories.SelectMany(p => p.Value.Select(t => p.Key + "\0" + t)));
        var sameTags = changes.All(c => Raw(c.Old).Order(StringComparer.Ordinal).SequenceEqual(Raw(c.New).Order(StringComparer.Ordinal), StringComparer.Ordinal));
        if (cachedFacets is { } facets && cachedFacetStamp == before && samePaths && changes.All(c => string.Equals(c.Old.Category, c.New.Category, StringComparison.Ordinal)))
        {
            if (!sameTags)
            {
                IReadOnlyList<string> Values(string sql) { using var command = Command(sql, null); using var reader = command.ExecuteReader(); var values = new List<string>(); while (reader.Read()) values.Add(reader.GetString(0)); return values; }
                var groups = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                using (var command = Command("SELECT DISTINCT category COLLATE EFP_ORDINAL,tag COLLATE EFP_ORDINAL FROM media_tags WHERE category<>'' ORDER BY category COLLATE EFP_ORDINAL,tag COLLATE EFP_ORDINAL", null))
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) { if (!groups.TryGetValue(reader.GetString(0), out var list)) groups[reader.GetString(0)] = list = new List<string>(); ((List<string>)list).Add(reader.GetString(1)); }
                facets = facets with { Tags = Values("SELECT DISTINCT tag COLLATE EFP_ORDINAL FROM media_tags ORDER BY tag COLLATE EFP_ORDINAL"), TagCategories = groups.Keys.ToArray(), CategorizedTags = groups };
            }
            cachedFacets = facets; cachedFacetStamp = after;
        }
        if (cachedTagStatistics is { } statistics && cachedTagStatisticsStamp == before && samePaths)
        {
            if (!sameTags)
            {
                var names = statistics.Counts.Count;
                var known = new HashSet<string>(statistics.Counts.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (var (old, updated) in changes)
                {
                    var folder = Path.GetDirectoryName(old.Path) ?? "";
                    CountTags(statistics, old.Id.ToString(), folder, StatisticsTags(old), -1);
                    CountTags(statistics, updated.Id.ToString(), folder, StatisticsTags(updated), +1);
                }
                statistics.Combinations = null;
                if (statistics.Counts.Count != names || !statistics.Counts.Keys.All(known.Contains)) BuildNameCandidates(statistics);
            }
            cachedTagStatisticsStamp = after;
        }
    }

    // Counts subsets of each medium's most frequent tags; tag ids are packed into one long per subset.
    private static IReadOnlyList<TagCombination> MineCombinations(TagStatistics statistics)
    {
        var ids = statistics.Counts.Keys.OrderByDescending(tag => statistics.Counts[tag]).ThenBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select((tag, index) => (tag, index)).ToDictionary(x => x.tag, x => x.index, StringComparer.OrdinalIgnoreCase);
        if (ids.Count >= ushort.MaxValue) return [];
        var names = ids.OrderBy(x => x.Value).Select(x => statistics.Display[x.Key]).ToArray();
        var support = new Dictionary<long, int>();
        foreach (var set in statistics.TagSets.Values)
        {
            var members = set.Select(t => ids[t]).Distinct().Order().Take(CombinationTagsPerMedia).ToArray();
            void Visit(int start, int size, long key)
            {
                if (size >= 2) support[key] = support.GetValueOrDefault(key) + 1;
                if (size == MaximumCombinationSize) return;
                for (var i = start; i < members.Length; i++) Visit(i + 1, size + 1, key << 16 | (long)(members[i] + 1));
            }
            Visit(0, 0, 0);
        }
        var minimum = Math.Max(3, statistics.TagSets.Count / 500);
        static long Pack(IEnumerable<int> members) => members.Aggregate(0L, (key, member) => key << 16 | (long)(member + 1));
        static int[] Unpack(long key) { var result = new List<int>(); for (; key != 0; key >>= 16) result.Insert(0, (int)(key & 0xFFFF) - 1); return result.ToArray(); }
        var frequent = support.Where(p => p.Value >= minimum).ToDictionary(p => p.Key, p => p.Value);
        // Drop a combination when a larger one covers nearly the same media; it adds no choice for the user.
        var covered = new HashSet<long>();
        foreach (var (key, count) in frequent)
        {
            var members = Unpack(key); if (members.Length < 3) continue;
            for (var skip = 0; skip < members.Length; skip++)
            {
                var subset = Pack(members.Where((_, index) => index != skip));
                if (frequent.TryGetValue(subset, out var subsetCount) && count >= subsetCount * .8) covered.Add(subset);
            }
        }
        return frequent.Where(p => !covered.Contains(p.Key)).Select(p => (Members: Unpack(p.Key), Count: p.Value))
            .OrderByDescending(c => c.Count * (1 + .5 * (c.Members.Length - 2))).ThenBy(c => c.Members.Length).ThenBy(c => string.Join(',', c.Members))
            .Take(CombinationCandidates).Select(c => new TagCombination(c.Members.Select(m => names[m]).ToArray(), c.Count)).ToArray();
    }

    private static string NameContext(string path)
    {
        var parts = new List<string> { Path.GetFileNameWithoutExtension(path) };
        var folder = Path.GetDirectoryName(path);
        for (var depth = 0; depth < 3 && !string.IsNullOrEmpty(folder); depth++) { parts.Add(Path.GetFileName(folder)); folder = Path.GetDirectoryName(folder); }
        return string.Join(' ', parts);
    }

    private static string NormalizeWords(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value) builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
