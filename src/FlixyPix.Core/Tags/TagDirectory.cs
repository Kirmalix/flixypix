using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>One tag of the directory: known from the vocabulary, from media, or both. An empty category means the tag is
/// used without one.</summary>
public sealed record DirectoryEntry(string Tag, string Category, TagRole Role, int MediaCount, bool IsFavorite = false,
    IReadOnlyList<string>? Aliases = null, string? Description = null, string? LegacyId = null, TagProfile? Profile = null)
{
    public IReadOnlyList<string> AliasList => Aliases ?? [];
}

/// <summary>The directory of tags, studios and performers: every tag with its category, role and number of media; editing,
/// merging and deleting in one transaction each. Renaming, merging and deleting work on the tag name in the whole library
/// (media, saved searches, scenes, rules, presets, tag pictures), like <see cref="CatalogRepository.ChangeTag"/>.</summary>
public sealed partial class CatalogRepository
{
    public IReadOnlyList<DirectoryEntry> GetDirectory(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var preferences = ReadPreferences();
            // media_tags has a row without category for every tag and one more per category it is in.
            var counts = new Dictionary<(string Tag, string Category), int>(PairComparer);
            var spelled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var command = Command("SELECT tag COLLATE EFP_ORDINAL, category COLLATE EFP_ORDINAL, count(DISTINCT media_id) FROM media_tags GROUP BY 1, 2", null))
            {
                using var cancel = cancellationToken.Register(command.Cancel);
                using var reader = command.ExecuteReader();
                while (reader.Read()) { counts[(reader.GetString(0), reader.GetString(1))] = reader.GetInt32(2); spelled.TryAdd(reader.GetString(0), reader.GetString(0)); }
            }
            var categorized = counts.Keys.Where(k => k.Category.Length > 0).Select(k => k.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var entries = new Dictionary<(string Tag, string Category), DirectoryEntry>(PairComparer);
            foreach (var ((tag, category), count) in counts)
                if (category.Length > 0 || !categorized.Contains(tag) && !preferences.TagVocabulary.Any(v => Same(v.Tag, tag)))
                    entries[(tag, category)] = new(tag, category, category.Length == 0 ? TagRole.Tag : TagRoles.Of(preferences, category), count);
            foreach (var entry in preferences.TagVocabulary)
            {
                var key = (entry.Tag, entry.Category);
                var count = counts.GetValueOrDefault(key, counts.GetValueOrDefault((entry.Tag, ""), 0));
                entries[key] = new(entries.TryGetValue(key, out var used) ? used.Tag : entry.Tag, entry.Category, TagRoles.Of(preferences, entry.Category),
                    count, entry.IsFavorite, entry.Aliases, entry.Description, entry.LegacyId, entry.Profile);
            }
            return entries.Values.OrderBy(e => e.Tag, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Category, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    /// <summary>Saves the entry that was shown as (<paramref name="tag"/>, <paramref name="category"/>): a new name renames
    /// (or merges into an existing tag), a new category moves the tag's media there, and aliases, favourite and description
    /// are stored in the vocabulary. Everything happens in one transaction.</summary>
    public TagChangeResult SaveDirectoryEntry(string tag, string category, DirectoryEntry edited, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag); ArgumentNullException.ThrowIfNull(category); ArgumentNullException.ThrowIfNull(edited);
        ArgumentException.ThrowIfNullOrWhiteSpace(edited.Tag);
        if (edited.Tag.Length > 256 || edited.Category.Length > 256 || edited.AliasList.Any(a => string.IsNullOrWhiteSpace(a) || a.Length > 256))
            throw new ArgumentException(T("Name, category and aliases must not be empty and may be at most 256 characters long."), nameof(edited));
        var name = edited.Tag.Trim(); var target = edited.Category.Trim();
        var profile = edited.Profile?.Normalized();
        profile?.Validate(DateOnly.FromDateTime(DateTime.Now));
        if (profile?.ParentStudio is { } parent && Same(parent, name)) throw new ArgumentException(T("A studio cannot belong to itself."), nameof(edited));
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var result = new TagChangeResult(0, 0, 0);
            if (!string.Equals(tag.Trim(), name, StringComparison.Ordinal)) result = ChangeTagCore(tag, name, transaction, cancellationToken);
            if (!Same(category, target))
            {
                if (category.Length == 0) AssignCategoryCore(name, target, transaction, cancellationToken);
                else if (target.Length > 0) MoveTagCategoryCore(name, category, target, transaction, cancellationToken);
                else throw new ArgumentException(T("A tag with a category cannot be saved without a category here; change its category instead."), nameof(edited));
            }
            if (target.Length > 0)
            {
                var preferences = ReadPreferences(transaction);
                var aliases = edited.AliasList.Select(a => a.Trim()).Where(a => !Same(a, name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var others = preferences.TagVocabulary.Where(v => !(Same(v.Tag, name) && Same(v.Category, target))).ToList();
                var existing = preferences.TagVocabulary.FirstOrDefault(v => Same(v.Tag, name) && Same(v.Category, target));
                if (profile?.ParentStudio is { } network && StudioChain(others, network).Any(s => Same(s, name)))
                    throw new ArgumentException(T("“{0}” already belongs to “{1}”; a studio cannot belong to its own sub-studio.", network, name), nameof(edited));
                others.Add(new TagVocabularyEntry(name, target, edited.IsFavorite, existing?.LegacyId, aliases.Count == 0 ? null : aliases,
                    string.IsNullOrWhiteSpace(edited.Description) ? null : edited.Description.Trim(), profile));
                SaveDocument("preferences", Guid.Empty, preferences with { TagVocabulary = others }, transaction);
            }
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return result;
        }
    }

    /// <summary>What deleting a tag (or renaming it into <paramref name="replacement"/>) would change, found by doing it
    /// in a transaction that is rolled back.</summary>
    public TagChangeResult PreviewTagChange(string tag, string? replacement, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var result = ChangeTagCore(tag, replacement, transaction, cancellationToken);
            transaction.Rollback();
            cachedMedia = null;
            return result;
        }
    }

    /// <summary>Merges the tags into <paramref name="target"/> (which may be new); their names become aliases of it. The result
    /// carries the undo (<see cref="UndoTagChange"/>).</summary>
    public TagChangeResult MergeTags(IReadOnlyCollection<string> tags, string target, CancellationToken cancellationToken = default) =>
        ChangeTags(tags.Where(t => !Same(t, target)), target, cancellationToken);

    /// <summary>Removes the tags from all media, searches and rules; rules that used them are disabled, not changed. The result
    /// carries the undo (<see cref="UndoTagChange"/>).</summary>
    public TagChangeResult DeleteTags(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default) => ChangeTags(tags, null, cancellationToken);

    private TagChangeResult ChangeTags(IEnumerable<string> tags, string? replacement, CancellationToken cancellationToken)
    {
        var list = tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return new(0, 0, 0);
        if (replacement is not null) ArgumentException.ThrowIfNullOrWhiteSpace(replacement);
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var mediaIds = MediaIdsWithTags(list, transaction);
            var before = SnapshotTagChange(mediaIds, transaction);
            var categories = replacement is null ? [] : CategoriesOf(list, transaction);
            var (media, searches, rules) = (0, 0, 0);
            foreach (var tag in list)
            {
                var result = ChangeTagCore(tag, replacement, transaction, cancellationToken);
                media += result.MediaChanged; searches += result.SearchesChanged; rules += result.RulesChanged;
            }
            if (replacement is not null) KeepMergedNamesAsAliases(categories, replacement.Trim(), transaction);
            var undo = UndoBetween(before, SnapshotTagChange(mediaIds, transaction));
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return new(media, searches, rules, undo);
        }
    }

