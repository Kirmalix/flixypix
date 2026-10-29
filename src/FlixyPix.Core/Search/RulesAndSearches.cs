using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
    public IReadOnlyList<SavedSearch> GetSavedSearches()
    {
        lock (gate) { ThrowIfDisposed(); return ReadDocuments<SavedSearch>("saved_searches").OrderBy(x => x.Order).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(); }
    }

    /// <summary>The view a saved search shows; searches saved before views existed are converted from their fields.</summary>
    public static ViewState StateOf(SavedSearch search)
    {
        ArgumentNullException.ThrowIfNull(search);
        return search.State ?? new ViewState
        {
            View = search.IsFavorite == true ? "favorites" : search.WatchLater == true ? "watchlater" : "all",
            SearchText = search.Text, Tag = search.Tag, TagCategory = search.TagCategory, Category = search.Category,
            MinimumStars = Math.Clamp(search.MinimumRating / 2, 0, 5), Expression = search.Expression,
        };
    }

    /// <summary>Hits of a saved search now, and how many of them were imported after it was last opened.</summary>
    public (long Total, long New) CountSavedSearch(SavedSearch search, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var query = StateOf(search).ToQuery(now) with { Limit = 1 };
        var total = QueryPage(query, cancellationToken).TotalCount;
        if (search.LastOpenedAt is not { } opened || total == 0) return (total, 0);
        var since = SearchExpression.Where(new() { Field = SearchField.AddedAt, Comparison = SearchComparison.GreaterThan, Date = opened });
        var fresh = QueryPage(query with { Expression = query.Expression is null ? since : SearchExpression.All(query.Expression, since) }, cancellationToken).TotalCount;
        return (total, fresh);
    }

    /// <summary>Notes the opening without dropping the browsing caches.</summary>
    public void MarkSavedSearchOpened(Guid id, DateTimeOffset when)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var search = ReadDocuments<SavedSearch>("saved_searches").FirstOrDefault(s => s.Id == id);
            if (search is null) return;
            var before = ChangeStamp();
            using (var transaction = connection.BeginTransaction())
            {
                SaveDocument("saved_searches", id, search with { LastOpenedAt = when }, transaction);
                transaction.Commit();
            }
            CarryCachesAcross(before, []);
        }
    }

    public SavedSearch UpsertSavedSearch(SavedSearch search)
    {
        ValidateSavedSearch(search);
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            EnsureSetParent(search.ParentId, null, transaction);
            SaveDocument("saved_searches", search.Id, search, transaction);
            transaction.Commit();
            return search;
        }
    }

    public void DeleteSavedSearch(Guid id) => DeleteDocument("saved_searches", id);

    public IReadOnlyList<MediaItem> SearchSaved(Guid id)
    {
        lock (gate)
        {
            var search = GetSavedSearches().FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException($"Saved search {id} does not exist.");
            var query = StateOf(search).ToQuery(DateTimeOffset.Now);
            // Older searches keep their rating on the 0–10 scale, which may be a half star.
            if (search.State is null) query = query with { MinimumRating = search.MinimumRating };
            return QueryItems(query, null, CancellationToken.None);
        }
    }

    public IReadOnlyList<FilenameTagRule> GetFilenameTagRules()
    {
        lock (gate) { ThrowIfDisposed(); return ReadDocuments<FilenameTagRule>("filename_rules").OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(); }
    }

    public FilenameTagRule UpsertFilenameTagRule(FilenameTagRule rule)
    {
        var validated = ValidateFilenameRule(rule);
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            SaveDocument("filename_rules", validated.Id, validated, transaction);
            transaction.Commit();
            return validated;
        }
    }

    public void DeleteFilenameTagRule(Guid id) => DeleteDocument("filename_rules", id);

    /// <summary>Explicitly applies enabled substring rules, adding tags without removing manually assigned tags.</summary>
    public int ApplyFilenameTagRules(Guid? mediaId = null, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var rules = ReadDocuments<FilenameTagRule>("filename_rules", transaction).Where(x => x.Enabled).Select(ValidateFilenameRule).ToArray();
            var changed = 0;
            foreach (var item in ReadDocuments<MediaItem>("media", transaction).Where(x => mediaId is null || x.Id == mediaId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tags = item.Tags.Concat(rules.SelectMany(rule => EvaluateTagRuleCore(item,rule)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (tags.Count == item.Tags.Count) continue;
                UpsertCore(item with { Tags = tags }, transaction);
                changed++;
            }
            transaction.Commit();
            return changed;
        }
    }

    private static void ValidateSavedSearch(SavedSearch search)
    {
        ArgumentNullException.ThrowIfNull(search);
        if (search.Expression is not null) ValidateSearchExpression(search.Expression);
        if (search.State is { } state)
        {
            if (state.Filter is null || state.RequiredTags is null || state.ExcludedTags is null || state.Selection is null) throw new ArgumentException("Invalid saved view.", nameof(search));
            if (state.ToQuery(DateTimeOffset.Now).Expression is { } combined) ValidateSearchExpression(combined);
        }
        if (search.Id == Guid.Empty || string.IsNullOrWhiteSpace(search.Name) || search.MinimumRating is < 0 or > 10)
            throw new ArgumentException("Saved search needs a name, id and minimum rating from 0 to 10.", nameof(search));
    }

    private static FilenameTagRule ValidateFilenameRule(FilenameTagRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Id == Guid.Empty || string.IsNullOrWhiteSpace(rule.Name) || rule.Tags is null || rule.Groups is null || rule.Extra is null ||
            rule.Groups.Count == 0 && string.IsNullOrWhiteSpace(rule.FilenameContains))
            throw new ArgumentException("Filename rule needs a name, id, filename substring and tags.", nameof(rule));
        var tags = rule.Tags.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (rule.Groups.Count == 0 && tags.Count == 0) throw new ArgumentException("Filename rule needs at least one nonempty tag.", nameof(rule));
        var groups = ValidateRuleGroups(rule.Groups);
        return rule with { Name = rule.Name.Trim(), Tags = tags, Groups = groups, Extra = new(rule.Extra) };
    }

    private void SaveDocument<T>(string table, Guid id, T value, SqliteTransaction transaction)
    {
        if(KeepLegacyExisting(table,id.ToString(),JsonSerializer.Serialize(value,Json),transaction))return;
        using var command = Command($"INSERT INTO {table}(id,document) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET document=excluded.document",
            transaction, ("$id", id.ToString()), ("$json", JsonSerializer.Serialize(value, Json)));
        command.ExecuteNonQuery();
    }
}
