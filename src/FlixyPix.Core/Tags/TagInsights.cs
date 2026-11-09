using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed record TagUsage(string Tag, int MediaCount);
public enum TagSuggestionSource { CoOccurrence, Folder, Name }
/// <summary>A tag worth adding, with a human-readable reason so suggestions stay explainable.</summary>
public sealed record TagSuggestion(string Tag, double Score, TagSuggestionSource Source, string Reason)
{
    /// <summary>The applied tag behind a co-occurrence suggestion.</summary>
    public string? BasedOn { get; init; }
    /// <summary>A performer or studio belongs into its category, not among the flat tags.</summary>
    public TagRole Role { get; init; }
    public string? Category { get; init; }
}
/// <summary>How often a suggestion was taken or passed over on media whose tags were being worked on.</summary>
public sealed record TagSuggestionFeedback(int Taken, int Ignored)
{
    internal double Factor => Ignored < 3 ? 1 : Math.Max(.2, (Taken + 1d) / (Taken + Ignored + 1d));
}
public sealed record TagCombination(IReadOnlyList<string> Tags, int MediaCount);

/// <summary>Tag counts, co-occurrence suggestions, typical combinations and bulk tag edits.
/// Statistics are computed locally from the catalog and reused until the catalog changes.</summary>
public sealed partial class CatalogRepository
{
    /// <summary>Below this many tagged media the share of a tag says too little to compare against.</summary>
    private const int LiftMinimumMedia = 50;
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
        public Dictionary<string, (TagRole Role, string Category)> Roles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<TagCombination>? Combinations { get; set; }
    }

    public IReadOnlyList<TagUsage> GetTagUsage()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var stamp = ChangeStamp();
            if (cachedTagUsage is not null && cachedTagUsageStamp == stamp) return cachedTagUsage;
            if (cachedTagStatistics is { } statistics && cachedTagStatisticsStamp == stamp)
            {
                cachedTagUsage = statistics.Counts.Select(pair => new TagUsage(statistics.Display[pair.Key], pair.Value))
                    .OrderByDescending(usage => usage.MediaCount).ThenBy(usage => usage.Tag, StringComparer.OrdinalIgnoreCase).ToArray();
                cachedTagUsageStamp = stamp;
                return cachedTagUsage;
            }
            // The usage alone comes from one table. The statistics also need every path, which on a cold disk takes seconds at each start.
            cachedTagUsage = ReadTagUsage(connection);
            cachedTagUsageStamp = stamp;
            return cachedTagUsage;
        }
    }

    private static IReadOnlyList<TagUsage> ReadTagUsage(SqliteConnection source)
    {
        var perMedium = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var display = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var command = source.CreateCommand();
        command.CommandText = "SELECT media_id,tag FROM media_tags";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var tag = reader.GetString(1).Trim(); if (tag.Length == 0) continue;
            var id = reader.GetString(0);
            if (!perMedium.TryGetValue(id, out var tags)) perMedium[id] = tags = [];
            if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) { tags.Add(tag); counts[tag] = counts.GetValueOrDefault(tag) + 1; }
            display.TryAdd(tag, tag);
        }
        return counts.Select(pair => new TagUsage(display[pair.Key], pair.Value))
            .OrderByDescending(usage => usage.MediaCount).ThenBy(usage => usage.Tag, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    /// <summary>The usage after an edit of some media, without going through all tags of the library again.</summary>
    private static IReadOnlyList<TagUsage> WithUsageChanges(IReadOnlyList<TagUsage> usage, IReadOnlyList<(MediaItem Old, MediaItem New)> changes)
    {
        var counts = usage.ToDictionary(entry => entry.Tag, entry => entry.MediaCount, StringComparer.OrdinalIgnoreCase);
        foreach (var (old, updated) in changes)
        {
            var before = StatisticsTags(old); var now = StatisticsTags(updated);
            foreach (var tag in now.Except(before, StringComparer.OrdinalIgnoreCase)) counts[tag] = counts.GetValueOrDefault(tag) + 1;
            foreach (var tag in before.Except(now, StringComparer.OrdinalIgnoreCase))
                if (counts.GetValueOrDefault(tag) <= 1) counts.Remove(tag); else counts[tag]--;
        }
        return counts.Select(pair => new TagUsage(pair.Key, pair.Value)).OrderByDescending(entry => entry.MediaCount).ThenBy(entry => entry.Tag, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private IReadOnlyList<TagUsage>? cachedTagUsage;
    private (long Changes, long DataVersion) cachedTagUsageStamp;

    /// <summary>Suggests tags for the given media from co-occurring tags, tags of folder neighbours and words in file or folder names.
    /// Tags already present on every given medium are never suggested.</summary>
    public IReadOnlyList<TagSuggestion> SuggestTags(IReadOnlyCollection<Guid> mediaIds, int limit = 12, IReadOnlyDictionary<string, TagSuggestionFeedback>? feedback = null)
    {
        ArgumentNullException.ThrowIfNull(mediaIds);
        lock (gate)
        {
            ThrowIfDisposed();
            var items = mediaIds.Distinct().Select(GetMediaCore).OfType<MediaItem>().ToArray();
            if (items.Length == 0 || limit <= 0) return [];
            var statistics = Statistics();
            var onAll = new HashSet<string>(StatisticsTags(items[0]), StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.Skip(1)) onAll.IntersectWith(StatisticsTags(item));
            var present = new HashSet<string>(items.SelectMany(item => item.Tags), StringComparer.OrdinalIgnoreCase);
            var tagged = statistics.TagSets.Count;
            var best = new Dictionary<string, TagSuggestion>(StringComparer.OrdinalIgnoreCase);
            void Offer(string tag, double score, TagSuggestionSource source, string reason, string? basedOn = null)
            {
                if (onAll.Contains(tag)) return;
                if (feedback?.GetValueOrDefault(tag) is { } answered) score *= answered.Factor;
                var display = statistics.Display.GetValueOrDefault(tag, tag);
                var role = statistics.Roles.GetValueOrDefault(tag);
                if (!best.TryGetValue(tag, out var current) || current.Score < score) best[tag] = new(display, score, source, reason) { BasedOn = basedOn, Role = role.Role, Category = role.Category };
            }
            foreach (var tag in present)
            {
                if (!statistics.Counts.TryGetValue(tag, out var count) || count < 2 || !statistics.Neighbours.TryGetValue(tag, out var neighbours)) continue;
                foreach (var (other, together) in neighbours)
                {
                    var confidence = (double)together / count;
                    if (together < 2 || confidence < .2) continue;
                    // A tag that half the library has says little by also being on half of these media: what counts is how much more typical it is here.
                    var lift = tagged >= LiftMinimumMedia ? confidence / ((double)statistics.Counts.GetValueOrDefault(other, 1) / tagged) : 2;
                    if (lift >= 1.15)
                        Offer(other, confidence * Math.Min(1, lift / 2), TagSuggestionSource.CoOccurrence, T("{0:P0} of the items with “{1}” also have “{2}”", confidence, statistics.Display[tag], statistics.Display[other]), statistics.Display[tag]);
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
                        // A single word that happens to be a person's name is a weak hint.
                        Offer(tag, statistics.Roles.ContainsKey(tag) && !tag.Contains(' ') ? .35 : statistics.Counts.ContainsKey(tag) ? .6 : .5, TagSuggestionSource.Name, T("“{0}” is in the file or folder name", tag));
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
        => UpdateTagsWithUndo(mediaIds, add, remove, cancellationToken).Rows.Count;

    public ImmediateEditUndo UpdateTagsWithUndo(IReadOnlyCollection<Guid> mediaIds, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove, CancellationToken cancellationToken = default)
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
            long? namespaceRevision = null;
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
                if (changes.Count > 0) namespaceRevision = ReadTagNamespaceRevision(transaction);
                CommitMediaChanges(transaction, before, changes);
            }
            return new(changes.Select(change => ImmediateRow(change.Old, change.New, ImmediateEditFields.Tags)).ToArray()) { TagNamespaceRevision = namespaceRevision };
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
        var statistics = BuildTagStatistics(ReadTagStatisticsRows(connection));
        cachedTagStatistics = statistics; cachedTagStatisticsStamp = stamp;
        return statistics;
    }

    /// <summary>Reads what suggestions need on a reader of its own, so the first selection does not hold up everything else
    /// while every path is read from a slow disk. Otherwise the suggestions read it themselves.</summary>
    public void PrepareTagSuggestions(CancellationToken cancellationToken = default)
    {
        (long Changes, long DataVersion) before;
        lock (gate)
        {
            ThrowIfDisposed();
            before = ChangeStamp();
            if (cachedTagStatistics is not null && cachedTagStatisticsStamp == before) return;
        }
        (List<(string Id, string Path)> Paths, List<(string Id, string Tag)> Tags) rows;
        try
        {
            using var aside = OpenAside();
            rows = new CatalogDatabase(aside, new object(), () => false).Run(cancellationToken, () => ReadTagStatisticsRows(aside));
        }
        catch (SqliteException error) when (error.SqliteErrorCode != 9) { return; }
        database.Run(cancellationToken, () =>
        {
            if (ChangeStamp() == before) { cachedTagStatistics = BuildTagStatistics(rows); cachedTagStatisticsStamp = before; }
            return true;
        });
    }

    /// <summary>Both tables in one pass each: looking up a path per tag jumps through the file, which a cold disk answers slowly.
    /// The order takes the paths from their index, which the lists beside the tiles have usually just read.</summary>
    private static (List<(string Id, string Path)> Paths, List<(string Id, string Tag)> Tags) ReadTagStatisticsRows(SqliteConnection source)
    {
        var paths = new List<(string, string)>(); var tags = new List<(string, string)>();
        using (var command = source.CreateCommand())
        {
            command.CommandText = "SELECT id,path FROM media_query ORDER BY path COLLATE EFP_ORDINAL";
            using var reader = command.ExecuteReader();
            while (reader.Read()) paths.Add((reader.GetString(0), reader.GetString(1)));
        }
        using (var command = source.CreateCommand())
        {
            command.CommandText = "SELECT media_id,tag FROM media_tags";
            using var reader = command.ExecuteReader();
            while (reader.Read()) tags.Add((reader.GetString(0), reader.GetString(1)));
        }
        return (paths, tags);
    }

    private TagStatistics BuildTagStatistics((List<(string Id, string Path)> Paths, List<(string Id, string Tag)> Tags) rows)
    {
        var statistics = new TagStatistics();
        var media = new Dictionary<string, (string Folder, List<string> Tags)>(StringComparer.Ordinal);
        var folders = new Dictionary<string, string>(rows.Paths.Count, StringComparer.Ordinal);
        foreach (var (id, path) in rows.Paths)
        {
            var folder = Path.GetDirectoryName(path) ?? "";
            folders[id] = folder;
            statistics.FolderMedia[folder] = statistics.FolderMedia.GetValueOrDefault(folder) + 1;
        }
        foreach (var (id, raw) in rows.Tags)
        {
            var tag = raw.Trim(); if (tag.Length == 0) continue;
            if (!folders.TryGetValue(id, out var folder)) continue;
            if (!media.TryGetValue(id, out var entry)) media[id] = entry = (folder, []);
            if (!entry.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) entry.Tags.Add(tag);
            statistics.Display.TryAdd(tag, tag);
        }
        foreach (var (id, (folder, tags)) in media) CountTags(statistics, id, folder, tags.ToArray(), +1);
        BuildNameCandidates(statistics);
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
        statistics.NameCandidates.Clear(); statistics.Roles.Clear();
        var preferencesNow = ReadPreferences();
        foreach (var group in preferencesNow.TagVocabulary.GroupBy(entry => entry.Category, StringComparer.OrdinalIgnoreCase))
        {
            var role = TagRoles.Of(preferencesNow, group.Key); if (role == TagRole.Tag) continue;
            foreach (var entry in group) if (!string.IsNullOrWhiteSpace(entry.Tag)) statistics.Roles.TryAdd(entry.Tag.Trim(), (role, group.Key));
        }
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
        facetsCache.CarryAcross(before, after, changes, samePaths, sameTags);
        if (cachedTagUsage is { } usage && cachedTagUsageStamp == before)
        {
            cachedTagUsage = sameTags ? usage : WithUsageChanges(usage, changes);
            cachedTagUsageStamp = after;
        }
        if (cachedNavigationCounts is { } navigation && cachedNavigationCountsStamp == before)
        {
            cachedNavigationCounts = changes.Aggregate(navigation, (counts, change) => counts.Moved(change.Old, change.New));
            cachedNavigationCountsStamp = after;
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