    /// <summary>The categories in which the tags are used on media.</summary>
    private List<(string Tag, string Category)> CategoriesOf(IEnumerable<string> tags, SqliteTransaction transaction)
    {
        var result = new List<(string Tag, string Category)>();
        foreach (var tag in tags)
        {
            using var command = Command("SELECT DISTINCT category FROM media_tags WHERE tag=$tag COLLATE EFP_ORDINAL AND category<>''", transaction, ("$tag", tag));
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add((tag, reader.GetString(0)));
        }
        return result;
    }

    /// <summary>After a merge the old names find the target as aliases, also where they had no directory entry of their own.</summary>
    private void KeepMergedNamesAsAliases(IReadOnlyList<(string Tag, string Category)> merged, string target, SqliteTransaction transaction)
    {
        if (merged.Count == 0) return;
        var preferences = ReadPreferences(transaction);
        var vocabulary = preferences.TagVocabulary.ToList();
        foreach (var (tag, category) in merged)
        {
            var index = vocabulary.FindIndex(v => Same(v.Tag, target) && Same(v.Category, category));
            var entry = index >= 0 ? vocabulary[index] : new TagVocabularyEntry(target, category);
            if ((entry.Aliases ?? []).Any(a => Same(a, tag))) continue;
            entry = entry with { Aliases = [.. entry.Aliases ?? [], tag] };
            if (index >= 0) vocabulary[index] = entry; else vocabulary.Add(entry);
        }
        SaveDocument("preferences", Guid.Empty, preferences with { TagVocabulary = vocabulary }, transaction);
    }

    /// <summary>Gives a tag used without category a category on every medium that has it.</summary>
    private int AssignCategoryCore(string tag, string category, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        using (var select = Command("SELECT DISTINCT media_id FROM media_tags WHERE tag=$tag COLLATE EFP_ORDINAL", transaction, ("$tag", tag)))
        using (var reader = select.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetString(0));
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var command = Command("SELECT document FROM media WHERE id=$id", transaction, ("$id", id));
            var item = JsonSerializer.Deserialize<MediaItem>((string)command.ExecuteScalar()!, Json)!;
            var categories = item.TagCategories.ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.OrdinalIgnoreCase);
            categories[category] = categories.GetValueOrDefault(category, []).Append(tag).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            UpsertCore(item with { TagCategories = categories }, transaction);
        }
        return ids.Count;
    }

    /// <summary>The studio and the networks above it, nearest first; stops at a loop left by older data.</summary>
    internal static List<string> StudioChain(IEnumerable<TagVocabularyEntry> vocabulary, string studio)
    {
        var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in vocabulary) if (entry.Profile?.ParentStudio is { } parent) parents.TryAdd(entry.Tag, parent);
        var chain = new List<string> { studio };
        while (parents.TryGetValue(chain[^1], out var next) && !chain.Contains(next, StringComparer.OrdinalIgnoreCase)) chain.Add(next);
        return chain;
    }

    /// <summary>The studio and every studio that belongs to it at any depth.</summary>
    internal static HashSet<string> StudioTree(IEnumerable<TagVocabularyEntry> vocabulary, string studio)
    {
        var children = vocabulary.Where(e => e.Profile?.ParentStudio is not null).ToLookup(e => e.Profile!.ParentStudio!, e => e.Tag, StringComparer.OrdinalIgnoreCase);
        var tree = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { studio.Trim() };
        var open = new Queue<string>(tree);
        while (open.TryDequeue(out var current)) foreach (var child in children[current]) if (tree.Add(child)) open.Enqueue(child);
        return tree;
    }

    private static readonly IEqualityComparer<(string Tag, string Category)> PairComparer = new IgnoreCasePairComparer();
    private sealed class IgnoreCasePairComparer : IEqualityComparer<(string Tag, string Category)>
    {
        public bool Equals((string Tag, string Category) x, (string Tag, string Category) y) => Same(x.Tag, y.Tag) && Same(x.Category, y.Category);
        public int GetHashCode((string Tag, string Category) value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Tag), StringComparer.OrdinalIgnoreCase.GetHashCode(value.Category));
    }
}
